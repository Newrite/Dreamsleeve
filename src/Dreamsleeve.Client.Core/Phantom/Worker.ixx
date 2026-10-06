export module Dreamsleeve.Client.Phantom.Worker;

import std;
export import Dreamsleeve.Client.Phantom.Exchange;
#ifdef DREAMSLEEVE_DIAGNOSTICS
import Dreamsleeve.Client.Diagnostics.PhantomRecorder;
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
    };

    struct PoseJob
    {
      std::uint64_t                         epoch, arrivalUs;
      Wire::RemotePose                      pose;
      std::shared_ptr<const ValidatedAsset> asset;
    };

    Exchange&                                  exchange;
    std::filesystem::path                      directory;
    std::mutex                                 mutex;
    std::condition_variable                    wake;
    std::deque<AssetJob>                       assets;
    std::unordered_map<std::uint64_t, PoseJob> poses;

    struct Missing
    {
      std::uint64_t epoch;
      Wire::Offer   offer;
    };

    std::vector<Missing>                 missing;
    std::shared_ptr<const PreparedAsset> local;
    Generation                           generation;
    std::uint64_t                        localEpoch{}, localRevision{};
    std::optional<std::uint64_t>         cacheBudget;
    std::jthread                         thread;

    void Trim(std::uint64_t budget)
    {
      std::error_code                               error;
      std::vector<std::filesystem::directory_entry> files;
      std::uint64_t                                 size = 0;
      for (const auto& file : std::filesystem::directory_iterator(directory, error))
      {
        const auto name = file.path().stem().string();
        if (name.size() != 64 || !std::ranges::all_of(name, [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); }))
          continue;
        if (file.is_regular_file(error) && file.path().extension() == ".partial")
        {
          std::filesystem::remove(file.path(), error);
          continue;
        }
        if (file.is_regular_file(error) && file.path().extension() == ".zst")
        {
          if (files.size() >= 4096)
          {
            std::filesystem::remove(file.path(), error);
            continue;
          }
          size += file.file_size(error);
          files.push_back(file);
        }
      }
      std::ranges::sort(files, [](const auto& a, const auto& b) {
        std::error_code error;
        return a.last_write_time(error) < b.last_write_time(error);
      });
      for (const auto& file : files)
      {
        if (size <= budget) break;
        const auto bytes = file.file_size(error);
        if (std::filesystem::remove(file.path(), error)) size -= std::min(size, bytes);
      }
    }

    void Save(const Wire::Descriptor& descriptor, std::span<const std::uint8_t> bytes, std::uint64_t budget)
    {
      if (directory.empty() || !budget || bytes.size() > budget) return;
      std::error_code error;
      std::filesystem::create_directories(directory, error);
      if (error) return;
      const auto path = directory / (Hex(descriptor.hash) + ".zst"), temporary = directory / (Hex(descriptor.hash) + ".partial");
      bool       written = false;
      {
        std::ofstream out(temporary, std::ios::binary | std::ios::trunc);
        out.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
        out.flush();
        written = static_cast<bool>(out);
      }
      if (!written)
      {
        std::filesystem::remove(temporary, error);
        return;
      }
      std::filesystem::rename(temporary, path, error);
      if (error) std::filesystem::remove(temporary, error);
      Trim(budget);
    }

    std::shared_ptr<const Bytes> Cached(const Wire::Descriptor& descriptor)
    {
      if (directory.empty() || !exchange.Settings().diskBytes) return {};
      const auto      path = directory / (Hex(descriptor.hash) + ".zst");
      std::error_code error;
      if (std::filesystem::file_size(path, error) != descriptor.compressedBytes || error) return {};
      auto          bytes = std::make_shared<Bytes>(descriptor.compressedBytes);
      std::ifstream input(path, std::ios::binary);
      input.read(reinterpret_cast<char*>(bytes->data()), static_cast<std::streamsize>(bytes->size()));
      if (!input) return {};
      std::filesystem::last_write_time(path, std::filesystem::file_time_type::clock::now(), error);
      return bytes;
    }

    void Asset(const AssetJob& job)
    {
      if (job.epoch != exchange.Epoch()) return;
      const auto remote = exchange.Find(job.offer.player);
      if (!remote || remote->view != job.offer.view || remote->descriptor != job.offer.asset) return;
      if (auto loaded = exchange.AssetFor(job.offer.asset))
      {
        exchange.Loaded(job.epoch, job.offer, std::move(loaded), true);
        return;
      }
      auto bytes = job.bytes ? job.bytes : Cached(job.offer.asset);
      if (!bytes)
      {
        Miss(job);
        return;
      }
      const auto digest = Hash(*bytes);
      if (!digest || *digest != job.offer.asset.hash)
      {
        if (!job.bytes)
        {
          std::error_code error;
          std::filesystem::remove(directory / (Hex(job.offer.asset.hash) + ".zst"), error);
          Miss(job);
          return;
        }
        exchange.Unavailable(job.epoch, job.offer, "Модель фантома: неверная контрольная сумма");
        return;
      }
      auto decoded = ReadAsset(*bytes, job.offer.asset.rawBytes);
      if (
        !decoded || decoded->Value().nodes.size() != job.offer.asset.channels ||
        decoded->Value().geometry.size() != job.offer.asset.geometry)
      {
        exchange.Unavailable(job.epoch, job.offer, "Модель фантома: неверный формат");
        return;
      }
      exchange.Loaded(job.epoch, job.offer, std::make_shared<const ValidatedAsset>(std::move(*decoded)), !job.bytes);
      if (job.bytes) Save(job.offer.asset, *bytes, exchange.Settings().diskBytes);
    }

    void Miss(const AssetJob& job)
    {
      std::lock_guard lock(mutex);
      std::erase_if(missing, [&](const auto& value) { return value.epoch != job.epoch || value.offer.player == job.offer.player; });
      if (missing.size() < 16)
        missing.push_back({job.epoch, job.offer});
      else
        exchange.Unavailable(job.epoch, job.offer, "Очередь загрузки моделей заполнена");
    }

    void Run(std::stop_token stop)
    {
      while (!stop.stop_requested())
      {
        auto work = exchange.TakeWork();
        if (!directory.empty() && work.settings.diskBytes && cacheBudget != work.settings.diskBytes)
        {
          cacheBudget = work.settings.diskBytes;
          try
          {
            Trim(*cacheBudget);
          }
          catch (const std::exception&)
          {
            exchange.Failed("Не удалось обслужить кеш моделей фантомов", false);
          }
        }
        if (localEpoch != work.epoch || localRevision != work.localRevision || !work.settings.publish)
        {
          local.reset();
          generation    = {};
          localEpoch    = work.epoch;
          localRevision = work.localRevision;
        }
        if (work.capture)
        {
          local.reset();
          generation = {};
          try
          {
            auto prepared = Prepare(std::move(work.capture->second));
            if (prepared)
            {
              generation = work.capture->first;
              local      = std::make_shared<const PreparedAsset>(std::move(*prepared));
              exchange.Prepared(work.epoch, work.localRevision, {generation, local});
            }
            else
              exchange.PreparationFailed(
                work.epoch,
                work.localRevision,
                "Не удалось подготовить модель фантома: " + prepared.error().field);
          }
          catch (const std::exception&)
          {
            exchange.PreparationFailed(work.epoch, work.localRevision, "Недостаточно ресурсов для подготовки модели фантома");
          }
        }
        if (work.snapshot && local && work.snapshot->generation == generation && work.context)
        {
          try
          {
            auto snapshot    = *work.snapshot;
            snapshot.context = work.context;
#ifdef DREAMSLEEVE_DIAGNOSTICS
            const auto encodingStart = std::chrono::steady_clock::now();
#endif
            auto encoded = WriteSnapshot(snapshot, local->asset);
#ifdef DREAMSLEEVE_DIAGNOSTICS
            if (encoded)
              Diagnostics::Phantoms().Encoded(
                snapshot,
                std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - encodingStart).count(),
                encoded->size());
#endif
            if (encoded)
              exchange.Encoded(
                work.epoch,
                {snapshot.generation, snapshot.context, snapshot.sequence, snapshot.sampledAtUs, std::move(*encoded)});
            else
              exchange.Failed("Не удалось подготовить позу фантома: " + encoded.error().field);
          }
          catch (const std::exception&)
          {
            exchange.Failed("Недостаточно ресурсов для подготовки позы фантома");
          }
        }
        std::optional<AssetJob>                    asset;
        std::unordered_map<std::uint64_t, PoseJob> batch;
        {
          std::lock_guard lock(mutex);
          if (!assets.empty())
          {
            asset = std::move(assets.front());
            assets.pop_front();
          }
          batch.swap(poses);
        }
        if (asset)
        {
          try
          {
            Asset(*asset);
          }
          catch (const std::exception&)
          {
            exchange.Unavailable(asset->epoch, asset->offer, "Не удалось выделить память для модели фантома");
          }
        }
        for (const auto& [id, job] : batch)
        {
          if (job.epoch != exchange.Epoch()) continue;
          try
          {
            auto pose = ReadSnapshot(job.pose.sample.payload, *job.asset);
            if (
              pose && pose->generation == job.pose.sample.generation && pose->context == job.pose.sample.context &&
              pose->sequence == job.pose.sample.sequence && pose->sampledAtUs == job.pose.sample.sampledAtUs)
              exchange.Pose(job.epoch, job.pose, std::make_shared<const Snapshot>(std::move(*pose)), job.arrivalUs);
            else
              exchange.Failed("Поза фантома: неверный формат");
          }
          catch (const std::exception&)
          {
            exchange.Failed("Недостаточно ресурсов для проверки позы фантома");
          }
        }
        std::unique_lock lock(mutex);
        wake.wait_for(lock, std::chrono::milliseconds(5), [&] { return stop.stop_requested() || !assets.empty() || !poses.empty(); });
      }
    }

public:

    Worker(Exchange& owner, std::filesystem::path cache)
        : exchange(owner),
          directory(std::move(cache)),
          thread([this](std::stop_token stop) { Run(stop); })
    {}

    ~Worker()
    {
      thread.request_stop();
      wake.notify_all();
      thread.join();
    }

    bool Queue(Wire::Offer offer, std::shared_ptr<const Bytes> bytes = {})
    {
      const auto      epoch  = exchange.Epoch();
      const auto      budget = exchange.Settings().memoryBytes;
      std::lock_guard lock(mutex);
      std::erase_if(assets, [&](const auto& job) { return job.epoch != epoch || job.offer.player == offer.player; });
      std::uint64_t queued = 2ULL * offer.asset.rawBytes + offer.asset.compressedBytes;
      for (const auto& job : assets)
        queued += 2ULL * job.offer.asset.rawBytes + job.offer.asset.compressedBytes;
      if (assets.size() >= 8 || queued > budget) return false;
      assets.push_back({epoch, std::move(offer), std::move(bytes)});
      wake.notify_one();
      return true;
    }

    void Queue(Wire::RemotePose pose, std::shared_ptr<const ValidatedAsset> asset, std::uint64_t arrivalUs)
    {
      std::lock_guard lock(mutex);
      if (poses.size() >= 16 && !poses.contains(pose.player)) return;
      poses.insert_or_assign(pose.player, PoseJob{exchange.Epoch(), arrivalUs, std::move(pose), std::move(asset)});
      wake.notify_one();
    }

    std::vector<Wire::Offer> TakeMissing()
    {
      std::lock_guard          lock(mutex);
      std::vector<Wire::Offer> result;
      for (auto& value : missing)
        if (value.epoch == exchange.Epoch()) result.push_back(std::move(value.offer));
      missing.clear();
      return result;
    }
  };

}
