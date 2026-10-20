export module Dreamsleeve.Client.Phantom.Worker;

import std;
import Dreamsleeve.Client.Phantom.Delta;
import Dreamsleeve.Client.Phantom.Cache;
export import Dreamsleeve.Client.Phantom.Exchange;
import Dreamsleeve.Client.Diagnostics.PhantomTrace;
#ifdef DREAMSLEEVE_DIAGNOSTICS
import Dreamsleeve.Client.Diagnostics.PhantomRecorder;
#endif

export namespace Dreamsleeve::Client::Phantom
{

  class Worker final
  {
    // Queued remote models, decode jobs waiting for a download, poses waiting
    // for decoding, and players whose last model may serve as a delta basis.
    static constexpr std::size_t MaxQueuedAssets = 8;
    static constexpr std::size_t MaxMissing      = 16;
    static constexpr std::size_t MaxQueuedPoses  = 32;
    static constexpr std::size_t MaxDeltaBases   = 512;
    // Pose decoding wakes at least this often to take the publication's work;
    // the model thread rechecks the cache budget at its own pace.
    static constexpr auto PoseWait  = std::chrono::milliseconds(5);
    static constexpr auto ModelWait = std::chrono::milliseconds(20);

    struct AssetJob
    {
      std::uint64_t                epoch;
      Wire::Offer                  offer;
      std::shared_ptr<const Bytes> bytes;
      std::optional<AssetDelta>    delta;

      // Raw and compressed buffers while decoding; a delta also holds its full basis.
      std::uint64_t MemoryBytes() const
      {
        return 2ULL * offer.asset.rawBytes + offer.asset.compressedBytes +
               (delta ? Limits{}.assetBytes + Limits{}.compressedAssetBytes : 0ULL);
      }
    };

    struct PoseJob
    {
      std::uint64_t                         epoch;
      std::uint64_t                         arrivalUs;
      Wire::RemotePose                      pose;
      std::shared_ptr<const ValidatedAsset> asset;
    };

    struct ModelJob
    {
      std::uint64_t                        epoch{};
      std::uint64_t                        revision{};
      Generation                           generation;
      ValidatedAsset                       asset;
      std::shared_ptr<const PreparedAsset> basis;
    };

    Exchange&                                                  exchange;
    Cache                                                      cache;
    std::mutex                                                 mutex;
    std::condition_variable                                    wake;
    std::deque<AssetJob>                                       assets;
    std::map<std::pair<std::uint64_t, std::uint64_t>, PoseJob> poses;

    struct Missing
    {
      std::uint64_t         epoch;
      Wire::Offer           offer;
      std::optional<Digest> baseHash;
    };

    std::map<std::uint64_t, Wire::Descriptor>            previousAssets;  // ModelRun owner; descriptors only.
    std::vector<Missing>                                 missing;
    std::optional<ModelJob>                              model;
    std::uint64_t                                        assetInFlightBytes{};
    std::condition_variable                              modelWake;
    std::function<Result<PreparedAsset>(ValidatedAsset)> prepare;
    // Only ModelRun touches the filesystem. Exchange owns prepared generations.
    std::optional<std::uint64_t> cacheBudget;
    std::jthread                 modelThread;
    std::jthread                 thread;

    void CacheFailure(const Error& error)
    {
      exchange.Failed("Кеш моделей фантомов: " + error.field, false);
    }

    std::shared_ptr<const Bytes> Cached(const Digest& hash, std::uint32_t expectedSize = 0)
    {
      if (!exchange.Settings().diskBytes) return {};
      auto read = cache.Read(hash, expectedSize);
      if (!read)
      {
        CacheFailure(read.error());
        if (read.error().reason == Failure::InvalidFormat)
          if (auto erased = cache.Erase(hash); !erased) CacheFailure(erased.error());
        return {};  // The owner reports the failure, then requests network content.
      }
      if (!*read) return {};
      if (auto touched = cache.Touch(hash); !touched) CacheFailure(touched.error());
      return std::make_shared<const Bytes>(std::move(**read));
    }

    void Asset(AssetJob& job)
    {
      if (job.epoch != exchange.Epoch()) return;
      const auto remote = exchange.Find(job.offer.player);
      if (!remote || remote->view != job.offer.view || remote->descriptor != job.offer.asset) return;

      if (auto loaded = exchange.AssetFor(job.offer.asset))
      {
        exchange.Loaded(job.epoch, job.offer, std::move(loaded), true);
        return;
      }

      auto bytes = job.bytes ? job.bytes : Cached(job.offer.asset.hash, job.offer.asset.compressedBytes);
      if (!bytes)
      {
        Miss(job);
        return;
      }

      if (job.delta)
      {
        auto full = ApplyDelta(job, *bytes);
        if (!full)
        {
          Miss(job, false);
          return;
        }
        bytes = std::move(*full);
      }

      if (const auto digest = Hash(*bytes); !digest || *digest != job.offer.asset.hash)
      {
        Mismatched(job);
        return;
      }

      auto decoded = [&] {
        Diagnostics::Trace::Span span(Diagnostics::Trace::Metric::AssetDecode);
        return ReadAsset(*bytes, job.offer.asset.rawBytes);
      }();
      if (!decoded || decoded->Layout().requiredChannels.size() != job.offer.asset.channels)
      {
        exchange.Unavailable(job.epoch, job.offer, "Модель фантома: неверный формат");
        return;
      }
      Diagnostics::Trace::Asset(job.offer.asset.hash, *bytes);

      const bool cached = !job.bytes;
      if (job.bytes)
        if (auto saved = cache.Save(job.offer.asset.hash, *bytes, exchange.Settings().diskBytes); !saved) CacheFailure(saved.error());

      // Complete persistence before releasing the decode reservation. Otherwise
      // Game could admit a replacement while these compressed buffers are live.
      bytes.reset();
      job.bytes.reset();
      exchange.Loaded(job.epoch, job.offer, std::make_shared<const ValidatedAsset>(std::move(*decoded)), cached);
      if (previousAssets.size() >= MaxDeltaBases && !previousAssets.contains(job.offer.player))
        previousAssets.erase(previousAssets.begin());
      previousAssets.insert_or_assign(job.offer.player, job.offer.asset);
    }

    // The full compressed model from a delta over the cached basis; the delta
    // and the basis must both hash as the offer announced.
    Result<std::shared_ptr<const Bytes>> ApplyDelta(const AssetJob& job, const Bytes& patch)
    {
      Diagnostics::Trace::Span span(Diagnostics::Trace::Metric::DeltaApply);
      const auto               invalid = std::unexpected(Error{Failure::InvalidFormat, "delta.base"});
      if (const auto digest = Hash(patch); !digest || *digest != job.delta->hash) return invalid;

      const auto basis = Cached(job.delta->baseHash);
      if (!basis) return invalid;
      if (const auto digest = Hash(*basis); !digest || *digest != job.delta->baseHash) return invalid;

      auto full = Delta::Apply(*basis, patch, job.offer.asset.rawBytes);
      if (!full) return std::unexpected(full.error());
      return std::make_shared<const Bytes>(std::move(*full));
    }

    // The model does not hash as announced. A delta and a stale cache file ask
    // the network again (the file goes); a downloaded model is refused.
    void Mismatched(const AssetJob& job)
    {
      if (job.delta)
      {
        Miss(job, false);
        return;
      }
      if (!job.bytes)
      {
        CacheFailure(Error{Failure::HashMismatch, "cache.hash"});
        if (auto erased = cache.Erase(job.offer.asset.hash); !erased) CacheFailure(erased.error());
        Miss(job);
        return;
      }
      exchange.Unavailable(job.epoch, job.offer, "Модель фантома: неверная контрольная сумма");
    }

    void Miss(const AssetJob& job, bool allowDelta = true)
    {
      std::optional<Digest> baseHash;
      const auto            prior = previousAssets.find(job.offer.player);
      if (allowDelta && prior != previousAssets.end() && prior->second.hash != job.offer.asset.hash)
      {
        if (exchange.Settings().diskBytes)
        {
          auto present = cache.Inspect(prior->second.hash, prior->second.compressedBytes);
          if (!present)
            CacheFailure(present.error());
          else if (*present)
            baseHash = prior->second.hash;
        }
      }

      std::lock_guard lock(mutex);
      std::erase_if(missing, [&](const auto& value) { return value.epoch != job.epoch || value.offer.player == job.offer.player; });
      if (missing.size() < MaxMissing)
        missing.push_back({job.epoch, job.offer, baseHash});
      else
        exchange.Unavailable(job.epoch, job.offer, "Очередь загрузки моделей заполнена");
    }

    void DecodePose(const PoseJob& job)
    {
      if (job.epoch != exchange.Epoch()) return;

      auto pose = [&] {
        Dreamsleeve::Client::Diagnostics::Trace::Span span(Dreamsleeve::Client::Diagnostics::Trace::Metric::PoseDecode);
        return ReadSnapshot(job.pose.sample.payload, *job.asset);
      }();

      if (
        pose && pose->generation == job.pose.sample.generation && pose->context == job.pose.sample.context &&
        pose->sequence == job.pose.sample.sequence && pose->sampledAtUs == job.pose.sample.sampledAtUs)
        exchange.Pose(job.epoch, job.pose, std::make_shared<const Snapshot>(std::move(*pose)), job.arrivalUs);
      else
        exchange.Failed("Поза фантома: неверный формат");
    }

    void Run(std::stop_token stop)
    {
      while (!stop.stop_requested())
      {
        auto work = exchange.TakeWork();
        if (work.capture)
        {
          std::lock_guard lock(mutex);
          model.emplace(
            ModelJob{
                work.epoch,
                work.localRevision,
                work.capture->first,
                std::move(work.capture->second),
                work.priorAsset ? work.priorAsset : work.asset
            });
          modelWake.notify_one();
        }

        // While the next asset is compressed, keep the committed scene moving.
        if (!work.asset && work.priorAsset)
        {
          work.asset      = std::move(work.priorAsset);
          work.generation = work.previousGeneration;
          work.snapshot   = std::move(work.previousSnapshot);
        }

        if (work.snapshot && work.asset && work.generation == work.snapshot->generation && work.context)
        {
          auto snapshot    = *work.snapshot;
          snapshot.context = work.context;
#ifdef DREAMSLEEVE_DIAGNOSTICS
          const auto encodingStart = std::chrono::steady_clock::now();
#endif
          auto encoded = [&] {
            Dreamsleeve::Client::Diagnostics::Trace::Span span(Dreamsleeve::Client::Diagnostics::Trace::Metric::PoseEncode);
            return WriteSnapshot(snapshot, work.asset->asset);
          }();
#ifdef DREAMSLEEVE_DIAGNOSTICS
          if (encoded)
            Diagnostics::Phantoms().Encoded(
              snapshot,
              std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - encodingStart).count(),
              encoded->size());
#endif
          if (encoded)
          {
            Wire::Pose packet{snapshot.generation, snapshot.context, snapshot.sequence, snapshot.sampledAtUs, std::move(*encoded)};
            if (
              work.priorAsset && work.previousSnapshot && work.previousGeneration == work.previousSnapshot->generation &&
              work.previousSnapshot->sampledAtUs == snapshot.sampledAtUs)
            {
              auto prior    = *work.previousSnapshot;
              prior.context = work.context;
              auto bytes    = WriteSnapshot(prior, work.priorAsset->asset);
              if (bytes)
                packet.previous = std::make_shared<const Wire::Pose>(
                  Wire::Pose{prior.generation, prior.context, prior.sequence, prior.sampledAtUs, std::move(*bytes)});
            }
            exchange.Encoded(work.epoch, work.poseRevision, std::move(packet));
          }
          else
            exchange.Failed("Не удалось подготовить позу фантома: " + encoded.error().field);
        }

        std::map<std::pair<std::uint64_t, std::uint64_t>, PoseJob> batch;
        {
          std::lock_guard lock(mutex);
          batch.swap(poses);
        }
        for (const auto& [id, job] : batch)
          DecodePose(job);

        std::unique_lock lock(mutex);
        wake.wait_for(lock, PoseWait, [&] { return stop.stop_requested() || !poses.empty(); });
      }
    }

    void ModelRun(std::stop_token stop)
    {
      while (!stop.stop_requested())
      {
        std::optional<ModelJob> capture;
        std::optional<AssetJob> remote;
        {
          std::unique_lock lock(mutex);
          modelWake.wait_for(lock, ModelWait, [&] { return stop.stop_requested() || model || !assets.empty(); });
          if (stop.stop_requested()) break;
          capture = std::exchange(model, {});
          if (!capture && !assets.empty())
          {
            remote = std::move(assets.front());
            assets.pop_front();
            assetInFlightBytes = remote->MemoryBytes();
          }
        }

        const auto settings = exchange.Settings();
        if (settings.diskBytes && cacheBudget != settings.diskBytes)
        {
          cacheBudget = settings.diskBytes;
          if (auto trimmed = cache.Trim(*cacheBudget); !trimmed) CacheFailure(trimmed.error());
        }

        if (capture && capture->epoch == exchange.Epoch() && exchange.Capturing(capture->generation))
        {
          auto result = [&] {
            Dreamsleeve::Client::Diagnostics::Trace::Span span(Dreamsleeve::Client::Diagnostics::Trace::Metric::ModelPrepare);
            return prepare(std::move(capture->asset));
          }();
          if (result && capture->basis && capture->basis->hash != result->hash)
          {
            Diagnostics::Trace::Span deltaSpan(Diagnostics::Trace::Metric::DeltaEncode);
            auto                     bytes = Delta::Create(*capture->basis->compressed, *result->compressed);
            if (bytes && bytes->size() < result->compressed->size())
            {
              auto hash = Hash(*bytes);
              if (hash)
                result->delta = PreparedDelta{
                    AssetDelta{capture->basis->hash, *hash, static_cast<std::uint32_t>(bytes->size())},
                    std::make_shared<const Bytes>(std::move(*bytes))
                };
            }
          }
          if (result)
          {
            Diagnostics::Trace::Asset(result->hash, *result->compressed);
            if (result->delta)
              Diagnostics::Trace::Event("delta_prepared", [&] {
                return std::format(
                  "\"generation\":{},\"full_bytes\":{},\"delta_bytes\":{}",
                  capture->generation.value,
                  result->compressed->size(),
                  result->delta->bytes->size());
              });
          }
          if (result)
            exchange.Prepared(
              capture->epoch,
              capture->revision,
              {capture->generation, std::make_shared<const PreparedAsset>(std::move(*result))});
          else
            exchange.PreparationFailed(capture->epoch, capture->revision, "Не удалось подготовить модель фантома: " + result.error().field);
        }

        if (remote)
        {
          Asset(*remote);
          std::lock_guard lock(mutex);
          assetInFlightBytes = 0;
        }
        wake.notify_one();
      }
    }

public:

    Worker(
      Exchange&                                            owner,
      std::filesystem::path                                cacheDirectory,
      std::function<Result<PreparedAsset>(ValidatedAsset)> prepareModel = [](ValidatedAsset asset) { return Prepare(std::move(asset)); })
        : exchange(owner),
          cache(std::move(cacheDirectory)),
          prepare(std::move(prepareModel)),
          modelThread([this](std::stop_token stop) { ModelRun(stop); }),
          thread([this](std::stop_token stop) { Run(stop); })
    {}

    ~Worker()
    {
      thread.request_stop();
      modelThread.request_stop();
      wake.notify_all();
      modelWake.notify_all();
      thread.join();
      modelThread.join();
    }

    bool Queue(Wire::Offer offer, std::shared_ptr<const Bytes> bytes = {}, std::optional<AssetDelta> delta = {})
    {
      const auto      epoch  = exchange.Epoch();
      const auto      budget = exchange.Settings().memoryBytes;
      AssetJob        job{epoch, std::move(offer), std::move(bytes), delta};
      std::lock_guard lock(mutex);
      std::erase_if(assets, [&](const auto& queued) { return queued.epoch != epoch || queued.offer.player == job.offer.player; });
      std::uint64_t queued = assetInFlightBytes + job.MemoryBytes();
      for (const auto& waiting : assets)
        queued += waiting.MemoryBytes();
      if (assets.size() >= MaxQueuedAssets || queued > budget) return false;

      assets.push_back(std::move(job));
      modelWake.notify_one();
      return true;
    }

    void Queue(Wire::RemotePose pose, std::shared_ptr<const ValidatedAsset> asset, std::uint64_t arrivalUs)
    {
      std::lock_guard lock(mutex);
      pose.sample.previous.reset();  // Each queue slot owns exactly one generation.
      const auto key = std::pair{pose.player, pose.sample.generation.value};
      if (poses.size() >= MaxQueuedPoses && !poses.contains(key)) return;
      poses.insert_or_assign(key, PoseJob{exchange.Epoch(), arrivalUs, std::move(pose), std::move(asset)});
      wake.notify_one();
    }

    auto TakeMissing()
    {
      std::lock_guard      lock(mutex);
      std::vector<Missing> result;
      for (auto& value : missing)
        if (value.epoch == exchange.Epoch()) result.push_back(std::move(value));
      missing.clear();
      return result;
    }
  };

}
