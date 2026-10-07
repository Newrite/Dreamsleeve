export module Dreamsleeve.Client.Phantom.Streaming;
import std;
import Dreamsleeve.Client.Utils;
export import Dreamsleeve.Client.Phantom.Worker;

export namespace Dreamsleeve::Client::Phantom
{

  struct Outbound
  {
    std::uint8_t lane{};
    Bytes        bytes;
  };

  class Streaming final
  {
    using Clock                   = std::chrono::steady_clock;
    static constexpr auto Timeout = std::chrono::seconds(30);

    struct Upload
    {
      Wire::Transfer    transfer;
      std::uint32_t     sent{}, acknowledged{};
      Clock::time_point touched;
    };

    struct Pending
    {
      Clock::time_point at;
    };

    struct Awaiting
    {
      RequestId         request;
      Clock::time_point deadline;
    };

    struct Ready
    {};

    struct Rejected
    {};

    struct Local
    {
      Publication                                              value;
      std::variant<Pending, Awaiting, Upload, Ready, Rejected> state;
    };

    struct CacheProbe
    {
      Clock::time_point deadline;
    };

    struct Receiving
    {
      TransferId transfer;
    };

    struct DownloadPlan
    {
      Wire::Offer                                            offer;
      std::variant<CacheProbe, Pending, Awaiting, Receiving> state;
    };

    struct Download
    {
      Wire::Offer            offer;
      std::shared_ptr<Bytes> bytes;
      Clock::time_point      touched;
      RequestId              request;
      std::uint32_t          acknowledged{};
      double                 credit{};
    };

    Exchange&                   exchange;
    Worker                      worker;
    std::optional<Wire::Policy> policy;
    std::optional<Local>        local;
    // Last server commit receipt. A local preparation rollback does not republish
    // that older generation through the server's monotonic manifest admission.
    std::optional<Generation>                       committedGeneration;
    std::optional<Wire::Pose>                       latestPose;
    std::unordered_map<std::uint64_t, Download>     downloads;
    std::unordered_map<std::uint64_t, DownloadPlan> plans;
    std::deque<Wire::Request>                       requests;
    std::uint64_t                                   context{}, lastPoseSequence{}, localRevision{};
    RequestId                                       lastRequest;
    bool                                            active{};
    Clock::time_point                               lastBudget{Clock::now()}, nextPose{};
    double                                          modelCredit{}, poseCredit{};

    bool Request(Wire::Request request)
    {
      if (requests.size() >= 128)
      {
        exchange.Failed("Очередь передачи фантомов заполнена");
        return false;
      }
      requests.push_back(std::move(request));
      return true;
    }

    RequestId NextRequest()
    {
      if (lastRequest.value == std::numeric_limits<std::uint64_t>::max()) return {};
      return RequestId{++lastRequest.value};
    }

    void Cancel(TransferId id)
    {
      std::erase_if(requests, [&](const auto& request) {
        const auto* chunk    = std::get_if<Wire::Chunk>(&request);
        const auto* progress = std::get_if<Wire::Progress>(&request);
        return (chunk && chunk->transfer == id) || (progress && progress->transfer == id);
      });
      Request(Wire::Cancel{id});
    }

    void CancelUpload()
    {
      std::erase_if(requests, [](const auto& request) { return std::holds_alternative<Wire::Publish>(request); });
      if (local)
        if (auto* upload = std::get_if<Upload>(&local->state)) Cancel(upload->transfer.transfer);
    }

    std::size_t TransferCount() const
    {
      return (local && (std::holds_alternative<Awaiting>(local->state) || std::holds_alternative<Upload>(local->state)) ? 1 : 0) +
             std::ranges::count_if(plans, [](const auto& entry) {
               return std::holds_alternative<Awaiting>(entry.second.state) || std::holds_alternative<Receiving>(entry.second.state);
             });
    }

    void Preferences()
    {
      if (!policy) return;
      const auto settings = exchange.Settings();
      Request(
        Wire::Preferences{
            active && policy->enabled && settings.publish,
            active && policy->enabled && settings.receive,
            std::min(settings.maximum, policy->maximumVisible),
            std::min(settings.distance, policy->distance)
        });
    }

    void Publish(Clock::time_point now)
    {
      if (!policy || !policy->enabled || !active || !local || !exchange.Settings().publish) return;
      const auto* pending = std::get_if<Pending>(&local->state);
      if (!pending || pending->at > now) return;
      if (TransferCount() >= policy->concurrentTransfers) return;
      const auto& publication = local->value;
      const auto& asset       = *publication.asset;
      if (
        asset.compressed->size() > policy->limits.compressedAssetBytes || asset.rawBytes > policy->limits.assetBytes ||
        asset.asset.Layout().requiredChannels.size() > policy->limits.nodes)
      {
        exchange.PublicationRejected(local->value.generation, "Модель фантома превышает лимит сервера");
        local->state     = Rejected{};
        lastPoseSequence = 0;
        return;
      }
      const Wire::Descriptor descriptor{
          asset.hash,
          publication.generation,
          AssetVersion,
          static_cast<std::uint32_t>(asset.compressed->size()),
          asset.rawBytes,
          static_cast<std::uint32_t>(asset.asset.Layout().requiredChannels.size())
      };
      const auto id = NextRequest();
      if (id.value && Request(Wire::Publish{descriptor, context, id})) local->state = Awaiting{id, now + Timeout};
    }

    void Receive(const Wire::Policy& value)
    {
      policy = value;
      exchange.SampleRate(value.sampleRate);
      Preferences();
      Publish(Clock::now());
    }

    void Receive(const Wire::PoseDemand& value)
    {
      if (!exchange.PoseDemand(value)) return;
      latestPose.reset();
      nextPose = {};
    }

    void Receive(const Wire::Settled& value)
    {
      exchange.Settled(value);
    }

    void Receive(const Wire::Offer& value)
    {
      if (!policy || !policy->enabled) return;
      const bool admitted = exchange.Offer(value);
      const auto remote   = exchange.Find(value.player);
      if (!remote || remote->view != value.view || remote->descriptor != value.asset || remote->Asset()) return;
      const auto existing = plans.find(value.player);
      if (existing != plans.end())
      {
        if (existing->second.offer.view == value.view && existing->second.offer.asset == value.asset) return;
        if (const auto* receiving = std::get_if<Receiving>(&existing->second.state))
        {
          Cancel(receiving->transfer);
          downloads.erase(receiving->transfer.value);
        }
      }
      const auto queued = admitted && worker.Queue(value);
      plans.insert_or_assign(
        value.player,
        DownloadPlan{
            value,
            queued ? decltype(DownloadPlan::state){CacheProbe{Clock::now() + Timeout}}
                   : decltype(DownloadPlan::state){Pending{Clock::now()}}
        });
    }

    void Receive(const Wire::Transfer& value)
    {
      if (!policy || !active)
      {
        Cancel(value.transfer);
        return;
      }
      if (value.upload)
      {
        const auto* awaiting = local ? std::get_if<Awaiting>(&local->state) : nullptr;
        if (
          !awaiting || value.request != awaiting->request || value.asset.generation != local->value.generation ||
          value.asset.hash != local->value.asset->hash || value.asset.compressedBytes != local->value.asset->compressed->size())
        {
          Cancel(value.transfer);
          return;
        }
        local->state = Upload{value, 0, 0, Clock::now()};
        return;
      }
      const auto  plan     = plans.find(value.player);
      const auto  remote   = exchange.Find(value.player);
      const auto* awaiting = plan == plans.end() ? nullptr : std::get_if<Awaiting>(&plan->second.state);
      if (
        !exchange.Settings().receive || !awaiting || value.request != awaiting->request || !remote ||
        remote->view != plan->second.offer.view || remote->descriptor != value.asset || downloads.size() >= policy->concurrentTransfers)
      {
        Cancel(value.transfer);
        return;
      }
      auto bytes = std::make_shared<Bytes>();
      bytes->reserve(value.asset.compressedBytes);
      const auto [entry, inserted] =
        downloads.emplace(value.transfer.value, Download{plan->second.offer, std::move(bytes), Clock::now(), value.request});
      if (!inserted)
      {
        Cancel(value.transfer);
        return;
      }
      plan->second.state = Receiving{value.transfer};
    }

    void Receive(const Wire::Progress& value)
    {
      if (!local) return;
      auto* upload = std::get_if<Upload>(&local->state);
      if (!upload || upload->transfer.transfer != value.transfer) return;
      if (value.nextOffset < upload->acknowledged || value.nextOffset > upload->sent)
      {
        Cancel(value.transfer);
        local->state     = Rejected{};
        lastPoseSequence = 0;
        exchange.PublicationRejected(local->value.generation, "Неверное подтверждение модели фантома");
        return;
      }
      upload->acknowledged = value.nextOffset;
      upload->touched      = Clock::now();
    }

    void Receive(const Wire::Chunk& value)
    {
      const auto found = downloads.find(value.transfer.value);
      if (found == downloads.end()) return;
      auto& download = found->second;
      if (value.offset != download.bytes->size() || value.data.size() > download.offer.asset.compressedBytes - download.bytes->size())
      {
        Cancel(value.transfer);
        exchange.Unavailable(exchange.Epoch(), download.offer, "Неверный блок модели фантома");
        plans.erase(download.offer.player);
        downloads.erase(found);
        return;
      }
      download.bytes->insert(download.bytes->end(), value.data.begin(), value.data.end());
      download.touched = Clock::now();
      exchange.Count(value.data.size(), 0);
    }

    void Receive(const Wire::Complete& value)
    {
      const auto now = Clock::now();
      if (local)
      {
        const auto* upload   = std::get_if<Upload>(&local->state);
        const auto* awaiting = std::get_if<Awaiting>(&local->state);
        const bool  matches  = upload && upload->transfer.transfer == value.transfer && upload->transfer.request == value.request;
        const bool  refused  = !value.transfer.value && value.upload && value.generation == local->value.generation && awaiting &&
                               awaiting->request == value.request;
        if (matches || refused)
        {
          if (value.accepted)
          {
            local->state        = Ready{};
            committedGeneration = local->value.generation;
          }
          else
          {
            local->state = value.retryAfterMs ? decltype(Local::state){Pending{now + std::chrono::milliseconds(value.retryAfterMs)}}
                                              : decltype(Local::state){Rejected{}};
            if (value.retryAfterMs)
              exchange.Failed("Сервер отклонил модель фантома: " + value.reason);
            else
            {
              lastPoseSequence = 0;
              exchange.PublicationRejected(local->value.generation, "Сервер отклонил модель фантома: " + value.reason);
            }
          }
          return;
        }
      }
      if (!value.transfer.value)
      {
        const auto  plan     = plans.find(value.player);
        const auto* awaiting = plan == plans.end() ? nullptr : std::get_if<Awaiting>(&plan->second.state);
        if (!awaiting || value.upload || awaiting->request != value.request || plan->second.offer.asset.generation != value.generation)
          return;
        if (value.retryAfterMs)
          plan->second.state = Pending{now + std::chrono::milliseconds(value.retryAfterMs)};
        else
        {
          exchange.Unavailable(exchange.Epoch(), plan->second.offer, "Загрузка фантома отклонена: " + value.reason);
          plans.erase(plan);
        }
        return;
      }
      const auto found = downloads.find(value.transfer.value);
      if (found == downloads.end()) return;
      if (found->second.request != value.request) return;
      auto download = std::move(found->second);
      downloads.erase(found);
      if (!value.accepted && value.retryAfterMs)
      {
        const auto plan = plans.find(download.offer.player);
        if (plan != plans.end()) plan->second.state = Pending{now + std::chrono::milliseconds(value.retryAfterMs)};
        return;
      }
      plans.erase(download.offer.player);
      if (!value.accepted || download.bytes->size() != download.offer.asset.compressedBytes)
      {
        exchange.Unavailable(exchange.Epoch(), download.offer, "Передача модели фантома не завершена");
        return;
      }
      if (!worker.Queue(download.offer, std::move(download.bytes)))
        exchange.Unavailable(exchange.Epoch(), download.offer, "Очередь проверки моделей заполнена");
    }

    void Receive(const Wire::Remove& value)
    {
      exchange.Remove(value);
      const auto plan = plans.find(value.player);
      if (plan != plans.end() && plan->second.offer.view <= value.view) plans.erase(plan);
      for (auto it = downloads.begin(); it != downloads.end();)
      {
        if (it->second.offer.player == value.player && it->second.offer.view <= value.view)
        {
          Cancel(TransferId{it->first});
          it = downloads.erase(it);
        }
        else
          ++it;
      }
    }

    void DownloadPoll(Clock::time_point now)
    {
      for (auto& offer : worker.TakeMissing())
      {
        const auto plan = plans.find(offer.player);
        if (plan != plans.end() && plan->second.offer.view == offer.view && plan->second.offer.asset == offer.asset)
          plan->second.state = Pending{now};
      }
      if (!policy || !active || !exchange.Settings().receive) return;
      for (auto it = plans.begin(); it != plans.end();)
      {
        const auto remote = exchange.Find(it->first);
        if (
          !remote || remote->Asset() || remote->State() == Representation::Unavailable || remote->view != it->second.offer.view ||
          remote->descriptor != it->second.offer.asset)
        {
          if (const auto* receiving = std::get_if<Receiving>(&it->second.state))
          {
            Cancel(receiving->transfer);
            downloads.erase(receiving->transfer.value);
          }
          it = plans.erase(it);
        }
        else
          ++it;
      }
      auto count = TransferCount();
      for (auto& [id, plan] : plans)
      {
        if (const auto* probe = std::get_if<CacheProbe>(&plan.state); probe && now >= probe->deadline) plan.state = Pending{now};
        if (const auto* waiting = std::get_if<Awaiting>(&plan.state); waiting && now >= waiting->deadline)
        {
          plan.state = Pending{now + std::chrono::seconds(1)};
          --count;
          exchange.Failed("Таймаут начала загрузки фантома");
        }
        if (const auto* pending = std::get_if<Pending>(&plan.state); pending && now >= pending->at && count < policy->concurrentTransfers)
        {
          const auto remote = exchange.Find(id);
          if (remote && remote->WaitingBudget())
          {
            plan.state = Pending{now + std::chrono::seconds(1)};
            if (exchange.Offer(plan.offer) && worker.Queue(plan.offer)) plan.state = CacheProbe{now + Timeout};
            continue;
          }
          const auto request = NextRequest();
          if (request.value && Request(Wire::Download{id, plan.offer.asset.generation, request}))
          {
            plan.state = Awaiting{request, now + Timeout};
            ++count;
          }
        }
      }
    }

public:

    Streaming(Exchange& owner, std::filesystem::path cache) : exchange(owner), worker(owner, std::move(cache)) {}

    void Reset()
    {
      exchange.Reset();
      policy.reset();
      committedGeneration.reset();
      local.reset();
      latestPose.reset();
      downloads.clear();
      plans.clear();
      requests.clear();
      context          = 0;
      active           = false;
      lastPoseSequence = localRevision = 0;
      modelCredit = poseCredit = 0;
      lastBudget               = Clock::now();
      nextPose                 = {};
    }

    void Context(std::uint64_t revision, bool ready)
    {
      if (context != revision || active != ready)
      {
        CancelUpload();
        committedGeneration.reset();
        context          = revision;
        active           = ready;
        lastPoseSequence = 0;
        latestPose.reset();
        std::erase_if(requests, [](const auto& request) {
          return std::holds_alternative<Wire::Publish>(request) || std::holds_alternative<Wire::Download>(request);
        });
        for (const auto& [id, download] : downloads)
          Cancel(TransferId{id});
        downloads.clear();
        plans.clear();
        if (ready && policy && exchange.Settings().receive)
          for (const auto& remote : exchange.Read().remotes)
            Receive(Wire::Offer{remote.player, remote.view, remote.descriptor});
        if (local) local->state = Pending{Clock::now()};
        if (policy)
        {
          Preferences();
          if (!active)
            Request(Wire::Withdraw{});
          else
            Publish(Clock::now());
        }
      }
      exchange.Context(revision, ready && policy && policy->enabled);
    }

    Result<void> ReceiveAsset(std::span<const std::uint8_t> bytes)
    {
      auto response = Wire::DecodeAsset(bytes);
      if (!response) return std::unexpected(response.error());
      std::visit([&](const auto& value) { Receive(value); }, *response);
      return {};
    }

    Result<void> ReceivePose(std::span<const std::uint8_t> bytes, std::uint64_t arrivalUs)
    {
      auto pose = Wire::DecodePose(bytes);
      if (!pose) return std::unexpected(pose.error());
      const auto remote = exchange.Find(pose->player);
      if (remote && remote->view == pose->view)
      {
        const auto queue = [&](const Wire::Pose& sample, const RemoteVersion& version) {
          if (version.Asset() && version.descriptor.generation == sample.generation)
            worker.Queue(Wire::RemotePose{pose->player, pose->view, sample}, version.Asset(), arrivalUs);
        };
        queue(pose->sample, *remote);
        if (pose->sample.previous)
        {
          queue(*pose->sample.previous, *remote);
          if (remote->previous) queue(*pose->sample.previous, *remote->previous);
        }
      }
      return {};
    }

    std::vector<Outbound> Poll(Clock::time_point now = Clock::now())
    {
      std::vector<Outbound> output;
      auto                  outgoing = exchange.TakeOutput();
      for (const auto& ready : outgoing.displayed)
        Request(ready);
      if (localRevision != outgoing.localRevision)
      {
        if (local && !outgoing.generation)
        {
          committedGeneration.reset();
          Request(Wire::Withdraw{});
        }
        CancelUpload();
        // Keep the committed publication usable during native asset preparation.
        if (!outgoing.generation || !local || committedGeneration != local->value.generation) local.reset();
        latestPose.reset();
        lastPoseSequence = 0;
        localRevision    = outgoing.localRevision;
      }
      if (outgoing.changed)
      {
        Preferences();
        if (!outgoing.settings.publish)
        {
          CancelUpload();
          local.reset();
          committedGeneration.reset();
          latestPose.reset();
          Request(Wire::Withdraw{});
        }
        if (!outgoing.settings.receive)
        {
          for (const auto& [id, download] : downloads)
            Cancel(TransferId{id});
          downloads.clear();
          plans.clear();
        }
      }
      if (outgoing.publication)
      {
        CancelUpload();
        const bool restored = committedGeneration == outgoing.publication->generation;
        local = Local{std::move(*outgoing.publication), restored ? decltype(Local::state){Ready{}} : decltype(Local::state){Pending{now}}};
        latestPose.reset();
        lastPoseSequence = 0;
      }
      if (outgoing.pose && exchange.PosesRequired()) latestPose = std::move(outgoing.pose);
      if (!exchange.PosesRequired()) latestPose.reset();
      if (local)
      {
        if (const auto* awaiting = std::get_if<Awaiting>(&local->state); awaiting && now >= awaiting->deadline)
        {
          local->state = Pending{now + std::chrono::seconds(1)};
          exchange.Failed("Таймаут начала публикации фантома");
        }
        Publish(now);
      }
      DownloadPoll(now);
      const auto elapsed = std::chrono::duration<double>(now - lastBudget).count();
      lastBudget         = now;
      if (policy)
      {
        const auto uploadRate = std::min(policy->modelBytesPerSecond, outgoing.settings.uploadBytesPerSecond);
        modelCredit           = std::min<double>(modelCredit + elapsed * uploadRate, Wire::ChunkBytes * policy->windowChunks);
        poseCredit = std::min<double>(poseCredit + elapsed * policy->poseBytesPerSecond, 2ULL * policy->limits.compressedPoseBytes + 1024);
      }
      if (local && policy)
      {
        if (auto* upload = std::get_if<Upload>(&local->state))
        {
          if (now - upload->touched > Timeout)
          {
            Cancel(upload->transfer.transfer);
            local->state = Pending{now + std::chrono::seconds(1)};
            exchange.Failed("Таймаут передачи модели фантома");
          }
          else
          {
            const auto& bytes = *local->value.asset->compressed;
            for (int i = 0; i < 4 && upload->sent < bytes.size(); ++i)
            {
              const auto count = std::min<std::size_t>(Wire::ChunkBytes, bytes.size() - upload->sent);
              if (upload->sent - upload->acknowledged + count > Wire::ChunkBytes * policy->windowChunks || modelCredit < count) break;
              if (!Request(
                    Wire::Chunk{
                        upload->transfer.transfer,
                        upload->sent,
                        Bytes(bytes.begin() + upload->sent, bytes.begin() + upload->sent + count)
                    }))
                break;
              upload->sent += static_cast<std::uint32_t>(count);
              modelCredit  -= count;
            }
          }
        }
      }
      const auto downloadCount = downloads.size();
      for (auto it = downloads.begin(); it != downloads.end();)
      {
        // Release a chunk-aligned prefix as credit permits, rather than waiting
        // for a whole window. Keep the divisor fixed if a transfer is removed.
        auto& download = it->second;
        if (policy)
        {
          const auto rate = std::min(policy->modelBytesPerSecond, outgoing.settings.downloadBytesPerSecond);
          download.credit = std::min<double>(download.credit + elapsed * rate / downloadCount, Wire::ChunkBytes * policy->windowChunks);
        }
        const auto received       = static_cast<std::uint32_t>(download.bytes->size());
        const auto unacknowledged = received - download.acknowledged;
        const auto available      = std::min(unacknowledged, static_cast<std::uint32_t>(download.credit));
        auto       nextOffset     = download.acknowledged + available;
        if (nextOffset != download.offer.asset.compressedBytes) nextOffset -= nextOffset % Wire::ChunkBytes;
        const auto acknowledged = nextOffset - download.acknowledged;
        if (acknowledged && Request(Wire::Progress{TransferId{it->first}, nextOffset}))
        {
          download.credit       -= acknowledged;
          download.acknowledged  = nextOffset;
          download.touched       = now;
        }
        if (now - it->second.touched > Timeout)
        {
          Cancel(TransferId{it->first});
          const auto plan = plans.find(it->second.offer.player);
          if (plan != plans.end()) plan->second.state = Pending{now + std::chrono::seconds(1)};
          it = downloads.erase(it);
        }
        else
          ++it;
      }
      while (!requests.empty() && output.size() < 16)
      {
        auto encoded = Wire::Encode(requests.front());
        requests.pop_front();
        if (encoded)
        {
          exchange.Count(encoded->size(), 0);
          output.push_back({Wire::ModelsLane, std::move(*encoded)});
        }
        else
          exchange.Failed("Не удалось сформировать запрос фантома");
      }
      const bool rejected = local && std::holds_alternative<Rejected>(local->state);
      if (rejected && latestPose && latestPose->generation == local->value.generation)
        latestPose = latestPose->previous ? std::optional<Wire::Pose>{*latestPose->previous} : std::nullopt;
      const auto rate = policy ? std::min(outgoing.settings.sampleRate, policy->sampleRate) : 1;
      if (
        latestPose && active && policy && local && (std::holds_alternative<Ready>(local->state) || rejected || latestPose->previous) &&
        outgoing.settings.publish &&
        (latestPose->generation == local->value.generation || (rejected && latestPose->generation.value < local->value.generation.value)) &&
        latestPose->context == context && latestPose->sequence.value > lastPoseSequence && now >= nextPose &&
        latestPose->payload.size() <= policy->limits.compressedPoseBytes)
      {
        auto encoded = Wire::Encode(*latestPose);
        if (encoded && encoded->size() <= poseCredit)
        {
          exchange.Count(0, encoded->size());
          poseCredit        -= encoded->size();
          lastPoseSequence   = latestPose->sequence.value;
          const auto period  = std::chrono::microseconds(1000000 / std::max(1u, rate));
          Dreamsleeve::Utils::Time::AdvanceSample(nextPose, now, period);
          output.push_back({Wire::PosesLane, std::move(*encoded)});
        }
        latestPose.reset();
      }
      return output;
    }
  };

}
