export module Dreamsleeve.Client.Phantom.Publisher;

import std;
import Dreamsleeve.Client.Utils;
import Dreamsleeve.Client.Phantom.Http;
export import Dreamsleeve.Client.Phantom.Link;

export namespace Dreamsleeve::Client::Phantom
{

  // The own phantom on the network thread: the prepared model goes to the
  // server by request and upload, then poses follow at the sampling rate within
  // the server's byte budget. A committed generation stays the bridge while
  // the next one is prepared; a rejected one keeps sending the older poses.
  class Publisher final
  {
    using Clock    = Link::Clock;
    using Pending  = Link::Pending;
    using Awaiting = Link::Awaiting;

    struct Upload
    {
      Wire::Transfer    transfer;
      std::uint32_t     progress{};
      Clock::time_point touched;
    };

    struct Ready
    {};

    struct Rejected
    {};

    struct Local
    {
      Publication                                              value;
      std::variant<Pending, Awaiting, Upload, Ready, Rejected> state;
      bool                                                     fullOnly{};
    };

    Link&                link;
    std::optional<Local> local;
    // Last server commit receipt. A local preparation rollback does not republish
    // that older generation through the server's monotonic manifest admission.
    std::optional<Generation> committedGeneration;
    std::optional<Wire::Pose> latestPose;
    std::uint64_t             lastPoseSequence{}, localRevision{};
    Clock::time_point         lastBudget{Clock::now()}, nextPose{};
    double                    poseCredit{};

    // The latest captured pose goes out when the publication can carry it:
    // committed, or rejected with a bridge to the older generation, in this
    // context, newer than the last one sent and within the sampling rate.
    bool PoseDue(Clock::time_point now, bool publish) const
    {
      if (!latestPose || !link.active || !link.policy || !local || !publish) return false;
      const bool rejected = std::holds_alternative<Rejected>(local->state);
      const bool carried  = std::holds_alternative<Ready>(local->state) || rejected || latestPose->previous;
      const bool current =
        latestPose->generation == local->value.generation || (rejected && latestPose->generation.value < local->value.generation.value);
      return carried && current && latestPose->context == link.context && latestPose->sequence.value > lastPoseSequence &&
             now >= nextPose && latestPose->payload.size() <= link.policy->limits.compressedPoseBytes;
    }

public:

    explicit Publisher(Link& shared) : link(shared) {}

    // The publication request or upload takes one of the server's transfer slots.
    bool Transferring() const
    {
      return local && (std::holds_alternative<Awaiting>(local->state) || std::holds_alternative<Upload>(local->state));
    }

    void Reset()
    {
      committedGeneration.reset();
      local.reset();
      latestPose.reset();
      lastPoseSequence = localRevision = 0;
      poseCredit                       = 0;
      lastBudget                       = Clock::now();
      nextPose                         = {};
    }

    void CancelUpload()
    {
      std::erase_if(link.requests, [](const auto& request) { return std::holds_alternative<Wire::Publish>(request); });
      if (local)
        if (auto* upload = std::get_if<Upload>(&local->state)) link.Cancel(upload->transfer.transfer);
    }

    // transfers counts every slot in use, the own and the downloads.
    void Publish(Clock::time_point now, std::size_t transfers)
    {
      const auto& policy = link.policy;
      if (!policy || !policy->enabled || !link.active || !local || !link.exchange.Settings().publish) return;
      const auto* pending = std::get_if<Pending>(&local->state);
      if (!pending || pending->at > now) return;
      if (transfers >= policy->concurrentTransfers) return;

      const auto& publication = local->value;
      const auto& asset       = *publication.asset;
      if (
        asset.compressed->size() > policy->limits.compressedAssetBytes || asset.rawBytes > policy->limits.assetBytes ||
        asset.asset.Layout().requiredChannels.size() > policy->limits.nodes)
      {
        link.exchange.PublicationRejected(local->value.generation, "Модель фантома превышает лимит сервера");
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
      const auto id = link.NextRequest();
      if (
        id.value && link.Request(
                      Wire::Publish{
                          descriptor,
                          link.context,
                          id,
                          !local->fullOnly && asset.delta ? std::optional{asset.delta->descriptor} : std::nullopt
                      }))
        local->state = Awaiting{id, now + Link::Timeout};
    }

    // The server lost its queued responses: an upload or a request in flight starts over.
    void Resynchronize()
    {
      if (!local) return;
      if (const auto* upload = std::get_if<Upload>(&local->state)) link.http.Cancel(upload->transfer.transfer);
      if (std::holds_alternative<Upload>(local->state) || std::holds_alternative<Awaiting>(local->state))
        local->state = Pending{Clock::now()};
    }

    // The server no longer wants poses: the captured one is not sent.
    void ForgetPose()
    {
      latestPose.reset();
      nextPose = {};
    }

    // Leaving the context: a new context also drops the publication itself.
    void Leave(bool newContext)
    {
      CancelUpload();
      if (newContext) local.reset();
      committedGeneration.reset();
      lastPoseSequence = 0;
      latestPose.reset();
    }

    // In a new context the publication is offered again from the start.
    void Restart()
    {
      if (local) local->state = Pending{Clock::now()};
    }

    void Receive(const Wire::Transfer& value)
    {
      const auto* awaiting = local ? std::get_if<Awaiting>(&local->state) : nullptr;
      if (
        !awaiting || value.request != awaiting->request || value.asset.generation != local->value.generation ||
        value.asset.hash != local->value.asset->hash || value.asset.compressedBytes != local->value.asset->compressed->size())
      {
        link.Cancel(value.transfer);
        return;
      }
      const auto& prepared = *local->value.asset;
      if (value.delta && (local->fullOnly || !prepared.delta || prepared.delta->descriptor != *value.delta))
      {
        link.Cancel(value.transfer);
        local->fullOnly = true;
        local->state    = Pending{Clock::now()};
        return;
      }
      if (!link.http.Start(
            value,
            value.delta ? prepared.delta->bytes : prepared.compressed,
            std::min(link.policy->modelBytesPerSecond, link.exchange.Settings().uploadBytesPerSecond)))
      {
        link.Cancel(value.transfer);
        local->state = Pending{Clock::now() + Link::RetryDelay};
        return;
      }
      local->state = Upload{value, 0, Clock::now()};
    }

    // The end of the own upload or the refusal of its request; false when the
    // completion belongs to a download.
    bool Complete(const Wire::Complete& value, Clock::time_point now)
    {
      if (!local) return false;
      const auto* upload   = std::get_if<Upload>(&local->state);
      const auto* awaiting = std::get_if<Awaiting>(&local->state);
      const bool  matches  = upload && upload->transfer.transfer == value.transfer && upload->transfer.request == value.request;
      const bool  refused  = !value.transfer.value && value.upload && value.generation == local->value.generation && awaiting &&
                             awaiting->request == value.request;
      if (!matches && !refused) return false;

      if (matches)
      {
        // A commit can arrive between HTTP progress polls. Count its final
        // persisted bytes before replacing Upload with Ready.
        if (value.accepted)
          link.exchange.Count(upload->transfer.BodyBytes() - upload->progress, 0);
        else
          link.http.Cancel(value.transfer);
      }
      if (value.accepted)
      {
        local->state        = Ready{};
        committedGeneration = local->value.generation;
      }
      else if (!local->fullOnly && local->value.asset->delta && (value.retryAfterMs == 0 || value.reason == "delta base unavailable"))
      {
        local->fullOnly = true;
        local->state    = Pending{now + std::chrono::milliseconds(value.retryAfterMs)};
      }
      else if (value.retryAfterMs)
      {
        local->state = Pending{now + std::chrono::milliseconds(value.retryAfterMs)};
        link.exchange.Failed("Сервер отклонил модель фантома: " + value.reason);
      }
      else
      {
        local->state     = Rejected{};
        lastPoseSequence = 0;
        link.exchange.PublicationRejected(local->value.generation, "Сервер отклонил модель фантома: " + value.reason);
      }
      return true;
    }

    bool Owns(const Wire::Transfer& transfer) const
    {
      const auto* upload = local ? std::get_if<Upload>(&local->state) : nullptr;
      return upload && upload->transfer.request == transfer.request && upload->transfer.transfer == transfer.transfer;
    }

    // Upload bytes so far; an upload that stopped moving starts over.
    void Progress(Clock::time_point now)
    {
      auto* upload = local ? std::get_if<Upload>(&local->state) : nullptr;
      if (!upload) return;
      const auto progress = link.http.Progress(upload->transfer.transfer, upload->transfer.request);
      if (progress > upload->progress)
      {
        link.exchange.Count(progress - upload->progress, 0);
        upload->progress = progress;
        upload->touched  = now;
      }
      if (now - upload->touched > Link::Timeout)
      {
        link.Cancel(upload->transfer.transfer);
        local->state = Pending{now + Link::RetryDelay};
      }
    }

    // A new local revision: a withdrawn generation is withdrawn on the server too.
    void Revise(const Exchange::Output& outgoing)
    {
      if (localRevision == outgoing.localRevision) return;
      if (local && !outgoing.generation)
      {
        committedGeneration.reset();
        link.Request(Wire::Withdraw{});
      }
      CancelUpload();
      // Keep the committed publication usable during native asset preparation.
      if (!outgoing.generation || !local || committedGeneration != local->value.generation) local.reset();
      latestPose.reset();
      lastPoseSequence = 0;
      localRevision    = outgoing.localRevision;
    }

    // Publishing switched off: nothing of the own phantom remains on the server.
    void Stop()
    {
      CancelUpload();
      local.reset();
      committedGeneration.reset();
      latestPose.reset();
      link.Request(Wire::Withdraw{});
    }

    // A newly prepared generation; the committed one comes back as Ready.
    void Adopt(Publication publication, Clock::time_point now)
    {
      CancelUpload();
      const bool restored = committedGeneration == publication.generation;
      local = Local{std::move(publication), restored ? decltype(Local::state){Ready{}} : decltype(Local::state){Pending{now}}};
      latestPose.reset();
      lastPoseSequence = 0;
    }

    void Capture(std::optional<Wire::Pose> pose)
    {
      if (pose && link.exchange.PosesRequired()) latestPose = std::move(pose);
      if (!link.exchange.PosesRequired()) latestPose.reset();
    }

    // A request the server never answered is tried again, then the next one goes.
    void Retry(Clock::time_point now, std::size_t transfers)
    {
      if (!local) return;
      if (const auto* awaiting = std::get_if<Awaiting>(&local->state); awaiting && now >= awaiting->deadline)
      {
        local->state = Pending{now + Link::RetryDelay};
        link.exchange.Failed("Таймаут начала публикации фантома");
      }
      Publish(now, transfers);
    }

    // The pose byte budget fills at the server's rate up to two poses and a margin.
    void Refill(Clock::time_point now)
    {
      const auto elapsed = std::chrono::duration<double>(now - lastBudget).count();
      lastBudget         = now;
      if (link.policy)
        poseCredit = std::min<double>(
          poseCredit + elapsed * link.policy->poseBytesPerSecond,
          double(2ULL * link.policy->limits.compressedPoseBytes + 1024));
    }

    void SendPose(Clock::time_point now, const ViewSettings& settings, std::vector<Outbound>& output)
    {
      const bool rejected = local && std::holds_alternative<Rejected>(local->state);
      if (rejected && latestPose && latestPose->generation == local->value.generation)
        latestPose = latestPose->previous ? std::optional<Wire::Pose>{*latestPose->previous} : std::nullopt;
      const auto rate = link.policy ? std::min(settings.sampleRate, link.policy->sampleRate) : 1;
      if (!PoseDue(now, settings.publish)) return;

      auto encoded = Wire::Encode(*latestPose);
      if (encoded && encoded->size() <= poseCredit)
      {
        link.exchange.Count(0, encoded->size());
        poseCredit        -= encoded->size();
        lastPoseSequence   = latestPose->sequence.value;
        const auto period  = std::chrono::microseconds(1000000 / std::max(1u, rate));
        Dreamsleeve::Utils::Time::AdvanceSample(nextPose, now, period);
        output.push_back({Wire::PosesLane, std::move(*encoded)});
      }
      latestPose.reset();
    }
  };

}
