export module Dreamsleeve.Client.MovementView;

import std;
import Dreamsleeve.Client.PlayoutClock;
export import Dreamsleeve.Client.StateUpdateQueue;
export import Dreamsleeve.Client.Config;

export namespace Dreamsleeve::Client
{

  // Game-thread owned. Apply drained state once per frame, then Sample at the
  // frame time. No access to the live network model and no background worker.
  class MovementView final
  {
public:

    using Clock = MovementClock;
    using Ptr   = std::unique_ptr<MovementView>;

    // The settings passed ValidateClientSettings.
    static Ptr Create(MovementSettings settings = {})
    {
      return Ptr{new MovementView{settings}};
    }

    void Apply(const StateUpdateBatch& batch, Clock::time_point now = Clock::now())
    {
      if (batch.requiresSnapshot)
      {
        tracks.clear();
        ready = false;
      }

      for (const auto& update : batch.updates)
        std::visit([this, now](const auto& value) { ApplyOne(value, now); }, update);
    }

    std::optional<Domain::PlayerLocation> Sample(Domain::PlayerId id, Clock::time_point now = Clock::now()) const
    {
      const auto found = tracks.find(id);
      if (found == tracks.end()) return std::nullopt;

      const auto&             samples = found->second.samples;
      const Clock::time_point target{std::chrono::duration_cast<Clock::duration>(
        std::chrono::microseconds{found->second.clock.At(Microseconds(now))})};
      if (target <= samples.front().time) return samples.front().location;

      for (std::size_t index = 1; index < samples.size(); ++index)
      {
        if (target <= samples[index].time) return Interpolate(samples[index - 1], samples[index], target);
      }

      // No extrapolation: a stopped/lost stream holds the last known location.
      return samples.back().location;
    }

    std::size_t HistorySize(Domain::PlayerId id) const noexcept
    {
      const auto found = tracks.find(id);
      return found == tracks.end() ? 0 : found->second.samples.size();
    }

private:

    struct SamplePoint
    {
      Clock::time_point      time;
      Domain::PlayerLocation location;
    };

    struct Track
    {
      std::uint64_t           viewRevision{};
      std::uint64_t           characterGeneration{};
      Clock::time_point       receivedAt{};
      std::deque<SamplePoint> samples;
      PlayoutClock            clock;
    };

    static std::uint64_t Microseconds(Clock::time_point value)
    {
      return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(value.time_since_epoch()).count());
    }

    explicit MovementView(MovementSettings value) : settings{value} {}

    static Domain::PlayerLocation Interpolate(const SamplePoint& first, const SamplePoint& second, Clock::time_point target)
    {
      const double alpha =
        std::chrono::duration<double>(target - first.time).count() / std::chrono::duration<double>(second.time - first.time).count();
      return Domain::Motion::Blend(first.location, second.location, alpha);
    }

    // Another view, character or space, a pause beyond maxGap or a teleport: snap.
    bool Discontinuous(const Track& track, const MovementObservation& observation) const
    {
      return track.viewRevision != observation.viewRevision || track.characterGeneration != observation.characterGeneration ||
             observation.receivedAt < track.receivedAt || observation.receivedAt - track.receivedAt > settings.maxGap ||
             Domain::Spatial::Jumped(track.samples.back().location, *observation.location, settings.teleportDistance);
    }

    std::optional<Clock::time_point> MapTime(const Track& track, const MovementObservation& observation) const
    {
      // The model has already checked context and sample sequence.
      const auto& previous = track.samples.back();
      return Domain::Motion::SourceTime<Clock>(
        previous.time,
        previous.location.sampledAtUs,
        observation.location->sampledAtUs,
        observation.receivedAt,
        track.samples.size() < 2 ? settings.delay : settings.maxGap,
        settings.maxGap);
    }

    void Observe(const MovementObservation& observation)
    {
      if (!observation.location)
      {
        tracks.erase(observation.playerId);
        return;
      }

      auto& track = tracks[observation.playerId];
      if (
        !track.samples.empty() && track.characterGeneration == observation.characterGeneration &&
        track.viewRevision == observation.viewRevision && track.samples.back().location == *observation.location)
        return;

      const auto mapped = track.samples.empty() || Discontinuous(track, observation) ? std::nullopt : MapTime(track, observation);
      const auto time   = mapped.value_or(observation.receivedAt);
      if (!mapped)
      {
        track.samples.clear();
        track.clock.Clear();
      }
      else if (time <= track.samples.back().time)
      {
        // Co-timed observations replace, so interpolation never divides by zero.
        track.samples.back().location = *observation.location;
        track.receivedAt              = observation.receivedAt;
        return;
      }

      track.viewRevision        = observation.viewRevision;
      track.characterGeneration = observation.characterGeneration;
      track.receivedAt          = observation.receivedAt;
      track.clock.Push(
        Microseconds(time),
        Microseconds(observation.receivedAt),
        1,
        std::chrono::duration_cast<std::chrono::microseconds>(settings.delay).count(),
        settings.historyCapacity);
      track.samples.push_back({time, *observation.location});
      while (track.samples.size() > settings.historyCapacity)
        track.samples.pop_front();
    }

    void Replace(const std::vector<Domain::Player>& players, Clock::time_point now)
    {
      tracks.clear();
      for (const auto& player : players)
        Observe({player.data.playerId, player.characterGeneration, now, player.location, player.viewRevision});
    }

    bool Older(std::uint64_t nextGeneration, std::uint64_t nextRevision) const noexcept
    {
      return hasCursor && (nextGeneration < generation || (nextGeneration == generation && nextRevision < revision));
    }

    void ApplyOne(const ClientSnapshot& snapshot, Clock::time_point now)
    {
      if (Older(snapshot.generation, snapshot.revision)) return;

      Replace(snapshot.players, snapshot.observedAt == Clock::time_point{} ? now : snapshot.observedAt);
      generation = snapshot.generation;
      revision   = snapshot.revision;
      hasCursor = ready = true;
    }

    void ApplyOne(const ClientStateDelta& delta, Clock::time_point now)
    {
      if (Older(delta.generation, delta.revision) || (hasCursor && delta.generation == generation && delta.revision == revision)) return;
      if (!ready || delta.generation != generation)
      {
        tracks.clear();
        ready      = false;
        generation = delta.generation;
        revision   = delta.revision;
        hasCursor  = true;
        return;  // The state queue must recover with a snapshot before deltas.
      }

      if (delta.playersReplaced)
        Replace(delta.players, delta.observedAt == Clock::time_point{} ? now : delta.observedAt);
      else
        for (const auto& observation : delta.movement)
          Observe(observation);

      for (const auto id : delta.removedPlayers)
        tracks.erase(id);
      revision = delta.revision;
    }

    MovementSettings                            settings;
    std::unordered_map<Domain::PlayerId, Track> tracks;
    std::uint64_t                               generation{};
    std::uint64_t                               revision{};
    bool                                        hasCursor{};
    bool                                        ready{};
  };

}
