import std;
import Dreamsleeve.Client.Diagnostics.PhantomRecorder;
import Dreamsleeve.Client.ProtocolCodec;
import Dreamsleeve.Client.Utils;

namespace Dreamsleeve::Client::Diagnostics
{
  namespace
  {

    enum class Kind : std::uint32_t
    {
      Model = 1,
      Sample,
      Encoded,
      PosePacket,
      MovementPacket,
      CaptureFailure
    };

    struct Writer
    {
      P::Bytes bytes;

      template <class T>
      void Put(T value)
      {
        const auto data = std::bit_cast<std::array<std::uint8_t, sizeof(T)>>(value);
        bytes.insert(bytes.end(), data.begin(), data.end());
      }

      void Data(std::span<const std::uint8_t> data)
      {
        bytes.insert(bytes.end(), data.begin(), data.end());
      }

      void Vec(P::Vec3 value)
      {
        Put(value.x);
        Put(value.y);
        Put(value.z);
      }

      void Move(const Movement& m)
      {
        Put(m.context);
        Put(m.sequence);
        Put(m.sampledAtUs);
        Vec(m.position);
        Vec(m.euler);
      }

      void Pose(const P::Snapshot& p)
      {
        Put(p.generation.value);
        Put(p.sequence.value);
        Put(p.context);
        Put(p.sampledAtUs);
        Vec(p.origin);
        Put(static_cast<std::uint32_t>(p.channels.size()));
        Put(static_cast<std::uint32_t>(p.bounds.size()));
        for (const auto& c : p.channels)
        {
          Vec(c.world.position);
          for (auto x : {c.world.rotation.x, c.world.rotation.y, c.world.rotation.z, c.world.rotation.w})
            Put(x);
          Put(c.world.scale);
          Put<std::uint8_t>(c.hidden);
        }
        for (const auto& b : p.bounds)
        {
          Vec(b.center);
          Put(b.radius);
        }
      }
    };

    struct SampleJob
    {
      std::shared_ptr<const P::ValidatedAsset> asset;
      std::shared_ptr<const P::Snapshot>       pose;
      Movement                                 actor;
      double                                   captureMs;
      bool                                     firstPerson;
    };

    struct RecordJob
    {
      Kind     kind;
      P::Bytes bytes;
    };

    struct Job
    {
      std::uint64_t                      charge;
      std::variant<SampleJob, RecordJob> value;
    };

    std::uint64_t Micros()
    {
      return std::chrono::duration_cast<std::chrono::microseconds>(Clock::now().time_since_epoch()).count();
    }

    std::string Quoted(std::string_view text)
    {
      std::string out = "\"";
      for (const unsigned char c : text)
        if (c == '"' || c == '\\')
        {
          out += '\\';
          out += static_cast<char>(c);
        }
        else if (c < 32)
          out += std::format("\\u{:04x}", c);
        else
          out += static_cast<char>(c);
      return out + '"';
    }

  }

  struct Recorder::State
  {
    Budget                  budget;
    mutable std::mutex      mutex;
    std::condition_variable wake;
    std::atomic<bool>       active{};
    Status                  status;
    std::deque<Job>         queue;

    struct RetainedAsset
    {
      std::uint64_t bytes;
      std::size_t   jobs;
    };

    // Jobs share immutable model storage, including the job currently being
    // written. Charge that storage once, even across wrapper/generation copies.
    std::unordered_map<const P::Asset*, RetainedAsset> retainedAssets;
    std::filesystem::path                              root;
    std::uint32_t                                      scenario{}, seconds{}, rate{};
    Clock::time_point                                  requestedAt{}, firstAt{}, lastAt{};
    bool                                               requested{}, shuttingDown{};
    std::jthread                                       thread;

    explicit State(Budget value) : budget(value), thread([this] { Run(); }) {}

    void StopLocked(std::string_view reason)
    {
      active.store(false);
      status.phase  = Phase::Saving;
      status.reason = reason;
      wake.notify_one();
    }

    bool Admit(std::uint64_t charge, const P::ValidatedAsset* asset = nullptr)
    {
      if (!active.load()) return false;
      const bool retain = asset && !retainedAssets.contains(&asset->Value());
      if (retain) charge += asset->MemoryBytes();
      if (queue.size() >= budget.queueJobs || charge > budget.queueBytes - std::min(status.queuedBytes, budget.queueBytes))
      {
        ++status.dropped;
        // Backpressure drops this observation, not the whole recording. Only
        // an individually inadmissible job can never recover by draining.
        if (!budget.queueJobs || charge > budget.queueBytes) StopLocked("queue-limit");
        return false;
      }
      status.queuedBytes += charge;
      if (asset)
      {
        auto [entry, inserted] = retainedAssets.try_emplace(&asset->Value(), asset->MemoryBytes(), 0);
        ++entry->second.jobs;
      }
      return true;
    }

    void Release(const Job& job)
    {
      status.queuedBytes -= job.charge;
      if (const auto* sample = std::get_if<SampleJob>(&job.value))
      {
        const auto entry = retainedAssets.find(&sample->asset->Value());
        if (--entry->second.jobs == 0)
        {
          status.queuedBytes -= entry->second.bytes;
          retainedAssets.erase(entry);
        }
      }
    }

    void ClearQueue()
    {
      queue.clear();
      retainedAssets.clear();
      status.queuedBytes = 0;
    }

    void Enqueue(RecordJob job)
    {
      std::lock_guard lock(mutex);
      const auto      charge = job.bytes.capacity() + sizeof(Job);
      if (!Admit(charge)) return;
      queue.push_back({charge, std::move(job)});
      wake.notify_one();
    }

    bool Due(Clock::time_point now) const
    {
      return firstAt == Clock::time_point{} ? now - requestedAt >= std::chrono::seconds(60)
                                            : now - firstAt >= std::chrono::seconds(seconds);
    }

    bool HasSpace(std::uint64_t bytes) const
    {
      // This is real free space on the destination volume, never a quota on
      // previous captures. Only the writer queries the filesystem.
      const auto available = std::filesystem::space(root).available;
      return available >= budget.freeReserveBytes && bytes <= available - budget.freeReserveBytes;
    }

    void RunSession()
    {
      std::filesystem::create_directories(root);
      if (!HasSpace(20)) throw std::runtime_error("disk-space-low");
      const auto stamp = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
      std::filesystem::path directory;
      for (std::uint32_t i = 0; i < 32; ++i)
      {
        directory = root / std::format("{}-{}-{}", stamp, Scenarios[scenario], i);
        if (std::filesystem::create_directory(directory)) break;
        if (i == 31) throw std::runtime_error("directory-collision");
      }
      {
        std::lock_guard lock(mutex);
        status.directory = directory.string();
      }
      const auto    partial = directory / "capture.phdiag.partial";
      std::ofstream output(partial, std::ios::binary);
      output.exceptions(std::ios::badbit | std::ios::failbit);
      Writer header;
      header.Data(std::array<std::uint8_t, 8>{'D', 'L', 'P', 'D', 'I', 'A', 'G', '2'});
      header.Put<std::uint32_t>(2);
      header.Put(Wire::Version);
      header.Put(P::AssetVersion);
      std::uint64_t written = header.bytes.size();
      output.write(reinterpret_cast<const char*>(header.bytes.data()), header.bytes.size());
      std::uint32_t                     records = 0;
      std::unordered_set<std::uint64_t> models;
      std::vector<double>               captures, encodes;
      std::optional<std::uint64_t>      firstSampleUs;
      auto                              write = [&](Kind kind, const P::Bytes& bytes) {
        if (records >= budget.records || !HasSpace(bytes.size() + 8))
        {
          std::lock_guard lock(mutex);
          ++status.dropped;
          StopLocked(records >= budget.records ? "record-limit" : "disk-space-low");
          return false;
        }
        Writer h;
        h.Put(static_cast<std::uint32_t>(kind));
        h.Put(static_cast<std::uint32_t>(bytes.size()));
        output.write(reinterpret_cast<const char*>(h.bytes.data()), h.bytes.size());
        output.write(reinterpret_cast<const char*>(bytes.data()), bytes.size());
        written += bytes.size() + 8;
        ++records;
        return true;
      };
      for (;;)
      {
        std::optional<Job> job;
        {
          std::unique_lock lock(mutex);
          if (active && Due(Clock::now())) StopLocked(firstAt == Clock::time_point{} ? "no-samples" : "duration");
          if (queue.empty())
          {
            if (!active) break;
            wake.wait_for(lock, std::chrono::milliseconds(25));
            continue;
          }
          job = std::move(queue.front());
          queue.pop_front();
        }
        bool saved = true;
        if (auto* sample = std::get_if<SampleJob>(&job->value))
        {
          if (!models.contains(sample->pose->generation.value))
          {
            if (models.size() >= budget.models) throw std::runtime_error("model-limit");
            auto asset = P::Prepare(*sample->asset);
            if (!asset) throw std::runtime_error("model-codec");
            Writer w;
            w.Put(sample->pose->generation.value);
            w.Put(asset->rawBytes);
            w.Put(static_cast<std::uint32_t>(asset->compressed->size()));
            w.Data(asset->hash);
            w.Data(*asset->compressed);
            saved = write(Kind::Model, w.bytes);
            models.insert(sample->pose->generation.value);
          }
          if (saved)
          {
            auto raw     = P::SnapshotBytes(*sample->pose, *sample->asset, CaptureLimits());
            auto encoded = P::WriteSnapshot(*sample->pose, *sample->asset, CaptureLimits());
            if (!raw || !encoded) throw std::runtime_error("sample-codec");
            Writer original;
            original.Pose(*sample->pose);
            Writer w;
            w.Put(sample->captureMs);
            w.Put<std::uint8_t>(sample->firstPerson);
            w.Move(sample->actor);
            w.Put(static_cast<std::uint32_t>(original.bytes.size()));
            w.Put(static_cast<std::uint32_t>(raw->size()));
            w.Put(static_cast<std::uint32_t>(encoded->size()));
            w.Data(original.bytes);
            w.Data(*raw);
            w.Data(*encoded);
            saved = write(Kind::Sample, w.bytes);
            if (saved)
            {
              captures.push_back(sample->captureMs);
              std::lock_guard lock(mutex);
              ++status.samples;
              status.captureMs = sample->captureMs;
              if (!firstSampleUs) firstSampleUs = sample->pose->sampledAtUs;
              status.seconds  = sample->pose->sampledAtUs >= *firstSampleUs ? (sample->pose->sampledAtUs - *firstSampleUs) / 1000000.0 : 0;
              status.sampleHz = status.seconds > 0 ? (status.samples - 1) / status.seconds : 0;
            }
          }
        }
        else
        {
          const auto& record = std::get<RecordJob>(job->value);
          saved              = write(record.kind, record.bytes);
          if (saved)
          {
            std::lock_guard lock(mutex);
            switch (record.kind)
            {
              case Kind::Encoded: {
                ++status.encoded;
                std::array<std::uint8_t, 8> b;
                std::ranges::copy(std::span(record.bytes).subspan(32, 8), b.begin());
                status.encodeMs = std::bit_cast<double>(b);
                encodes.push_back(status.encodeMs);
                break;
              }
              case Kind::PosePacket:
                ++status.sent;
                break;
              case Kind::MovementPacket:
                ++status.movements;
                break;
              case Kind::CaptureFailure:
                ++status.errors;
                break;
              default:
                break;
            }
          }
        }
        {
          std::lock_guard lock(mutex);
          Release(*job);
          status.bytes = written;
          if (!saved)
          {
            ClearQueue();
          }
        }
      }
      output.flush();
      output.close();
      std::filesystem::rename(partial, directory / "capture.phdiag");
      auto percentile = [](std::vector<double>& values, double fraction) {
        if (values.empty()) return 0.0;
        std::ranges::sort(values);
        return values[static_cast<std::size_t>((values.size() - 1) * fraction)];
      };
      Status result;
      {
        std::lock_guard lock(mutex);
        result = status;
      }
      std::ofstream summary(directory / "summary.json");
      summary.exceptions(std::ios::badbit | std::ios::failbit);
      summary << std::format(
        "{{\n  \"format\": 2, \"protocol\": {}, \"assetVersion\": {},\n  \"scenario\": {}, \"requestedSeconds\": {}, \"requestedHz\": {},\n" "  \"samples\": {}, \"encoded\": {}, \"sent\": {}, \"movements\": {}, \"captureErrors\": {}, \"dropped\": {},\n" "  \"seconds\": {}, \"sampleHz\": {}, \"archiveBytes\": {}, \"models\": {}, \"reason\": {}, \"lastCaptureError\": {},\n" "  \"omittedGeometry\": {}, \"hiddenGeometry\": {}, \"partialSamples\": {}, \"partialDetail\": {},\n  \"captureMsP50\": {}, \"captureMsP95\": {}, \"encodeMsP50\": {}, \"encodeMsP95\": {}\n}}\n",
        Wire::Version,
        P::AssetVersion,
        Quoted(Scenarios[scenario]),
        seconds,
        rate,
        result.samples,
        result.encoded,
        result.sent,
        result.movements,
        result.errors,
        result.dropped,
        result.seconds,
        result.sampleHz,
        written,
        models.size(),
        Quoted(result.reason),
        Quoted(result.lastCaptureError),
        result.omittedGeometry,
        result.hiddenGeometry,
        result.partialSamples,
        Quoted(result.partialDetail),
        percentile(captures, .5),
        percentile(captures, .95),
        percentile(encodes, .5),
        percentile(encodes, .95));
      summary.flush();
      summary.close();
      {
        std::lock_guard lock(mutex);
        status.phase = Phase::Complete;
      }
    }

    void Run()
    {
      for (;;)
      {
        {
          std::unique_lock lock(mutex);
          wake.wait(lock, [&] { return requested || shuttingDown; });
          if (!requested && shuttingDown) return;
          requested = false;
        }
        try
        {
          RunSession();
        }
        catch (const std::exception& e)
        {
          std::lock_guard lock(mutex);
          active.store(false);
          status.phase  = Phase::Failed;
          status.reason = e.what();
          ++status.errors;
          ClearQueue();
        }
      }
    }
  };

  Recorder::Recorder(Budget budget) : state(std::make_unique<State>(budget)) {}

  Recorder::~Recorder()
  {
    Shutdown();
  }

  bool Recorder::Start(std::filesystem::path directory, std::uint32_t scenario, std::uint32_t seconds, std::uint32_t rate)
  {
    if (directory.empty() || scenario >= Scenarios.size() || (seconds != 15 && seconds != 30) || !rate || rate > 50) return false;
    std::lock_guard lock(state->mutex);
    if (state->shuttingDown || state->status.phase == Phase::Recording || state->status.phase == Phase::Saving) return false;
    state->root         = std::move(directory);
    state->scenario     = scenario;
    state->seconds      = seconds;
    state->rate         = rate;
    state->status       = {};
    state->status.phase = Phase::Recording;
    state->requestedAt  = Clock::now();
    state->firstAt = state->lastAt = {};
    state->requested               = true;
    state->active.store(true);
    state->wake.notify_one();
    return true;
  }

  bool Recorder::Active() const noexcept
  {
    return state->active.load();
  }

  Status Recorder::Read() const
  {
    std::lock_guard lock(state->mutex);
    return state->status;
  }

  void Recorder::Stop(std::string_view reason)
  {
    std::lock_guard lock(state->mutex);
    if (state->active) state->StopLocked(reason);
  }

  void Recorder::Shutdown()
  {
    if (!state->thread.joinable()) return;
    {
      std::lock_guard lock(state->mutex);
      if (state->active) state->StopLocked("shutdown");
      state->shuttingDown = true;
    }
    state->wake.notify_one();
    state->thread.join();
  }

  void Recorder::Sample(
    std::shared_ptr<const P::ValidatedAsset> asset,
    std::shared_ptr<const P::Snapshot>       pose,
    Movement                                 actor,
    double                                   captureMs,
    bool                                     firstPerson)
  {
    if (!Active() || !asset || !pose) return;
    const auto now = Clock::now();
    auto       charge =
      sizeof(Job) + sizeof(P::Snapshot) + pose->channels.capacity() * sizeof(P::Channel) + pose->bounds.capacity() * sizeof(P::Bound);
    std::lock_guard lock(state->mutex);
    if (!state->active.load()) return;
    if (state->Due(now))
    {
      state->StopLocked("duration");
      return;
    }
    if (!state->Admit(charge, asset.get())) return;
    if (state->status.omittedGeometry || state->status.hiddenGeometry) ++state->status.partialSamples;
    if (state->firstAt == Clock::time_point{}) state->firstAt = now;
    state->lastAt = now;
    state->queue.push_back({
        charge,
        SampleJob{std::move(asset), std::move(pose), actor, captureMs, firstPerson}
    });
    state->wake.notify_one();
  }

  void Recorder::Encoded(const P::Snapshot& p, double milliseconds, std::size_t bytes)
  {
    if (!Active()) return;
    Writer w;
    w.Put(p.generation.value);
    w.Put(p.sequence.value);
    w.Put(p.context);
    w.Put(p.sampledAtUs);
    w.Put(milliseconds);
    w.Put<std::uint64_t>(bytes);
    state->Enqueue({Kind::Encoded, std::move(w.bytes)});
  }

  void Recorder::Sent(std::span<const std::uint8_t> packet)
  {
    if (!Active() || packet.size() > 2ULL * P::Limits{}.compressedPoseBytes + 1024) return;
    Writer w;
    w.Put(Micros());
    w.Data(packet);
    state->Enqueue({Kind::PosePacket, std::move(w.bytes)});
  }

  void Recorder::MovementSent(Movement movement, std::span<const std::uint8_t> packet)
  {
    if (!Active()) return;
    Writer w;
    w.Put(Micros());
    w.Move(movement);
    w.Put<std::uint64_t>(packet.size());
    w.Data(packet);
    state->Enqueue({Kind::MovementPacket, std::move(w.bytes)});
  }

  void Recorder::Partial(std::uint32_t omitted, std::uint32_t hidden, std::string_view detail)
  {
    if (!Active()) return;
    std::lock_guard lock(state->mutex);
    if (!state->active) return;
    state->status.omittedGeometry = omitted;
    state->status.hiddenGeometry  = hidden;
    state->status.partialDetail   = Dreamsleeve::Utils::Text::ClipBytes(detail, 512);
  }

  void Recorder::Failed(const P::Error& failure)
  {
    if (!Active()) return;
    {
      std::lock_guard lock(state->mutex);
      if (!state->active) return;
      state->status.lastCaptureError = Dreamsleeve::Utils::Text::ClipBytes(failure.field, 512);
    }
    Writer w;
    w.Put(Micros());
    w.Put(static_cast<std::uint32_t>(failure.reason));
    state->Enqueue({Kind::CaptureFailure, std::move(w.bytes)});
  }

  Recorder& Phantoms()
  {
    static Recorder recorder;
    return recorder;
  }

}
