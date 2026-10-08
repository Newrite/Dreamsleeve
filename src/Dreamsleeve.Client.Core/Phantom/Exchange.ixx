export module Dreamsleeve.Client.Phantom.Exchange;

import std;
import Dreamsleeve.Client.Domain;
export import Dreamsleeve.Client.Phantom.Wire;
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

private:

    friend class Exchange;

    // Exchange alone can install a non-null validated model. Readiness is
    // derived from this variant, never a separate flag beside a nullable model.
    struct Deferred
    {};

    std::variant<std::monostate, std::shared_ptr<const ValidatedAsset>, std::string, Deferred> content;
  };

  struct Remote : RemoteVersion
  {
    std::uint64_t                player{}, sceneBytes{};
    std::optional<RemoteVersion> previous;
    Remote() = default;

    Remote(std::uint64_t p, std::uint64_t v, Wire::Descriptor d) : RemoteVersion(v, d), player(p) {}
  };

  struct Metrics
  {
    std::uint64_t modelBytes{}, poseBytes{}, rejected{}, dropped{}, cacheHits{};
    std::uint32_t queued{}, sampleRate{};
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
    PublicationPhase                                     phase{PublicationPhase::Preparing};
    mutable std::mutex                                   mutex;
    ViewSettings                                         settings;
    std::uint64_t                                        epoch{1}, context{}, localRevision{1}, poseRevision{1};
    std::optional<Generation>                            localGeneration, previousGeneration;
    std::optional<Domain::LocationId>                    space;
    std::uint64_t                                        previousReservation{};
    std::shared_ptr<const Snapshot>                      previousSnapshot;
    std::vector<Wire::Displayed>                         displayed;
    bool                                                 available{}, changed{true};
    std::optional<Wire::PoseDemand>                      poseDemand;
    std::uint32_t                                        serverSampleRate{50};
    std::optional<std::pair<Generation, ValidatedAsset>> capture;
    std::shared_ptr<const Snapshot>                      snapshot;
    std::optional<Publication>                           publication;
    std::shared_ptr<const PreparedAsset>                 localAsset, previousAsset;
    std::optional<Wire::Pose>                            encoded;
    std::unordered_map<std::uint64_t, Remote>            remotes;
    Metrics                                              metrics;
    std::uint64_t                                        localReservation{};

    bool DemandAllowsPoses() const
    {
      return !poseDemand || poseDemand->context != context || poseDemand->required;
    }

    void ClearPublication(bool retainBasis = false)
    {
      // A compression basis has no generation/pose authority in the next context.
      auto basis = retainBasis ? (phase == PublicationPhase::Settled && localAsset ? localAsset : previousAsset) : nullptr;
      const auto basisReservation = basis ? (basis == localAsset ? localReservation : previousReservation) : 0;
      ++localRevision;
      localGeneration.reset();
      previousGeneration.reset();
      phase               = PublicationPhase::Preparing;
      previousReservation = 0;
      previousSnapshot.reset();
      capture.reset();
      snapshot.reset();
      publication.reset();
      localAsset.reset();
      previousAsset = std::move(basis);
      previousReservation = basisReservation;
      encoded.reset();
      localReservation = 0;
    }

    static std::uint64_t Reservation(const Wire::Descriptor& descriptor)
    {
      // Decode/model capacity and compressed transfer. Live playback plus the
      // display, network and worker copies can retain independent histories.
      // Four more frames cover decode/interpolation and producers; raw/encoded
      // scratch and two worker batches are charged independently.
      return 3ULL * descriptor.rawBytes + descriptor.compressedBytes + (4 * BufferedPoseCount + 4) * SnapshotWorkingBytes() +
             Limits{}.poseBytes + 4ULL * Limits{}.compressedPoseBytes;
    }

    static std::uint64_t Retained(const RemoteVersion& remote)
    {
      const auto asset = remote.Asset();
      return (asset ? asset->MemoryBytes() : 0) + (4 * BufferedPoseCount + 4) * SnapshotWorkingBytes() + Limits{}.poseBytes +
             4ULL * Limits{}.compressedPoseBytes;
    }

    static std::uint64_t Reservation(const Remote& remote)
    {
      // Once decode completes, worker scratch and compressed input are released.
      // The immutable asset is shared with Game; charge it once here.
      const auto contentBytes = remote.Asset() ? Retained(remote)
                              : remote.WaitingBudget() ? 0 : Reservation(remote.descriptor);
      return contentBytes + remote.sceneBytes +
             (remote.previous ? Retained(*remote.previous) : 0);
    }

    std::uint64_t Reserved(std::uint64_t except = 0) const
    {
      std::uint64_t bytes = localReservation + previousReservation;
      for (const auto& [id, remote] : remotes)
        if (id != except) bytes += Reservation(remote);
      return bytes;
    }

public:

    struct Work
    {
      std::uint64_t                                        epoch{}, context{}, localRevision{}, poseRevision{};
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
      if (settings.publish != value.publish || (value.memoryBytes < settings.memoryBytes && localReservation > value.memoryBytes))
        ClearPublication();
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
        snapshot.reset();
        previousSnapshot.reset();
        encoded.reset();
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
      if (value != context) ClearPublication(true);
      space = std::move(location);
      if (value != context || available != ready)
      {
        ++poseRevision;
        if (poseDemand && poseDemand->context < value) poseDemand.reset();
        context   = value;
        available = ready;
        snapshot.reset();
        previousSnapshot.reset();
        encoded.reset();
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
      ClearPublication();
      remotes.clear();
      displayed.clear();
      metrics = {};
    }

    // The native source was destroyed/replaced across a context/root change.
    // Neither of its generation-specific poses can safely be produced again.
    void RestartCapture()
    {
      std::lock_guard lock(mutex);
      ClearPublication(true);
      changed = true;
    }

    bool Submit(std::uint64_t expectedContext, Generation generation, ValidatedAsset asset)
    {
      std::lock_guard lock(mutex);
      const auto bytes = 4 * asset.MemoryBytes() + 6 * SnapshotWorkingBytes() + Limits{}.poseBytes + 2ULL * Limits{}.compressedPoseBytes;
      if (
        expectedContext != context || !available || !settings.publish || capture ||
        (localGeneration && phase != PublicationPhase::Settled && phase != PublicationPhase::Rejected) ||
        bytes + Reserved() - (phase == PublicationPhase::Rejected ? localReservation : 0) > settings.memoryBytes)
        return false;
      // A rejected candidate never replaces the last usable bridge asset.
      if (phase != PublicationPhase::Rejected && localGeneration)
      {
        previousGeneration  = localGeneration;
        previousReservation = localReservation;
        previousAsset       = std::move(localAsset);
      }
      localAsset.reset();
      phase            = PublicationPhase::Preparing;
      localReservation = bytes;
      ++localRevision;
      localGeneration = generation;
      changed         = true;
      capture.emplace(generation, std::move(asset));
      return true;
    }

    void Submit(std::shared_ptr<const Snapshot> pose, std::shared_ptr<const Snapshot> prior = {})
    {
      std::lock_guard lock(mutex);
      if (!available || !settings.publish || !DemandAllowsPoses()) return;
      if (localGeneration == pose->generation)
      {
        if (prior && previousGeneration == prior->generation && prior->sampledAtUs == pose->sampledAtUs)
          previousSnapshot = std::move(prior);
        snapshot = std::move(pose);
      }
      else if (previousGeneration == pose->generation)
        previousSnapshot = std::move(pose);
    }

    Work TakeWork()
    {
      std::lock_guard lock(mutex);
      auto            current = settings;
      current.sampleRate      = std::min(current.sampleRate, serverSampleRate);
      return {
          epoch,
          context,
          localRevision,
          poseRevision,
          current,
          std::exchange(capture, {}),
          localAsset ? std::exchange(snapshot, {}) : nullptr,
          localGeneration,
          previousGeneration,
          std::exchange(previousSnapshot, {}),
          localAsset,
          previousAsset
      };
    }

    void Prepared(std::uint64_t workEpoch, std::uint64_t revision, Publication value)
    {
      std::lock_guard lock(mutex);
      if (workEpoch == epoch && revision == localRevision && localGeneration == value.generation && settings.publish)
      {
        // Preparation scratch is gone; keep immutable NIF/compressed data and
        // bounded pose work, not another compression reservation indefinitely.
        localReservation = value.asset->asset.MemoryBytes() + value.asset->compressed->capacity() +
                           (value.asset->delta ? value.asset->delta->bytes->capacity() : 0) + 6 * SnapshotWorkingBytes() +
                           Limits{}.poseBytes + 2ULL * Limits{}.compressedPoseBytes;
        localAsset       = value.asset;
        publication      = std::move(value);
        if (phase == PublicationPhase::Preparing) phase = PublicationPhase::Pending;
      }
    }

    void PreparationFailed(std::uint64_t workEpoch, std::uint64_t revision, std::string error)
    {
      std::lock_guard lock(mutex);
      if (workEpoch == epoch && revision == localRevision)
      {
        if (previousGeneration)
        {
          ++localRevision;
          localGeneration = previousGeneration;
          localAsset      = std::move(previousAsset);
          phase           = PublicationPhase::Settled;
          previousGeneration.reset();
          localReservation = std::exchange(previousReservation, 0);
          snapshot         = std::exchange(previousSnapshot, {});
          capture.reset();
          publication = localAsset ? std::optional<Publication>{{*localGeneration, localAsset}} : std::nullopt;
          encoded.reset();
        }
        else
          ClearPublication();
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
      if (localGeneration != generation) return;
      phase = PublicationPhase::Rejected;
      encoded.reset();
      metrics.error = std::move(error);
      ++metrics.rejected;
    }

    bool Capturing(Generation generation) const
    {
      std::lock_guard lock(mutex);
      return (localGeneration == generation || previousGeneration == generation) && settings.publish;
    }

    bool CanReplace() const
    {
      std::lock_guard lock(mutex);
      return !localGeneration || phase == PublicationPhase::Settled || phase == PublicationPhase::Rejected;
    }

    void Settled(const Wire::Settled& value)
    {
      std::lock_guard lock(mutex);
      if (context != value.context || localGeneration != value.generation) return;
      if (phase == PublicationPhase::Rejected) return;
      phase = PublicationPhase::Settled;
      previousGeneration.reset();
      previousSnapshot.reset();
      previousReservation = 0;
      previousAsset.reset();
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
        (localGeneration == value.generation || (phase == PublicationPhase::Preparing && previousGeneration == value.generation)) &&
        settings.publish && DemandAllowsPoses())
      {
        if (!previousGeneration) value.previous.reset();
        encoded = std::move(value);
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
          localGeneration,
          std::exchange(changed, false),
          settings,
          localRevision,
          std::exchange(publication, {}),
          std::exchange(encoded, {}),
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
      if (!admitted && found == remotes.end()) return false;
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
