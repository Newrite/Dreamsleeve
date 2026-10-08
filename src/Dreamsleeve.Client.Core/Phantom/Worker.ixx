export module Dreamsleeve.Client.Phantom.Worker;

import std;
import Dreamsleeve.Client.Phantom.Delta;
import Dreamsleeve.Client.Phantom.Cache;
export import Dreamsleeve.Client.Phantom.Exchange;
#ifdef DREAMSLEEVE_DIAGNOSTICS
import Dreamsleeve.Client.Diagnostics.PhantomRecorder;
import Dreamsleeve.Client.Diagnostics.PhantomTrace;
#endif

export namespace Dreamsleeve::Client::Phantom
{

  class Worker final
  {
    struct AssetJob
    {
      std::uint64_t                epoch;
      Wire::Offer                  offer;
      std::shared_ptr<const Bytes> bytes;
      std::optional<AssetDelta>    delta;
    };

    struct PoseJob
    {
      std::uint64_t                         epoch, arrivalUs;
      Wire::RemotePose                      pose;
      std::shared_ptr<const ValidatedAsset> asset;
    };

    struct ModelJob
    {
      std::uint64_t                        epoch{}, revision{};
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
    std::jthread                 modelThread, thread;

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
#ifdef DREAMSLEEVE_DIAGNOSTICS
        Diagnostics::Trace::Span deltaSpan(Diagnostics::Trace::Metric::DeltaApply);
#endif
        const auto digest     = Hash(*bytes);
        auto       basis      = Cached(job.delta->baseHash);
        const auto baseDigest = basis ? Hash(*basis) : Result<Digest>{std::unexpected(Error{Failure::InvalidFormat, "delta.base"})};
        auto       full       = basis && baseDigest && *baseDigest == job.delta->baseHash && digest && *digest == job.delta->hash
                                ? Delta::Apply(*basis, *bytes, job.offer.asset.rawBytes)
                                : Result<Bytes>{std::unexpected(Error{Failure::InvalidFormat, "delta.base"})};
        if (!full)
        {
          Miss(job, false);
          return;
        }
        bytes = std::make_shared<const Bytes>(std::move(*full));
      }
      const auto digest = Hash(*bytes);
      if (!digest || *digest != job.offer.asset.hash)
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
        return;
      }
      auto decoded = [&] {
#ifdef DREAMSLEEVE_DIAGNOSTICS
        Dreamsleeve::Client::Diagnostics::Trace::Span span(Dreamsleeve::Client::Diagnostics::Trace::Metric::AssetDecode);
#endif
        return ReadAsset(*bytes, job.offer.asset.rawBytes);
      }();
      if (!decoded || decoded->Layout().requiredChannels.size() != job.offer.asset.channels)
      {
        exchange.Unavailable(job.epoch, job.offer, "Модель фантома: неверный формат");
        return;
      }
#ifdef DREAMSLEEVE_DIAGNOSTICS
      Diagnostics::Trace::Asset(Hex(job.offer.asset.hash), *bytes);
#endif
      const bool cached = !job.bytes;
      if (job.bytes)
        if (auto saved = cache.Save(job.offer.asset.hash, *bytes, exchange.Settings().diskBytes); !saved) CacheFailure(saved.error());
      // Complete persistence before releasing the decode reservation. Otherwise
      // Game could admit a replacement while these compressed buffers are live.
      bytes.reset();
      job.bytes.reset();
      exchange.Loaded(job.epoch, job.offer, std::make_shared<const ValidatedAsset>(std::move(*decoded)), cached);
      if (previousAssets.size() >= 512 && !previousAssets.contains(job.offer.player)) previousAssets.erase(previousAssets.begin());
      previousAssets.insert_or_assign(job.offer.player, job.offer.asset);
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
      if (missing.size() < 16)
        missing.push_back({job.epoch, job.offer, baseHash});
      else
        exchange.Unavailable(job.epoch, job.offer, "Очередь загрузки моделей заполнена");
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
#ifdef DREAMSLEEVE_DIAGNOSTICS
            Dreamsleeve::Client::Diagnostics::Trace::Span span(Dreamsleeve::Client::Diagnostics::Trace::Metric::PoseEncode);
#endif
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
        {
          if (job.epoch != exchange.Epoch()) continue;
          auto pose = [&] {
#ifdef DREAMSLEEVE_DIAGNOSTICS
            Dreamsleeve::Client::Diagnostics::Trace::Span span(Dreamsleeve::Client::Diagnostics::Trace::Metric::PoseDecode);
#endif
            return ReadSnapshot(job.pose.sample.payload, *job.asset);
          }();
          if (
            pose && pose->generation == job.pose.sample.generation && pose->context == job.pose.sample.context &&
            pose->sequence == job.pose.sample.sequence && pose->sampledAtUs == job.pose.sample.sampledAtUs)
            exchange.Pose(job.epoch, job.pose, std::make_shared<const Snapshot>(std::move(*pose)), job.arrivalUs);
          else
            exchange.Failed("Поза фантома: неверный формат");
        }
        std::unique_lock lock(mutex);
        wake.wait_for(lock, std::chrono::milliseconds(5), [&] { return stop.stop_requested() || !poses.empty(); });
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
          modelWake.wait_for(lock, std::chrono::milliseconds(20), [&] { return stop.stop_requested() || model || !assets.empty(); });
          if (stop.stop_requested()) break;
          capture = std::exchange(model, {});
          if (!capture && !assets.empty())
          {
            remote = std::move(assets.front());
            assets.pop_front();
            assetInFlightBytes = 2ULL * remote->offer.asset.rawBytes + remote->offer.asset.compressedBytes +
                                 (remote->delta ? Limits{}.assetBytes + Limits{}.compressedAssetBytes : 0ULL);
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
#ifdef DREAMSLEEVE_DIAGNOSTICS
            Dreamsleeve::Client::Diagnostics::Trace::Span span(Dreamsleeve::Client::Diagnostics::Trace::Metric::ModelPrepare);
#endif
            return prepare(std::move(capture->asset));
          }();
          if (result && capture->basis && capture->basis->hash != result->hash)
          {
#ifdef DREAMSLEEVE_DIAGNOSTICS
            Diagnostics::Trace::Span deltaSpan(Diagnostics::Trace::Metric::DeltaEncode);
#endif
            auto bytes = Delta::Create(*capture->basis->compressed, *result->compressed);
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
#ifdef DREAMSLEEVE_DIAGNOSTICS
          if (result)
          {
            Diagnostics::Trace::Asset(Hex(result->hash), *result->compressed);
            if (result->delta)
              Diagnostics::Trace::Event(
                "delta_prepared",
                std::format(
                  "\"generation\":{},\"full_bytes\":{},\"delta_bytes\":{}",
                  capture->generation.value,
                  result->compressed->size(),
                  result->delta->bytes->size()));
          }
#endif
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
      std::lock_guard lock(mutex);
      std::erase_if(assets, [&](const auto& job) { return job.epoch != epoch || job.offer.player == offer.player; });
      std::uint64_t queued = assetInFlightBytes + 2ULL * offer.asset.rawBytes + offer.asset.compressedBytes +
                             (delta ? Limits{}.assetBytes + Limits{}.compressedAssetBytes : 0ULL);
      for (const auto& job : assets)
        queued += 2ULL * job.offer.asset.rawBytes + job.offer.asset.compressedBytes +
                  (job.delta ? Limits{}.assetBytes + Limits{}.compressedAssetBytes : 0ULL);
      if (assets.size() >= 8 || queued > budget) return false;
      assets.push_back({epoch, std::move(offer), std::move(bytes), delta});
      modelWake.notify_one();
      return true;
    }

    void Queue(Wire::RemotePose pose, std::shared_ptr<const ValidatedAsset> asset, std::uint64_t arrivalUs)
    {
      std::lock_guard lock(mutex);
      pose.sample.previous.reset();  // Each queue slot owns exactly one generation.
      const auto key = std::pair{pose.player, pose.sample.generation.value};
      if (poses.size() >= 32 && !poses.contains(key)) return;
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
