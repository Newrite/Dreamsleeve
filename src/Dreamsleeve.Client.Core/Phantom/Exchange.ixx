export module Dreamsleeve.Client.Phantom.Exchange;

import std;
export import Dreamsleeve.Client.Phantom.Wire;
export import Dreamsleeve.Client.Phantom.Playback;

export namespace Dreamsleeve::Client::Phantom
{

  struct Publication
  {
    Generation                           generation;
    std::shared_ptr<const PreparedAsset> asset;
  };

  struct Remote
  {
    std::uint64_t    player{}, view{};
    Wire::Descriptor descriptor;
    Playback         playback;
    std::uint64_t    sceneBytes{};

    Remote() = default;

    Remote(std::uint64_t player, std::uint64_t view, Wire::Descriptor descriptor) : player(player), view(view), descriptor(descriptor) {}

    std::shared_ptr<const ValidatedAsset> Asset() const
    {
      const auto* ready = std::get_if<std::shared_ptr<const ValidatedAsset>>(&content);
      return ready ? *ready : nullptr;
    }

    Representation State() const
    {
      if (std::holds_alternative<std::monostate>(content)) return Representation::Loading;
      return Asset() ? Representation::Ready : Representation::Unavailable;
    }

private:

    friend class Exchange;
    // Exchange alone can install a non-null validated model. Readiness is
    // derived from this variant, never a separate flag beside a nullable model.
    std::variant<std::monostate, std::shared_ptr<const ValidatedAsset>, std::string> content;
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
    mutable std::mutex                                   mutex;
    ViewSettings                                         settings;
    std::uint64_t                                        epoch{1}, context{}, localRevision{1};
    std::optional<Generation>                            localGeneration;
    bool                                                 available{}, changed{true};
    std::uint32_t                                        serverSampleRate{50};
    std::optional<std::pair<Generation, ValidatedAsset>> capture;
    std::shared_ptr<const Snapshot>                      snapshot;
    std::optional<Publication>                           publication;
    std::optional<Wire::Pose>                            encoded;
    std::unordered_map<std::uint64_t, Remote>            remotes;
    Metrics                                              metrics;
    std::uint64_t                                        localReservation{};
    std::uint64_t                                        graphicsReservation{};

    void ClearPublication()
    {
      ++localRevision;
      localGeneration.reset();
      capture.reset();
      snapshot.reset();
      publication.reset();
      encoded.reset();
      localReservation = 0;
    }

    static std::uint64_t Reservation(const Wire::Descriptor& descriptor)
    {
      // Immutable neutral model and decode scratch, plus
      // compressed transfer and eight buffered complete poses.
      return 2ULL * descriptor.rawBytes + descriptor.compressedBytes + 8ULL * Limits{}.poseBytes;
    }

    std::uint64_t Reserved(std::uint64_t except = 0) const
    {
      std::uint64_t bytes = localReservation + graphicsReservation;
      for (const auto& [id, remote] : remotes)
        if (id != except) bytes += Reservation(remote.descriptor) + remote.sceneBytes;
      return bytes;
    }

public:

    struct Work
    {
      std::uint64_t                                        epoch{}, context{}, localRevision{};
      ViewSettings                                         settings;
      std::optional<std::pair<Generation, ValidatedAsset>> capture;
      std::shared_ptr<const Snapshot>                      snapshot;
    };

    void Configure(ViewSettings value)
    {
      std::lock_guard lock(mutex);
      if (
        settings.publish != value.publish ||
        (value.memoryBytes < settings.memoryBytes && localReservation + graphicsReservation > value.memoryBytes))
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

    void SampleRate(std::uint32_t rate)
    {
      std::lock_guard lock(mutex);
      serverSampleRate   = std::clamp(rate, 1u, 50u);
      metrics.sampleRate = std::min(settings.sampleRate, serverSampleRate);
    }

    void Context(std::uint64_t value, bool ready)
    {
      std::lock_guard lock(mutex);
      if (value != context || available != ready)
      {
        context   = value;
        available = ready;
        snapshot.reset();
        encoded.reset();
        for (auto& [id, remote] : remotes)
          remote.playback.Clear();
        changed = true;
      }
    }

    void Reset()
    {
      std::lock_guard lock(mutex);
      ++epoch;
      context   = 0;
      available = false;
      changed   = true;
      ClearPublication();
      remotes.clear();
      metrics = {};
    }

    bool Submit(Generation generation, ValidatedAsset asset)
    {
      std::lock_guard lock(mutex);
      const auto      bytes = 4 * asset.MemoryBytes() + 8ULL * Limits{}.poseBytes;
      if (!available || !settings.publish || capture || bytes + graphicsReservation > settings.memoryBytes) return false;
      localReservation = bytes;
      while (Reserved() > settings.memoryBytes && !remotes.empty())
        remotes.erase(remotes.begin());
      ++localRevision;
      localGeneration = generation;
      changed         = true;
      capture.emplace(generation, std::move(asset));
      return true;
    }

    void Submit(std::shared_ptr<const Snapshot> pose)
    {
      std::lock_guard lock(mutex);
      if (available && settings.publish && localGeneration == pose->generation) snapshot = std::move(pose);
    }

    Work TakeWork()
    {
      std::lock_guard lock(mutex);
      auto            current = settings;
      current.sampleRate      = std::min(current.sampleRate, serverSampleRate);
      return {epoch, context, localRevision, current, std::exchange(capture, {}), std::exchange(snapshot, {})};
    }

    void Prepared(std::uint64_t workEpoch, std::uint64_t revision, Publication value)
    {
      std::lock_guard lock(mutex);
      if (workEpoch == epoch && revision == localRevision && localGeneration == value.generation && settings.publish)
        publication = std::move(value);
    }

    void PreparationFailed(std::uint64_t workEpoch, std::uint64_t revision, std::string error)
    {
      std::lock_guard lock(mutex);
      if (workEpoch == epoch && revision == localRevision)
      {
        ClearPublication();
        changed       = true;
        metrics.error = std::move(error);
        ++metrics.rejected;
      }
    }

    bool Capturing(Generation generation) const
    {
      std::lock_guard lock(mutex);
      return localGeneration == generation && settings.publish;
    }

    void Encoded(std::uint64_t workEpoch, Wire::Pose value)
    {
      std::lock_guard lock(mutex);
      if (workEpoch == epoch && context == value.context && localGeneration == value.generation && settings.publish)
        encoded = std::move(value);
    }

    struct Output
    {
      bool                       changed{};
      ViewSettings               settings;
      std::uint64_t              localRevision{};
      std::optional<Publication> publication;
      std::optional<Wire::Pose>  pose;
    };

    Output TakeOutput()
    {
      std::lock_guard lock(mutex);
      return {std::exchange(changed, false), settings, localRevision, std::exchange(publication, {}), std::exchange(encoded, {})};
    }

    std::uint64_t Epoch() const
    {
      std::lock_guard lock(mutex);
      return epoch;
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
      if (bytes + Reservation(found->second.descriptor) > settings.memoryBytes - std::min(settings.memoryBytes, Reserved(player)))
        return false;
      found->second.sceneBytes = bytes;
      return true;
    }

    bool GraphicsMemory(std::uint64_t bytes)
    {
      std::lock_guard lock(mutex);
      const auto      others = Reserved() - graphicsReservation;
      if (bytes > settings.memoryBytes - std::min(settings.memoryBytes, others)) return false;
      graphicsReservation = bytes;
      return true;
    }

    bool Offer(const Wire::Offer& offer)
    {
      std::lock_guard lock(mutex);
      if (!settings.receive) return false;
      const auto found = remotes.find(offer.player);
      if (found != remotes.end() && offer.view < found->second.view) return false;
      if (found == remotes.end() && remotes.size() >= settings.maximum) return false;
      const auto sceneBytes = found == remotes.end() ? 0 : found->second.sceneBytes;
      if (Reservation(offer.asset) + sceneBytes > settings.memoryBytes - std::min(settings.memoryBytes, Reserved(offer.player)))
        return false;
      auto& remote = remotes[offer.player];
      if (remote.view != offer.view || remote.descriptor != offer.asset)
      {
        remote            = {offer.player, offer.view, offer.asset};
        remote.sceneBytes = sceneBytes;
      }
      return true;
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

    std::shared_ptr<const ValidatedAsset> AssetFor(const Digest& hash) const
    {
      std::lock_guard lock(mutex);
      for (const auto& [id, remote] : remotes)
        if (remote.descriptor.hash == hash && remote.Asset()) return remote.Asset();
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
      if (
        workEpoch != epoch || found == remotes.end() || found->second.view != wire.view ||
        found->second.descriptor.generation != wire.sample.generation)
        return;
      if (!found->second.playback.Push(std::move(pose), arrivalUs)) ++metrics.dropped;
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
