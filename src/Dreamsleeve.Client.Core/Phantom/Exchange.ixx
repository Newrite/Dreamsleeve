export module Dreamsleeve.Client.Phantom.Exchange;

import std;
import Dreamsleeve.Client.Domain;
export import Dreamsleeve.Client.PhantomProtocol;
export import Dreamsleeve.Client.Phantom.Playback;

export namespace Dreamsleeve::Client::Phantom
{

  struct Publication
  {
    Generation                           generation;
    std::shared_ptr<const PreparedAsset> asset;
  };

  struct RemoteVersion
  {
    std::uint64_t    view{};
    Wire::Descriptor descriptor;
    Playback         playback;

    RemoteVersion() = default;

    RemoteVersion(std::uint64_t view, Wire::Descriptor descriptor) : view(view), descriptor(descriptor) {}

    std::shared_ptr<const ValidatedAsset> Asset() const
    {
      const auto* ready = std::get_if<std::shared_ptr<const ValidatedAsset>>(&content);
      return ready ? *ready : nullptr;
    }

    bool WaitingBudget() const
    {
      return std::holds_alternative<Deferred>(content);
    }

    Representation State() const
    {
      if (WaitingBudget() || std::holds_alternative<std::monostate>(content)) return Representation::Loading;
      return Asset() ? Representation::Ready : Representation::Unavailable;
    }

    // The share of the model received while it loads; absent before its
    // transfer starts (waiting, a cached model) and once it is ready or refused.
    std::optional<float> Downloaded() const
    {
      if (State() != Representation::Loading || expected == 0) return std::nullopt;
      return std::min(1.0f, static_cast<float>(received) / static_cast<float>(expected));
    }

private:

    friend class Exchange;

    // Bytes of the current transfer as Streaming last reported them.
    std::uint32_t received{}, expected{};

    // Exchange alone can install a non-null validated model. Readiness is
    // derived from this variant, never a separate flag beside a nullable model.
    struct Deferred
    {};

    std::variant<std::monostate, std::shared_ptr<const ValidatedAsset>, std::string, Deferred> content;
  };

  struct Remote : RemoteVersion
  {
    std::uint64_t                player{};
    std::uint64_t                sceneBytes{};
    std::optional<RemoteVersion> previous;

    Remote() = default;

    Remote(std::uint64_t p, std::uint64_t v, Wire::Descriptor d) : RemoteVersion(v, d), player(p) {}
  };

  struct Metrics
  {
    std::uint64_t modelBytes{};
    std::uint64_t poseBytes{};
    std::uint64_t rejected{};
    std::uint64_t dropped{};
    std::uint64_t cacheHits{};
    std::uint32_t queued{};
    std::uint32_t sampleRate{};
    std::string   error;
  };

  struct Display
  {
    ViewSettings        settings;
    std::vector<Remote> remotes;
    Metrics             metrics;
    bool                available{};
  };

  // The only shared boundary. Large values are immutable, poses replace one slot.
  class Exchange final
  {
    enum class PublicationPhase
    {
      Preparing,
      Pending,
      Settled,
      Rejected
    };

    // The own phantom: the generation being prepared or published (local) and
    // the committed one it replaces (previous), with their assets, the poses
    // waiting for the worker and the memory both reserve.
    struct Own
    {
      PublicationPhase                                     phase{PublicationPhase::Preparing};
      std::uint64_t                                        localRevision{1};
      std::optional<Generation>                            localGeneration, previousGeneration;
      std::optional<std::pair<Generation, ValidatedAsset>> capture;
      std::shared_ptr<const Snapshot>                      snapshot, previousSnapshot;
      std::optional<Publication>                           publication;
      std::shared_ptr<const PreparedAsset>                 localAsset, previousAsset;
      std::optional<Wire::Pose>                            encoded;
      std::uint64_t                                        localReservation{}, previousReservation{};

      // A new revision without a generation. A retained compression basis
      // keeps its memory but has no generation or pose authority.
      void Clear(bool retainBasis = false)
      {
        auto       basis = retainBasis ? (phase == PublicationPhase::Settled && localAsset ? localAsset : previousAsset) : nullptr;
        const auto basisReservation = basis ? (basis == localAsset ? localReservation : previousReservation) : 0;

        ++localRevision;
        localGeneration.reset();
        previousGeneration.reset();
        phase = PublicationPhase::Preparing;
        previousSnapshot.reset();
        capture.reset();
        snapshot.reset();
        publication.reset();
        localAsset.reset();
        previousAsset       = std::move(basis);
        previousReservation = basisReservation;
        encoded.reset();
        localReservation = 0;
      }
    };

    mutable std::mutex mutex;
    ViewSettings       settings;
    std::uint64_t      epoch{1};
    // The movement context the own phantom is captured in, and whether it can publish there.
    std::uint64_t                     context{};
    std::optional<Domain::LocationId> space;
    bool                              available{}, changed{true};
    std::optional<Wire::PoseDemand>   poseDemand;
    std::uint64_t                     poseRevision{1};
    std::uint32_t                     serverSampleRate{50};
    Own                               own;

    std::unordered_map<std::uint64_t, Remote> remotes;
    // Remote scenes shown since the last output, for the server's display acknowledgement.
    std::vector<Wire::Displayed> displayed;
    Metrics                      metrics;

    bool DemandAllowsPoses() const
    {
      return !poseDemand || poseDemand->context != context || poseDemand->required;
    }

    // Decode and model capacity and the compressed transfer, with the poses.
    static std::uint64_t Reservation(const Wire::Descriptor& descriptor)
    {
      return 3ULL * descriptor.rawBytes + descriptor.compressedBytes + RemotePoseBytes();
    }

    static std::uint64_t Retained(const RemoteVersion& remote)
    {
      const auto asset = remote.Asset();
      return (asset ? asset->MemoryBytes() : 0) + RemotePoseBytes();
    }

    static std::uint64_t Reservation(const Remote& remote)
    {
      // Once decode completes, worker scratch and compressed input are released.
      // The immutable asset is shared with Game; charge it once here.
      const auto contentBytes = remote.Asset() ? Retained(remote) : remote.WaitingBudget() ? 0 : Reservation(remote.descriptor);
      return contentBytes + remote.sceneBytes + (remote.previous ? Retained(*remote.previous) : 0);
    }

    std::uint64_t Reserved(std::uint64_t except = 0) const
    {
      std::uint64_t bytes = own.localReservation + own.previousReservation;
      for (const auto& [id, remote] : remotes)
        if (id != except) bytes += Reservation(remote);
      return bytes;
    }

public:

    struct Work
    {
      std::uint64_t                                        epoch{};
      std::uint64_t                                        context{};
      std::uint64_t                                        localRevision{};
      std::uint64_t                                        poseRevision{};
      ViewSettings                                         settings;
      std::optional<std::pair<Generation, ValidatedAsset>> capture;
      std::shared_ptr<const Snapshot>                      snapshot;
      std::optional<Generation>                            generation, previousGeneration;
      std::shared_ptr<const Snapshot>                      previousSnapshot;
      std::shared_ptr<const PreparedAsset>                 asset, priorAsset;
    };

    void Configure(ViewSettings value)
    {
      std::lock_guard lock(mutex);
      if (settings.publish != value.publish || (value.memoryBytes < settings.memoryBytes && own.localReservation > value.memoryBytes))
        own.Clear();
      settings           = value;
      changed            = true;
      metrics.sampleRate = std::min(settings.sampleRate, serverSampleRate);
      if (!settings.receive) remotes.clear();

      while (remotes.size() > settings.maximum || Reserved() > settings.memoryBytes)
      {
        if (remotes.empty()) break;
        remotes.erase(remotes.begin());
      }
    }

    ViewSettings Settings() const
    {
      std::lock_guard lock(mutex);
      auto            result = settings;
      result.sampleRate      = std::min(result.sampleRate, serverSampleRate);
      return result;
    }

    bool PoseDemand(const Wire::PoseDemand& value)
    {
      std::lock_guard lock(mutex);
      // Models and control are independent lanes. Preserve a future context's
      // command until the local movement ACK activates that context.
      if (value.context < context || (poseDemand && value.context < poseDemand->context)) return false;
      if (poseDemand && value.context == poseDemand->context && value.required == poseDemand->required) return false;
      ++poseRevision;
      poseDemand = value;
      if (!value.required)
      {
        own.snapshot.reset();
        own.previousSnapshot.reset();
        own.encoded.reset();
      }
      return true;
    }

    bool PosesRequired() const
    {
      std::lock_guard lock(mutex);
      return available && settings.publish && DemandAllowsPoses();
    }

    void SampleRate(std::uint32_t rate)
    {
      std::lock_guard lock(mutex);
      serverSampleRate   = std::clamp(rate, 1u, 50u);
      metrics.sampleRate = std::min(settings.sampleRate, serverSampleRate);
    }

    void Context(std::uint64_t value, bool ready, std::optional<Domain::LocationId> location = {})
    {
      std::lock_guard lock(mutex);
      if (value != context) own.Clear(true);
      space = std::move(location);
      if (value != context || available != ready)
      {
        ++poseRevision;
        if (poseDemand && poseDemand->context < value) poseDemand.reset();
        context   = value;
        available = ready;
        own.snapshot.reset();
        own.previousSnapshot.reset();
        own.encoded.reset();
        for (auto& [id, remote] : remotes)
        {
          remote.playback.Clear();
          remote.previous.reset();
        }
        changed = true;
      }
    }

    void Reset()
    {
      std::lock_guard lock(mutex);
      ++epoch;
      ++poseRevision;
      poseDemand.reset();
      space.reset();
      context   = 0;
      available = false;
      changed   = true;
      own.Clear();
      remotes.clear();
      displayed.clear();
      metrics = {};
    }

    // The native source was destroyed/replaced across a context/root change.
    // Neither of its generation-specific poses can safely be produced again.
    void RestartCapture()
    {
      std::lock_guard lock(mutex);
      own.Clear(true);
      changed = true;
    }

    bool Submit(std::uint64_t expectedContext, Generation generation, ValidatedAsset asset)
    {
      std::lock_guard lock(mutex);
      const auto      bytes = 4 * asset.MemoryBytes() + LocalPoseBytes();
      if (
        expectedContext != context || !available || !settings.publish || own.capture ||
        (own.localGeneration && own.phase != PublicationPhase::Settled && own.phase != PublicationPhase::Rejected) ||
        bytes + Reserved() - (own.phase == PublicationPhase::Rejected ? own.localReservation : 0) > settings.memoryBytes)
        return false;

      // A rejected candidate never replaces the last usable bridge asset.
      if (own.phase != PublicationPhase::Rejected && own.localGeneration)
      {
        own.previousGeneration  = own.localGeneration;
        own.previousReservation = own.localReservation;
        own.previousAsset       = std::move(own.localAsset);
      }

      own.localAsset.reset();
      own.phase            = PublicationPhase::Preparing;
      own.localReservation = bytes;
      ++own.localRevision;
      own.localGeneration = generation;
      changed             = true;
      own.capture.emplace(generation, std::move(asset));
      return true;
    }

    void Submit(std::shared_ptr<const Snapshot> pose, std::shared_ptr<const Snapshot> prior = {})
    {
      std::lock_guard lock(mutex);
      if (!available || !settings.publish || !DemandAllowsPoses()) return;
      if (own.localGeneration == pose->generation)
      {
        if (prior && own.previousGeneration == prior->generation && prior->sampledAtUs == pose->sampledAtUs)
          own.previousSnapshot = std::move(prior);
        own.snapshot = std::move(pose);
      }
      else if (own.previousGeneration == pose->generation)
        own.previousSnapshot = std::move(pose);
    }

    Work TakeWork()
    {
      std::lock_guard lock(mutex);
      auto            current = settings;
      current.sampleRate      = std::min(current.sampleRate, serverSampleRate);
      return {
          epoch,
          context,
          own.localRevision,
          poseRevision,
          current,
          std::exchange(own.capture, {}),
          own.localAsset ? std::exchange(own.snapshot, {}) : nullptr,
          own.localGeneration,
          own.previousGeneration,
          std::exchange(own.previousSnapshot, {}),
          own.localAsset,
          own.previousAsset
      };
    }

    void Prepared(std::uint64_t workEpoch, std::uint64_t revision, Publication value)
    {
      std::lock_guard lock(mutex);
      if (workEpoch == epoch && revision == own.localRevision && own.localGeneration == value.generation && settings.publish)
      {
        // Preparation scratch is gone; keep immutable NIF/compressed data and
        // bounded pose work, not another compression reservation indefinitely.
        own.localReservation = value.asset->asset.MemoryBytes() + value.asset->compressed->capacity() +
                               (value.asset->delta ? value.asset->delta->bytes->capacity() : 0) + LocalPoseBytes();
        own.localAsset       = value.asset;
        own.publication      = std::move(value);
        if (own.phase == PublicationPhase::Preparing) own.phase = PublicationPhase::Pending;
      }
    }

    void PreparationFailed(std::uint64_t workEpoch, std::uint64_t revision, std::string error)
    {
      std::lock_guard lock(mutex);
      if (workEpoch == epoch && revision == own.localRevision)
      {
        if (own.previousGeneration)
        {
          ++own.localRevision;
          own.localGeneration = own.previousGeneration;
          own.localAsset      = std::move(own.previousAsset);
          own.phase           = PublicationPhase::Settled;
          own.previousGeneration.reset();
          own.localReservation = std::exchange(own.previousReservation, 0);
          own.snapshot         = std::exchange(own.previousSnapshot, {});
          own.capture.reset();
          own.publication = own.localAsset ? std::optional<Publication>{{*own.localGeneration, own.localAsset}} : std::nullopt;
          own.encoded.reset();
        }
        else
          own.Clear();
        changed       = true;
        metrics.error = std::move(error);
        ++metrics.rejected;
      }
    }

    // Terminal transport rejection is not settlement. Retain the old bridge,
    // permit a later appearance generation, and never promote this candidate.
    void PublicationRejected(Generation generation, std::string error)
    {
      std::lock_guard lock(mutex);
      if (own.localGeneration != generation) return;
      own.phase = PublicationPhase::Rejected;
      own.encoded.reset();
      metrics.error = std::move(error);
      ++metrics.rejected;
    }

    bool Capturing(Generation generation) const
    {
      std::lock_guard lock(mutex);
      return (own.localGeneration == generation || own.previousGeneration == generation) && settings.publish;
    }

    bool CanReplace() const
    {
      std::lock_guard lock(mutex);
      return !own.localGeneration || own.phase == PublicationPhase::Settled || own.phase == PublicationPhase::Rejected;
    }

    void Settled(const Wire::Settled& value)
    {
      std::lock_guard lock(mutex);
      if (context != value.context || own.localGeneration != value.generation) return;
      if (own.phase == PublicationPhase::Rejected) return;
      own.phase = PublicationPhase::Settled;
      own.previousGeneration.reset();
      own.previousSnapshot.reset();
      own.previousReservation = 0;
      own.previousAsset.reset();
    }

    void Displayed(Wire::Displayed value)
    {
      std::lock_guard lock(mutex);
      const auto      found = remotes.find(value.player);
      if (found == remotes.end() || found->second.view != value.view || found->second.descriptor.generation != value.generation) return;
      found->second.previous.reset();
      std::erase_if(displayed, [&](const auto& item) { return item.player == value.player; });
      if (displayed.size() < 16) displayed.push_back(value);
    }

    void Encoded(std::uint64_t workEpoch, std::uint64_t workPoseRevision, Wire::Pose value)
    {
      std::lock_guard lock(mutex);
      if (
        workEpoch == epoch && workPoseRevision == poseRevision && context == value.context &&
        (own.localGeneration == value.generation ||
         (own.phase == PublicationPhase::Preparing && own.previousGeneration == value.generation)) &&
        settings.publish && DemandAllowsPoses())
      {
        if (!own.previousGeneration) value.previous.reset();
        own.encoded = std::move(value);
      }
    }

    struct Output
    {
      std::optional<Generation>    generation;
      bool                         changed{};
      ViewSettings                 settings;
      std::uint64_t                localRevision{};
      std::optional<Publication>   publication;
      std::optional<Wire::Pose>    pose;
      std::vector<Wire::Displayed> displayed;
    };

    Output TakeOutput()
    {
      std::lock_guard lock(mutex);
      return {
          own.localGeneration,
          std::exchange(changed, false),
          settings,
          own.localRevision,
          std::exchange(own.publication, {}),
          std::exchange(own.encoded, {}),
          std::exchange(displayed, {})
      };
    }

    std::uint64_t Epoch() const
    {
      std::lock_guard lock(mutex);
      return epoch;
    }

    // Capture and submission share the movement authority's coordinate space.
    // A game transition may lead or lag the network thread; neither may relabel
    // a captured model as belonging to a different context.
    std::optional<std::uint64_t> CaptureContext(const Domain::LocationId& location) const
    {
      std::lock_guard lock(mutex);
      if (!available || !space || *space != location) return {};
      return context;
    }

    bool Available() const
    {
      std::lock_guard lock(mutex);
      return available;
    }

    std::uint64_t RemainingMemory() const
    {
      std::lock_guard lock(mutex);
      return settings.memoryBytes - std::min(settings.memoryBytes, Reserved());
    }

    // A scene adapter reserves its complete planned native allocation before
    // building GPU buffers. Replacing a model temporarily owns both scenes.
    bool SceneMemory(std::uint64_t player, std::uint64_t bytes)
    {
      std::lock_guard lock(mutex);
      const auto      found = remotes.find(player);
      if (found == remotes.end()) return bytes == 0;
      if (bytes <= found->second.sceneBytes)
      {
        found->second.sceneBytes = bytes;
        return true;
      }
      if (
        bytes + Reservation(found->second) - found->second.sceneBytes >
        settings.memoryBytes - std::min(settings.memoryBytes, Reserved(player)))
        return false;
      found->second.sceneBytes = bytes;
      return true;
    }

    bool Offer(const Wire::Offer& offer)
    {
      std::lock_guard lock(mutex);
      if (!settings.receive) return false;
      const auto found = remotes.find(offer.player);
      if (found != remotes.end() && offer.view < found->second.view) return false;
      if (found == remotes.end() && remotes.size() >= settings.maximum) return false;

      const auto  sceneBytes = found == remotes.end() ? 0 : found->second.sceneBytes;
      const auto* prior      = found == remotes.end() ? nullptr
                             : found->second.Asset()  ? static_cast<const RemoteVersion*>(&found->second)
                             : found->second.previous ? &*found->second.previous
                                                      : nullptr;
      const auto  priorBytes = prior && (found->second.view != offer.view || found->second.descriptor != offer.asset) ? Retained(*prior)
                             : found != remotes.end() && found->second.previous ? Retained(*found->second.previous)
                                                                                : 0;
      const bool  admitted =
        Reservation(offer.asset) + sceneBytes + priorBytes <= settings.memoryBytes - std::min(settings.memoryBytes, Reserved(offer.player));

      // The server offers each subscription once. A new remote that does not fit
      // yet is kept as Deferred metadata and retried, never forgotten.
      auto& remote = remotes[offer.player];
      if (remote.view != offer.view || remote.descriptor != offer.asset)
      {
        auto prior = remote.Asset() ? std::optional<RemoteVersion>{static_cast<const RemoteVersion&>(remote)} : std::move(remote.previous);
        remote     = {offer.player, offer.view, offer.asset};
        remote.sceneBytes = sceneBytes;
        remote.previous   = std::move(prior);
      }

      // Keep the new AOI view so bridge poses still reach the visible scene.
      // Deferred metadata owns no incoming model allocation or decode job.
      if (!admitted)
        remote.content = RemoteVersion::Deferred{};
      else if (remote.WaitingBudget())
        remote.content = std::monostate{};
      return admitted;
    }

    void Remove(const Wire::Remove& remove)
    {
      std::lock_guard lock(mutex);
      const auto      found = remotes.find(remove.player);
      if (found != remotes.end() && remove.view >= found->second.view) remotes.erase(found);
    }

    // The server restarted this client's phantom session: it will offer every
    // current subscription again, and earlier offers have no Remove to follow.
    void ClearRemotes()
    {
      std::lock_guard lock(mutex);
      remotes.clear();
      displayed.clear();
    }

    void Loaded(std::uint64_t workEpoch, const Wire::Offer& offer, std::shared_ptr<const ValidatedAsset> asset, bool cached)
    {
      std::lock_guard lock(mutex);
      const auto      found = remotes.find(offer.player);
      if (workEpoch != epoch || found == remotes.end() || found->second.view != offer.view || found->second.descriptor != offer.asset)
        return;
      if (!asset) return;

      found->second.content = std::move(asset);
      if (cached) ++metrics.cacheHits;
    }

    // Streaming reports a model transfer as its bytes arrive; a new offer of the
    // player starts from nothing again.
    void Received(const Wire::Offer& offer, std::uint32_t bytes, std::uint32_t total)
    {
      std::lock_guard lock(mutex);
      const auto      found = remotes.find(offer.player);
      if (found == remotes.end() || found->second.view != offer.view || found->second.descriptor != offer.asset) return;
      found->second.received = bytes;
      found->second.expected = total;
    }

    std::shared_ptr<const ValidatedAsset> AssetFor(const Wire::Descriptor& descriptor) const
    {
      std::lock_guard lock(mutex);
      for (const auto& [id, remote] : remotes)
        if (remote.descriptor.SameContent(descriptor) && remote.Asset()) return remote.Asset();
      return {};
    }

    void Unavailable(std::uint64_t workEpoch, const Wire::Offer& offer, std::string error)
    {
      std::lock_guard lock(mutex);
      const auto      found = remotes.find(offer.player);
      if (workEpoch != epoch || found == remotes.end() || found->second.view != offer.view || found->second.descriptor != offer.asset)
        return;
      found->second.content = error;
      metrics.error         = std::move(error);
      ++metrics.rejected;
    }

    std::optional<Remote> Find(std::uint64_t player) const
    {
      std::lock_guard lock(mutex);
      const auto      found = remotes.find(player);
      return found == remotes.end() ? std::nullopt : std::optional(found->second);
    }

    void Pose(std::uint64_t workEpoch, const Wire::RemotePose& wire, std::shared_ptr<const Snapshot> pose, std::uint64_t arrivalUs)
    {
      std::lock_guard lock(mutex);
      const auto      found = remotes.find(wire.player);
      if (workEpoch != epoch || found == remotes.end() || found->second.view != wire.view) return;
      auto& remote  = found->second;
      auto* version = remote.descriptor.generation == wire.sample.generation ? static_cast<RemoteVersion*>(&remote)
                    : remote.previous && remote.previous->descriptor.generation == wire.sample.generation ? &*remote.previous
                                                                                                          : nullptr;
      if (version && !version->playback.Push(std::move(pose), arrivalUs, settings)) ++metrics.dropped;
    }

    void Failed(std::string error, bool rejected = true)
    {
      std::lock_guard lock(mutex);
      metrics.error = std::move(error);
      if (rejected) ++metrics.rejected;
    }

    void Count(std::uint64_t models, std::uint64_t poses)
    {
      std::lock_guard lock(mutex);
      metrics.modelBytes += models;
      metrics.poseBytes  += poses;
    }

    Metrics Stats() const
    {
      std::lock_guard lock(mutex);
      return metrics;
    }

    Display Read() const
    {
      std::lock_guard lock(mutex);
      Display         result{settings, {}, metrics, available};
      result.remotes.reserve(remotes.size());
      result.settings.sampleRate = std::min(result.settings.sampleRate, serverSampleRate);

      for (const auto& [id, remote] : remotes)
        result.remotes.push_back(remote);
      return result;
    }
  };

}
