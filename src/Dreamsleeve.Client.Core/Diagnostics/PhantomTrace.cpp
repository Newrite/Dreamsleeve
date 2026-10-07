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
      std::size_t           limit, bytes{};
      std::uint64_t         part{};

      void Open()
      {
        if (file.is_open()) file.close();
        file.clear();
        file.exceptions(std::ios::failbit | std::ios::badbit);
        file.open(directory / std::format("trace-{:06}.jsonl", part++), std::ios::binary | std::ios::out);
        bytes = 0;
      }

      void sink_it_(const spdlog::details::log_msg& message) override
      {
        spdlog::memory_buf_t text;
        formatter_->format(message, text);
        try
        {
          if (!file.is_open() || (bytes && bytes + text.size() > limit)) Open();
          file.write(text.data(), static_cast<std::streamsize>(text.size()));
          bytes += text.size();
        }
        catch (...)
        {
          // Retry in a new part when the disk becomes writable; never overwrite a partial part.
          try
          {
            if (file.is_open()) file.close();
          }
          catch (...)
          {}
          throw;
        }
      }

      void flush_() override
      {
        if (file.is_open()) file.flush();
      }

  public:

      SessionSink(std::filesystem::path path, std::size_t partBytes) : directory(std::move(path)), limit(partBytes) {}
    };

    struct Aggregate
    {
      std::uint64_t                 count{};
      double                        sum{}, maximum{};
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
        "native_copy",
        "native_dispose",
        "model_prepare",
        "asset_dump",
        "asset_decode",
        "pose_encode",
        "pose_decode"
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

    void WriteFailed(Writer& writer) noexcept
    {
      const auto count = ++writer.errors;
      if ((count & (count - 1)) == 0) try
        {
          spdlog::warn("Phantom trace incomplete: {} write failures", count);
        }
        catch (...)
        {}
    }

  }

  bool Enabled() noexcept
  {
    return bool(current.load());
  }

  bool Start(const std::filesystem::path& directory, std::size_t partBytes) noexcept
  {
    try
    {
      Stop();
      {
        std::lock_guard lock(measurementsMutex);
        measurements = {};
      }
      if (partBytes < 1024) return false;
      const auto stamp = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
      const auto session = directory / std::format("{}-{}", stamp, GetCurrentProcessId());
      if (!std::filesystem::create_directories(session)) return false;
      auto writer       = std::make_shared<Writer>();
      writer->directory = session;
      auto sink         = std::make_shared<SessionSink>(session, partBytes);
      // At most 256 bounded packet records (<=34 KiB each), one file owner.
      writer->pool = std::make_shared<spdlog::details::thread_pool>(256, 1);
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
      return true;
    }
    catch (...)
    {
      Stop();
      return false;
    }
  }

  void Stop() noexcept
  {
    // Producers are stopped by Runtime before this call. Keep sink/pool ownership
    // in the cleanup thread if the disk blocks; never join file IO on the game thread.
    try
    {
      auto writer = current.exchange({});
      if (!writer) return;
      auto done       = std::make_shared<std::promise<void>>();
      auto completion = done->get_future();
      std::thread([writer = std::move(writer), done]() mutable {
        try
        {
          writer->logger->flush();
          writer->pool.reset();
          writer.reset();
        }
        catch (...)
        {}
        done->set_value();
      }).detach();
      if (completion.wait_for(std::chrono::seconds(2)) != std::future_status::ready)
        spdlog::warn("Phantom trace shutdown timed out; pending dump may be incomplete");
    }
    catch (...)
    {}
  }

  void Event(std::string_view kind, std::string fields) noexcept
  {
    try
    {
      if (auto writer = current.load())
      {
        const auto utc = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
        const auto mono =
          std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
        writer->logger->info("{{\"utc_ms\":{},\"mono_us\":{},\"event\":\"{}\"{}{}}}", utc, mono, kind, fields.empty() ? "" : ",", fields);
      }
    }
    catch (...)
    {}
  }

  void Asset(std::string_view hash, std::span<const std::uint8_t> bytes) noexcept
  {
    if (!Enabled()) return;
    const auto writer = current.load();
    if (!writer) return;
    Span span(Metric::AssetDump);
    try
    {
      if (hash.size() != 64 || !std::ranges::all_of(hash, [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); }))
        throw std::runtime_error("invalid trace asset hash");
      const auto models = writer->directory / "models";
      std::filesystem::create_directories(models);
      const auto target = models / (std::string(hash) + ".zst");
      if (std::filesystem::exists(target)) return;
      auto partial  = target;
      partial      += ".partial";
      std::ofstream output;
      output.exceptions(std::ios::failbit | std::ios::badbit);
      output.open(partial, std::ios::binary);
      output.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
      output.close();
      std::filesystem::rename(partial, target);
      Event("asset", std::format("\"hash\":\"{}\",\"bytes\":{}", hash, bytes.size()));
    }
    catch (...)
    {
      WriteFailed(*writer);
    }
  }

  void Packet(bool outgoing, std::uint8_t lane, std::span<const std::uint8_t> bytes) noexcept
  {
    if (!Enabled() || (lane != static_cast<std::uint8_t>(Wire::Channel::Models) && lane != static_cast<std::uint8_t>(Wire::Channel::Poses)))
      return;  // Never auth/chat/control payloads.
    try
    {
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
    catch (...)
    {}
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

  void FlushMetrics() noexcept
  {
    const auto writer = current.load();
    if (!writer) return;
    try
    {
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
      FILETIME   created{}, exited{}, kernel{}, user{};
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
    catch (...)
    {}
  }

}
