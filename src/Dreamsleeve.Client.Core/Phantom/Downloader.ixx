export module Dreamsleeve.Client.Phantom.Downloader;

import std;
import Dreamsleeve.Client.Phantom.Http;
import Dreamsleeve.Client.Phantom.Worker;
export import Dreamsleeve.Client.Phantom.Link;

export namespace Dreamsleeve::Client::Phantom
{

  // Remote models on the network thread. Each offer first goes to the worker,
  // which looks in the cache; a miss becomes a download request, then an HTTP
  // transfer whose bytes the worker checks and decodes. Within the server's
  // transfer slots, with retries for requests and transfers that stall.
  class Downloader final
  {
    using Clock    = Link::Clock;
    using Pending  = Link::Pending;
    using Awaiting = Link::Awaiting;

    struct CacheProbe
    {
      Clock::time_point deadline;
    };

    struct Receiving
    {
      TransferId transfer;
    };

    struct Plan
    {
      Wire::Offer                                            offer;
      std::variant<CacheProbe, Pending, Awaiting, Receiving> state;
      std::optional<Digest>                                  baseHash;
    };

    struct Download
    {
      Wire::Offer                   offer;
      std::shared_ptr<Bytes>        bytes;
      Clock::time_point             touched;
      RequestId                     request;
      std::uint32_t                 progress{};
      std::optional<Wire::Complete> receipt;
      std::optional<AssetDelta>     delta;

      std::uint32_t BodyBytes() const
      {
        return delta ? delta->compressedBytes : offer.asset.compressedBytes;
      }
    };

    Link&                                       link;
    Worker&                                     worker;
    std::unordered_map<std::uint64_t, Download> downloads;
    std::unordered_map<std::uint64_t, Plan>     plans;

    // Whether a refused request failed, rather than waiting its turn at the server.
    static bool Failed(Wire::Outcome outcome)
    {
      switch (outcome)
      {
        case Wire::Outcome::Timeout:
        case Wire::Outcome::StorageFailure:
        case Wire::Outcome::HttpFailure:
          return true;
        case Wire::Outcome::Accepted:
        case Wire::Outcome::SourceChanged:
        case Wire::Outcome::ContextChanged:
        case Wire::Outcome::ViewRemoved:
        case Wire::Outcome::Disconnected:
        case Wire::Outcome::Cancelled:
        case Wire::Outcome::Stale:
        case Wire::Outcome::Superseded:
        case Wire::Outcome::DeltaBaseUnavailable:
        case Wire::Outcome::PublishRefused:
        case Wire::Outcome::ReplacementPending:
        case Wire::Outcome::InitialDisplayPending:
        case Wire::Outcome::PublishCooldown:
        case Wire::Outcome::SourceLimit:
        case Wire::Outcome::TransferLimit:
        case Wire::Outcome::ViewUnavailable:
          return false;
      }
      return false;
    }

    // The offer is still the one the exchange holds and its model is not loaded.
    bool Wanted(const Wire::Offer& offer) const
    {
      const auto remote = link.exchange.Find(offer.player);
      return remote && remote->view == offer.view && remote->descriptor == offer.asset && !remote->Asset();
    }

    void CancelReceiving(const Plan& plan)
    {
      if (const auto* receiving = std::get_if<Receiving>(&plan.state))
      {
        link.Cancel(receiving->transfer);
        downloads.erase(receiving->transfer.value);
      }
    }

    // The worker's cache misses become plans that ask the server.
    void TakeMisses(Clock::time_point now)
    {
      for (auto& missing : worker.TakeMissing())
      {
        const auto& offer = missing.offer;
        if (!Wanted(offer)) continue;
        auto plan = plans.find(offer.player);
        if (plan == plans.end()) plan = plans.emplace(offer.player, Plan{offer, Pending{now}}).first;
        if (plan->second.offer.view == offer.view && plan->second.offer.asset == offer.asset)
        {
          plan->second.state    = Pending{now};
          plan->second.baseHash = missing.baseHash;
          link.exchange.Requeued(offer);
        }
      }
    }

    // A plan whose remote left, loaded or was refused has nothing to fetch.
    void DropSettled()
    {
      for (auto it = plans.begin(); it != plans.end();)
      {
        const auto remote = link.exchange.Find(it->first);
        if (
          !remote || remote->Asset() || remote->State() == Representation::Unavailable || remote->view != it->second.offer.view ||
          remote->descriptor != it->second.offer.asset)
        {
          CancelReceiving(it->second);
          it = plans.erase(it);
        }
        else
          ++it;
      }
    }

public:

    Downloader(Link& shared, Worker& models) : link(shared), worker(models) {}

    // Download requests and transfers that take the server's transfer slots.
    std::size_t Transferring() const
    {
      return std::ranges::count_if(plans, [](const auto& entry) {
        return std::holds_alternative<Awaiting>(entry.second.state) || std::holds_alternative<Receiving>(entry.second.state);
      });
    }

    void Reset()
    {
      downloads.clear();
      plans.clear();
    }

    // Every download stops, the server told of each.
    void Clear()
    {
      for (const auto& [id, download] : downloads)
        link.Cancel(TransferId{id});
      Reset();
    }

    // The server lost its transfers: they stop here without telling it.
    void Resynchronize()
    {
      for (const auto& [id, download] : downloads)
        link.http.Cancel(TransferId{id});
      Reset();
    }

    void Receive(const Wire::Offer& value)
    {
      if (!link.policy || !link.policy->enabled) return;
      const bool admitted = link.exchange.Offer(value);
      if (!Wanted(value)) return;
      if (const auto existing = plans.find(value.player); existing != plans.end())
      {
        if (existing->second.offer.view == value.view && existing->second.offer.asset == value.asset) return;
        CancelReceiving(existing->second);
      }

      const auto queued = admitted && worker.Queue(value);
      plans.insert_or_assign(
        value.player,
        Plan{
            value,
            queued ? decltype(Plan::state){CacheProbe{Clock::now() + Link::Timeout}} : decltype(Plan::state){Pending{Clock::now()}}
        });
    }

    void Receive(const Wire::Transfer& value)
    {
      const auto  plan     = plans.find(value.player);
      const auto  remote   = link.exchange.Find(value.player);
      const auto* awaiting = plan == plans.end() ? nullptr : std::get_if<Awaiting>(&plan->second.state);
      if (
        !link.exchange.Settings().receive || !awaiting || value.request != awaiting->request || !remote ||
        remote->view != plan->second.offer.view || remote->descriptor != value.asset ||
        downloads.size() >= link.policy->concurrentTransfers)
      {
        link.Cancel(value.transfer);
        return;
      }

      if (value.delta && (plan->second.baseHash != value.delta->baseHash))
      {
        link.Cancel(value.transfer);
        plan->second.baseHash.reset();
        plan->second.state = Pending{Clock::now()};
        return;
      }

      const auto [entry, inserted] = downloads.emplace(
        value.transfer.value,
        Download{plan->second.offer, std::make_shared<Bytes>(), Clock::now(), value.request, 0, {}, value.delta});
      if (!inserted)
      {
        link.Cancel(value.transfer);
        return;
      }
      plan->second.state = Receiving{value.transfer};
      link.exchange.Received(entry->second.offer, 0, entry->second.BodyBytes());

      if (!link.http.Start(value, {}, std::min(link.policy->modelBytesPerSecond, link.exchange.Settings().downloadBytesPerSecond)))
      {
        link.Cancel(value.transfer);
        downloads.erase(value.transfer.value);
        plan->second.state = Pending{Clock::now() + Link::RetryDelay};
        link.exchange.Retrying(plan->second.offer);
      }
    }

    // The refusal of a download request, or the server's receipt of a transfer.
    void Complete(const Wire::Complete& value, Clock::time_point now)
    {
      if (!value.transfer.value)
      {
        const auto  plan     = plans.find(value.player);
        const auto* awaiting = plan == plans.end() ? nullptr : std::get_if<Awaiting>(&plan->second.state);
        if (!awaiting || value.upload || awaiting->request != value.request || plan->second.offer.asset.generation != value.generation)
          return;
        if (value.retryAfterMs)
        {
          plan->second.state = Pending{now + std::chrono::milliseconds(value.retryAfterMs)};
          if (Failed(value.outcome)) link.exchange.Retrying(plan->second.offer);
        }
        else
        {
          link.exchange.Unavailable(
            link.exchange.Epoch(),
            plan->second.offer,
            ModelFailure::Refused,
            "Загрузка фантома отклонена: " + value.Describe());
          plans.erase(plan);
        }
        return;
      }

      const auto found = downloads.find(value.transfer.value);
      if (found == downloads.end() || found->second.request != value.request) return;
      // The receipt may come before the last bytes: it waits for them.
      if (value.Accepted() && found->second.bytes->size() != found->second.BodyBytes())
      {
        found->second.receipt = value;
        return;
      }

      if (!value.Accepted()) link.http.Cancel(value.transfer);
      auto download = std::move(found->second);
      downloads.erase(found);
      if (!value.Accepted() && value.retryAfterMs)
      {
        const auto plan = plans.find(download.offer.player);
        if (plan != plans.end()) plan->second.state = Pending{now + std::chrono::milliseconds(value.retryAfterMs)};
        link.exchange.Retrying(download.offer);
        return;
      }
      plans.erase(download.offer.player);
      if (!value.Accepted() || download.bytes->size() != download.BodyBytes())
      {
        link.exchange.Unavailable(link.exchange.Epoch(), download.offer, ModelFailure::Transfer, "Передача модели фантома не завершена");
        return;
      }

      if (!worker.Queue(download.offer, std::move(download.bytes), download.delta))
      {
        if (download.delta)
        {
          plans.insert_or_assign(download.offer.player, Plan{download.offer, Pending{now}});
          link.exchange.Retrying(download.offer);
        }
        else
          link.exchange.Unavailable(link.exchange.Epoch(), download.offer, ModelFailure::Transfer, "Очередь проверки моделей заполнена");
      }
    }

    void Receive(const Wire::Remove& value)
    {
      const auto plan = plans.find(value.player);
      if (plan != plans.end() && plan->second.offer.view <= value.view) plans.erase(plan);
      for (auto it = downloads.begin(); it != downloads.end();)
      {
        if (it->second.offer.player == value.player && it->second.offer.view <= value.view)
        {
          link.Cancel(TransferId{it->first});
          it = downloads.erase(it);
        }
        else
          ++it;
      }
    }

    // others counts the slots the own publication takes.
    void Poll(Clock::time_point now, std::size_t others)
    {
      TakeMisses(now);
      if (!link.policy || !link.active || !link.exchange.Settings().receive) return;
      DropSettled();

      auto count = others + Transferring();
      for (auto& [id, plan] : plans)
      {
        if (const auto* probe = std::get_if<CacheProbe>(&plan.state); probe && now >= probe->deadline) plan.state = Pending{now};
        if (const auto* waiting = std::get_if<Awaiting>(&plan.state); waiting && now >= waiting->deadline)
        {
          plan.state = Pending{now + Link::RetryDelay};
          --count;
          link.exchange.Failed("Таймаут начала загрузки фантома");
          link.exchange.Retrying(plan.offer);
        }
        const auto* pending = std::get_if<Pending>(&plan.state);
        if (!pending || now < pending->at || count >= link.policy->concurrentTransfers) continue;

        // A model that did not fit into memory is offered to the exchange again.
        if (const auto remote = link.exchange.Find(id); remote && remote->WaitingBudget())
        {
          plan.state = Pending{now + Link::RetryDelay};
          if (link.exchange.Offer(plan.offer) && worker.Queue(plan.offer)) plan.state = CacheProbe{now + Link::Timeout};
          continue;
        }
        const auto request = link.NextRequest();
        if (request.value && link.Request(Wire::Download{id, plan.offer.asset.generation, request, plan.baseHash}))
        {
          plan.state = Awaiting{request, now + Link::Timeout};
          ++count;
        }
      }
    }

    // A transfer that stopped moving starts over.
    void Expire(Clock::time_point now)
    {
      for (auto it = downloads.begin(); it != downloads.end();)
      {
        if (now - it->second.touched <= Link::Timeout)
        {
          ++it;
          continue;
        }
        link.Cancel(TransferId{it->first});
        const auto plan = plans.find(it->second.offer.player);
        if (plan != plans.end()) plan->second.state = Pending{now + Link::RetryDelay};
        link.exchange.Retrying(it->second.offer);
        it = downloads.erase(it);
      }
    }

    // Bytes received so far, for the counters and the loading ring.
    void Progress(Clock::time_point now)
    {
      for (auto& [id, download] : downloads)
      {
        const auto progress = link.http.Progress(TransferId{id}, download.request);
        if (progress <= download.progress) continue;
        link.exchange.Count(progress - download.progress, 0);
        link.exchange.Received(download.offer, progress, download.BodyBytes());
        download.progress = progress;
        download.touched  = now;
      }
    }

    bool Owns(const Wire::Transfer& transfer) const
    {
      const auto found = downloads.find(transfer.transfer.value);
      return found != downloads.end() && found->second.request == transfer.request;
    }

    // The transfer finished; a receipt that came first completes it now.
    void Arrived(const Wire::Transfer& transfer, std::shared_ptr<Bytes> bytes, Clock::time_point now)
    {
      const auto found = downloads.find(transfer.transfer.value);
      if (found == downloads.end() || found->second.request != transfer.request) return;
      found->second.bytes   = std::move(bytes);
      found->second.touched = now;
      if (found->second.receipt)
      {
        const auto receipt = *found->second.receipt;
        Complete(receipt, now);
      }
    }
  };

}
