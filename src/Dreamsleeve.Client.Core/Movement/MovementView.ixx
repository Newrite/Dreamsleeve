export module Dreamsleeve.Client.MovementView;

import std;
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

    static Domain::Result<Ptr> TryCreate(MovementSettings settings = {})
    {
      if (!settings.Valid())
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::InvalidConfig, "movement"}
        };

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

      const auto& samples = found->second.samples;
      const auto  target  = now - settings.delay;
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
    };

    explicit MovementView(MovementSettings value) : settings{value} {}

    static float Angle(float first, float second, double alpha)
    {
      constexpr auto turn       = 2.0 * std::numbers::pi;
      const auto     difference = std::remainder(static_cast<double>(second) - first, turn);
      return static_cast<float>(std::remainder(first + difference * alpha, turn));
    }

    static Domain::PlayerLocation Interpolate(const SamplePoint& first, const SamplePoint& second, Clock::time_point target)
    {
      const double alpha =
        std::chrono::duration<double>(target - first.time).count() / std::chrono::duration<double>(second.time - first.time).count();
      auto        result = second.location;
      const auto& a      = first.location;
      const auto& b      = second.location;
      result.position    = {
          static_cast<float>(std::lerp(static_cast<double>(a.position.X), static_cast<double>(b.position.X), alpha)),
          static_cast<float>(std::lerp(static_cast<double>(a.position.Y), static_cast<double>(b.position.Y), alpha)),
          static_cast<float>(std::lerp(static_cast<double>(a.position.Z), static_cast<double>(b.position.Z), alpha))
      };
      result.rotation =
        {Angle(a.rotation.X, b.rotation.X, alpha), Angle(a.rotation.Y, b.rotation.Y, alpha), Angle(a.rotation.Z, b.rotation.Z, alpha)};
      result.sampledAtUs = 0;  // A rendered pose is not a new source measurement.
      return result;
    }

    bool Discontinuous(const Track& track, const MovementObservation& observation) const
    {
      const auto& previous = track.samples.back().location;
      const auto& next     = *observation.location;
      if (
        track.viewRevision != observation.viewRevision || track.characterGeneration != observation.characterGeneration ||
        previous.location.locationId != next.location.locationId)
        return true;

      if (observation.receivedAt - track.receivedAt > settings.maxGap) return true;

      const double x = static_cast<double>(next.position.X) - previous.position.X;
      const double y = static_cast<double>(next.position.Y) - previous.position.Y;
      const double z = static_cast<double>(next.position.Z) - previous.position.Z;
      return x * x + y * y + z * z > settings.teleportDistance * settings.teleportDistance;
    }

    std::optional<Clock::time_point> MapTime(const Track& track, const MovementObservation& observation) const
    {
      const auto& previous = track.samples.back();
      const auto  stamp    = observation.location->sampledAtUs;
      const auto  oldStamp = previous.location.sampledAtUs;
      auto        time     = observation.receivedAt;
      if (stamp != 0 && oldStamp != 0)
      {
        // The model has already checked context and sample sequence.
        // A source clock restart is a discontinuity, never unsigned wraparound.
        if (stamp < oldStamp) return std::nullopt;

        const auto elapsed = stamp - oldStamp;
        const auto maxUs   = static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(settings.maxGap).count());
        if (elapsed > maxUs) return std::nullopt;

        time = previous.time + std::chrono::microseconds{elapsed};
      }
      else if ((stamp == 0) != (oldStamp == 0))
        return std::nullopt;

      // A visibility entry/snapshot can seed an old source measurement at a
      // recent receive time. Do not stretch its next segment into the future
      // beyond our interpolation budget. Ordinary jitter within delay is kept.
      if (time - observation.receivedAt > settings.delay || observation.receivedAt - time > settings.maxGap) return std::nullopt;

      return time;
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
        track.samples.clear();
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
