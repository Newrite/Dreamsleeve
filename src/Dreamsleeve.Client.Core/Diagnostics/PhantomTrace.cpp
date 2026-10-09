#include <process.h>
#include <cerrno>
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <Psapi.h>
#include <spdlog/spdlog.h>
#include <spdlog/async_logger.h>
#include <spdlog/details/thread_pool.h>
#include <spdlog/sinks/base_sink.h>
import std;
import Dreamsleeve.Client.Diagnostics.PhantomTrace;
import Dreamsleeve.Client.ProtocolCodec;

namespace Dreamsleeve::Client::Diagnostics::Trace
{
  namespace
  {

    // Segment a session without deleting its beginning or its model packets.
    class SessionSink final : public spdlog::sinks::base_sink<std::mutex>
    {
      std::filesystem::path directory;
      std::ofstream         file;
      std::size_t           limit;
      std::size_t           bytes{};
      std::uint64_t         part{};
      std::function<void()> failed;

      void Open()
      {
        if (file.is_open())
        {
          file.close();
          if (!file) failed();
        }
        file.clear();

        file.open(directory / std::format("trace-{:06}.jsonl", part++), std::ios::binary | std::ios::out);
        bytes = 0;
      }

      void sink_it_(const spdlog::details::log_msg& message) override
      {
        spdlog::memory_buf_t text;
        formatter_->format(message, text);
        if (!file.is_open() || (bytes && bytes + text.size() > limit)) Open();
        file.write(text.data(), static_cast<std::streamsize>(text.size()));
        if (!file)
        {
          // Retry in a new part, retaining the incomplete part for diagnosis.
          if (file.is_open()) file.close();
          failed();
          return;
        }
        bytes += text.size();
      }

      void flush_() override
      {
        if (file.is_open())
        {
          file.flush();
          if (!file)
          {
            file.close();
            failed();
          }
        }
      }

  public:

      SessionSink(std::filesystem::path path, std::size_t partBytes, std::function<void()> onFailure)
          : directory(std::move(path)),
            limit(partBytes),
            failed(std::move(onFailure))
      {}
    };

    struct Aggregate
    {
      std::uint64_t                 count{};
      double                        sum{};
      double                        maximum{};
      std::array<std::uint64_t, 10> buckets{};
    };

    constexpr std::array limits{0.1, 0.25, 0.5, 1.0, 2.0, 4.0, 8.0, 16.0, 33.0};
    constexpr std::array names{
        "frame_interval",
        "game_tick",
        "topology",
        "clone",
        "normalize",
        "validate_asset",
        "native_load",
        "scene_prepare",
        "capture_pose",
        "apply_pose",
        "native_audit",
        "native_save",
        "native_reserve",
        "native_dispose",
        "model_prepare",
        "asset_dump",
        "asset_decode",
        "pose_encode",
        "pose_decode",
        "delta_encode",
        "delta_apply"
    };
    static_assert(names.size() == static_cast<std::size_t>(Metric::Count));

    struct Writer
    {
      std::filesystem::path                         directory;
      std::shared_ptr<spdlog::details::thread_pool> pool;
      std::shared_ptr<spdlog::async_logger>         logger;
      std::atomic<std::uint64_t>                    errors{};
    };

    std::mutex                           measurementsMutex;
    std::array<Aggregate, names.size()>  measurements;
    std::atomic<std::shared_ptr<Writer>> current;

    std::mutex               lifecycle;
    std::shared_future<void> pending;
    bool                     failNextCleanupLaunch{};
    std::shared_future<void> nextCleanupGate;

    void WriteFailed(Writer& writer)
    {
      const auto count = ++writer.errors;
      if ((count & (count - 1)) == 0) spdlog::warn("Phantom trace incomplete: {} write failures", count);
    }

    struct CleanupJob
    {
      std::shared_ptr<Writer>             writer;
      std::shared_ptr<std::promise<void>> done;
      std::shared_future<void>            accepted;
      std::shared_future<void>            gate;
    };

    void __cdecl Cleanup(void* context)
    {
      std::unique_ptr<CleanupJob> job(static_cast<CleanupJob*>(context));
      // Launch may finish before its caller: wait until the caller releases its
      // writer copy so logger/pool destruction stays on this cleanup thread.
      job->accepted.wait();
      if (job->gate.valid()) job->gate.wait();

      job->writer->logger->flush();
      job->writer->logger.reset();
      job->writer->pool.reset();
      job->writer.reset();

      auto done = std::move(job->done);
      job.reset();
      done->set_value();
    }

    // Called only while lifecycle is held; current has one lifecycle writer.
    std::expected<StopOutcome, TraceError> StopOwned()
    {
      if (pending.valid())
      {
        if (pending.wait_for(std::chrono::seconds(0)) != std::future_status::ready) return StopOutcome::CleanupPending;
        pending = {};
      }

      auto writer = current.load();
      if (!writer) return StopOutcome::Stopped;

      // Allocate every handoff resource before detaching the published owner.
      auto               done       = std::make_shared<std::promise<void>>();
      auto               completion = done->get_future().share();
      std::promise<void> accepted;
      auto job = std::make_unique<CleanupJob>(writer, done, accepted.get_future().share(), std::exchange(nextCleanupGate, {}));
      current.store({});
      auto*      context = job.release();
      const bool refuse  = std::exchange(failNextCleanupLaunch, false);

      // _beginthread returns -1 on failure and automatically closes its handle
      // when Cleanup returns. Never use/wait/close the returned handle.
      // https://learn.microsoft.com/cpp/c-runtime-library/reference/beginthread-beginthreadex
      const auto launched = refuse ? static_cast<std::uintptr_t>(-1) : _beginthread(Cleanup, 0, context);
      if (launched == static_cast<std::uintptr_t>(-1))
      {
        const auto                  error = std::error_code(refuse ? EAGAIN : errno, std::generic_category());
        std::unique_ptr<CleanupJob> refused(context);
        current.store(std::move(writer));
        return std::unexpected(TraceError{TraceFailure::CleanupLaunch, error.message()});
      }

      pending = std::move(completion);
      writer.reset();
      accepted.set_value();
      if (pending.wait_for(std::chrono::seconds(2)) != std::future_status::ready) return StopOutcome::CleanupPending;
      pending = {};
      return StopOutcome::Stopped;
    }

  }

  bool Enabled() noexcept
  {
    return bool(current.load());
  }

  std::expected<void, TraceError> Start(const std::filesystem::path& directory, std::size_t partBytes)
  {
    if (partBytes < 1024) return std::unexpected(TraceError{TraceFailure::InvalidConfiguration, "trace part must be at least 1024 bytes"});

    std::lock_guard owner(lifecycle);
    const auto      stopped = StopOwned();
    if (!stopped) return std::unexpected(stopped.error());
    if (*stopped == StopOutcome::CleanupPending)
      return std::unexpected(TraceError{TraceFailure::CleanupPending, "previous trace cleanup is still pending"});

    {
      std::lock_guard lock(measurementsMutex);
      measurements = {};
    }
    const auto stamp   = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
    const auto session = directory / std::format("{}-{}", stamp, GetCurrentProcessId());
    std::error_code error;
    if (!std::filesystem::create_directories(session, error))
      return std::unexpected(TraceError{TraceFailure::Storage, error ? error.message() : "trace session directory already exists"});
    auto writer       = std::make_shared<Writer>();
    writer->directory = session;
    auto sink         = std::make_shared<SessionSink>(session, partBytes, [weak = std::weak_ptr(writer)] {
      if (auto value = weak.lock()) WriteFailed(*value);
    });
    // At most 256 bounded packet records (<=34 KiB each), one file owner.
    try
    {
      writer->pool = std::make_shared<spdlog::details::thread_pool>(256, 1);
    }
    catch (const std::system_error& failure)
    {
      return std::unexpected(TraceError{TraceFailure::StartupLaunch, failure.what()});
    }
    writer->logger =
      std::make_shared<spdlog::async_logger>("phantom-trace", sink, writer->pool, spdlog::async_overflow_policy::overrun_oldest);
    writer->logger->set_pattern("%v");
    writer->logger->set_error_handler([weak = std::weak_ptr(writer)](const std::string&) {
      if (auto value = weak.lock()) WriteFailed(*value);
    });

    current.store(std::move(writer));
    Event(
      "start",
      std::format(
        "\"format\":1,\"protocol\":{},\"part_bytes\":{},\"retention\":\"complete-sessions-no-auto-delete\",\"histogram_ms_upper\":[0.1,0.25,0.5,1,2,4,8,16,33]",
        Wire::Version,
        partBytes));
    return {};
  }

  std::expected<StopOutcome, TraceError> Stop()
  {
    // Runtime stops producers before entering this serialized lifecycle boundary.
    std::lock_guard owner(lifecycle);
    return StopOwned();
  }

  namespace Testing
  {

    void FailNextCleanupLaunch()
    {
      std::lock_guard owner(lifecycle);
      failNextCleanupLaunch = true;
    }

    void GateNextCleanup(std::shared_future<void> gate)
    {
      std::lock_guard owner(lifecycle);
      nextCleanupGate = std::move(gate);
    }

  }

  void Event(std::string_view kind, std::string fields)
  {
    if (auto writer = current.load())
    {
      const auto utc  = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
      const auto mono = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
      writer->logger->info("{{\"utc_ms\":{},\"mono_us\":{},\"event\":\"{}\"{}{}}}", utc, mono, kind, fields.empty() ? "" : ",", fields);
    }
  }

  void Asset(std::string_view hash, std::span<const std::uint8_t> bytes)
  {
    if (!Enabled()) return;
    const auto writer = current.load();
    if (!writer) return;
    Span span(Metric::AssetDump);
    if (hash.size() != 64 || !std::ranges::all_of(hash, [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); }))
    {
      WriteFailed(*writer);
      return;
    }
    const auto      models = writer->directory / "models";
    std::error_code error;
    std::filesystem::create_directories(models, error);
    if (error)
    {
      WriteFailed(*writer);
      return;
    }
    const auto target = models / (std::string(hash) + ".zst");
    if (std::filesystem::exists(target, error))
    {
      if (!std::filesystem::is_regular_file(target, error) || error) WriteFailed(*writer);
      return;
    }
    if (error)
    {
      WriteFailed(*writer);
      return;
    }
    auto partial  = target;
    partial      += ".partial";
    auto failed   = [&] {
      WriteFailed(*writer);
      std::error_code cleanup;
      std::filesystem::remove(partial, cleanup);
      if (cleanup) WriteFailed(*writer);
    };
    std::ofstream output;

    output.open(partial, std::ios::binary);
    if (!output)
    {
      WriteFailed(*writer);
      return;
    }
    output.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    output.close();
    if (!output)
    {
      failed();
      return;
    }
    std::filesystem::rename(partial, target, error);
    if (error)
    {
      failed();
      return;
    }
    Event("asset", std::format("\"hash\":\"{}\",\"bytes\":{}", hash, bytes.size()));
  }

  void Packet(bool outgoing, std::uint8_t lane, std::span<const std::uint8_t> bytes)
  {
    if (!Enabled() || (lane != static_cast<std::uint8_t>(Wire::Channel::Models) && lane != static_cast<std::uint8_t>(Wire::Channel::Poses)))
      return;  // Never auth/chat/control payloads.
    // Slice large fragmented snapshots without unbounded logger messages.
    constexpr std::size_t             slice = 16384;
    static std::atomic<std::uint64_t> serial{};
    const auto                        id    = ++serial;
    constexpr char                    hex[] = "0123456789abcdef";
    for (std::size_t offset = 0; offset < bytes.size(); offset += slice)
    {
      const auto  n = std::min(slice, bytes.size() - offset);
      std::string data(n * 2, '0');
      for (std::size_t i = 0; i < n; ++i)
      {
        data[i * 2]     = hex[bytes[offset + i] >> 4];
        data[i * 2 + 1] = hex[bytes[offset + i] & 15];
      }
      Event(
        "packet",
        std::format(
          "\"id\":{},\"outgoing\":{},\"lane\":{},\"size\":{},\"offset\":{},\"hex\":\"{}\"",
          id,
          outgoing,
          lane,
          bytes.size(),
          offset,
          data));
    }
  }

  void Observe(Metric metric, double ms) noexcept
  {
    if (!Enabled() || !std::isfinite(ms) || ms < 0) return;
    std::lock_guard lock(measurementsMutex);
    auto&           a = measurements[static_cast<std::size_t>(metric)];
    ++a.count;
    a.sum     += ms;
    a.maximum  = std::max(a.maximum, ms);
    ++a.buckets[std::lower_bound(limits.begin(), limits.end(), ms) - limits.begin()];
  }

  void FlushMetrics()
  {
    const auto writer = current.load();
    if (!writer) return;

    std::array<Aggregate, names.size()> batch;
    {
      std::lock_guard lock(measurementsMutex);
      batch = std::exchange(measurements, {});
    }
    for (std::size_t i = 0; i < batch.size(); ++i)
      if (batch[i].count)
      {
        const auto& a = batch[i];
        std::string buckets;
        for (auto n : a.buckets)
        {
          if (!buckets.empty()) buckets += ',';
          buckets += std::to_string(n);
        }
        Event(
          "metric",
          std::format(
            "\"name\":\"{}\",\"count\":{},\"sum_ms\":{},\"max_ms\":{},\"buckets\":[{}]",
            names[i],
            a.count,
            a.sum,
            a.maximum,
            buckets));
      }

    PROCESS_MEMORY_COUNTERS_EX memory{};
    memory.cb = sizeof(memory);
    FILETIME created{};
    FILETIME exited{};
    FILETIME kernel{};
    FILETIME user{};
    const auto process = GetCurrentProcess();
    if (
      K32GetProcessMemoryInfo(process, reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory)) &&
      GetProcessTimes(process, &created, &exited, &kernel, &user))
    {
      const auto ticks = [](FILETIME value) {
        return (std::uint64_t(value.dwHighDateTime) << 32) | value.dwLowDateTime;
      };
      Event(
        "process",
        std::format(
          "\"working_bytes\":{},\"private_bytes\":{},\"page_faults_total\":{},\"cpu_100ns_total\":{}",
          memory.WorkingSetSize,
          memory.PrivateUsage,
          memory.PageFaultCount,
          ticks(kernel) + ticks(user)));
    }

    Event(
      "writer",
      std::format(
        "\"overrun_total\":{},\"queued\":{},\"write_errors_total\":{}",
        writer->pool->overrun_counter(),
        writer->pool->queue_size(),
        writer->errors.load()));
    writer->logger->flush();
  }

}
