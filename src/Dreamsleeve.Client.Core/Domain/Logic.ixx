export module Dreamsleeve.Client.Domain.Logic;

export import Dreamsleeve.Client.Domain;
import std;
import Dreamsleeve.Client.Utils;

export namespace Domain
{

  enum class ErrorCode
  {
    InvalidConfig,
    ChannelMismatch,
    ConflictingMessage,
    DuplicatePlayer,
    DuplicateKey,
    UnknownPlayer,
    UnknownChannel,
    InvalidCursor,
    StaleGeneration
  };

  struct Error
  {
    ErrorCode   code;
    std::string field;

    bool operator==(const Error&) const = default;
  };

  template <class T>
  using Result = std::expected<T, Error>;

  using OperationResult = Result<void>;

  constexpr MessageTime FromUnixMilliseconds(std::int64_t value) noexcept
  {
    return MessageTime{std::chrono::milliseconds{value}};
  }

  constexpr std::int64_t ToUnixMilliseconds(MessageTime value) noexcept
  {
    return value.time_since_epoch().count();
  }

}

// Pure rules of the game domain: math, checks and decisions on plain values.
// The stores, the codec and the game adapter hold state and call these; the
// rules themselves are tested here, without a session, a codec or the game.

export namespace Domain::Calendar
{

  // Days of a Tamriel month (1..12); the calendar has no leap day.
  constexpr std::uint32_t MonthLength(std::uint32_t month) noexcept
  {
    constexpr std::array<std::uint32_t, 12> Lengths{31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    return month >= 1 && month <= 12 ? Lengths[month - 1] : 0;
  }

}

export namespace Domain::Checks
{

  bool Finite(std::floating_point auto... values)
  {
    return (std::isfinite(values) && ...);
  }

  bool Finite(const Position& value)
  {
    return Finite(value.X, value.Y, value.Z);
  }

  bool Finite(const Rotation& value)
  {
    return Finite(value.X, value.Y, value.Z);
  }

  // A form of a named plugin; the same rule for every WRLD, CELL and race key.
  bool ValidKey(const FormKey& value)
  {
    return !value.pluginName.empty() && value.localFormId != InvalidId;
  }

  bool ValidPlacement(const GroundMarkPlacement& value)
  {
    return ValidKey(value.locationId) && Finite(value.position) && Finite(value.heading);
  }

  // The same calendar ranges as the server's GameDate.create: eras 1..99,
  // years 1..99999, Tamriel months of fixed length, 24 hours.
  bool ValidGameDate(const GameDate& value)
  {
    return value.era >= 1 && value.era <= 99 && value.year >= 1 && value.year <= 99999 && value.month >= 1 && value.month <= 12 &&
           value.day >= 1 && value.day <= Calendar::MonthLength(value.month) && value.dayOfWeek <= 6 && value.hour <= 23 &&
           value.minute <= 59;
  }

}

export namespace Domain::Spatial
{

  constexpr double DistanceSquared(const Position& left, const Position& right) noexcept
  {
    const double dx = static_cast<double>(left.X) - static_cast<double>(right.X);
    const double dy = static_cast<double>(left.Y) - static_cast<double>(right.Y);
    const double dz = static_cast<double>(left.Z) - static_cast<double>(right.Z);
    return dx * dx + dy * dy + dz * dz;
  }

  // Straight-line distance in game units; both points are of one space.
  double Distance(const Position& left, const Position& right) noexcept
  {
    return std::sqrt(DistanceSquared(left, right));
  }

  // A space is its WRLD or CELL key; a location label is not part of it.
  bool SameSpace(const PlayerLocation& left, const PlayerLocation& right)
  {
    return left.location.locationId == right.location.locationId;
  }

  // The observer entered another space, or had none before.
  bool SpaceChanged(const std::optional<LocationId>& last, const LocationId& now)
  {
    return !last || *last != now;
  }

  // How far a point is from an observer, when it stands in the observer's space
  // within radius (the boundary included); absent otherwise.
  std::optional<double> Reach(
    const LocationId& observerSpace,
    const Position&   observer,
    const LocationId& space,
    const Position&   point,
    double            radius)
  {
    if (space != observerSpace) return std::nullopt;
    const auto distance = Distance(observer, point);
    if (distance > radius) return std::nullopt;
    return distance;
  }

  // A move that motion must not bridge: into another space, or farther than a
  // teleport threshold within one.
  bool Jumped(const PlayerLocation& from, const PlayerLocation& to, double threshold)
  {
    return !SameSpace(from, to) || DistanceSquared(from.position, to.position) > threshold * threshold;
  }

  // Keeps the `limit` nearest items, nearest first; distanceOf projects an item.
  template <class Item, class Projection>
  void KeepNearest(std::vector<Item>& items, std::size_t limit, Projection distanceOf)
  {
    std::ranges::sort(items, {}, distanceOf);
    if (items.size() > limit) items.resize(limit);
  }

  // Shown within limit; once shown, hidden again only past limit * hysteresis,
  // so a thing at the edge does not flicker as the observer sways.
  bool ShownWithin(double distance, double limit, bool shown, double hysteresis)
  {
    return distance <= (shown ? limit * hysteresis : limit);
  }

}

export namespace Domain::Motion
{

  // The shorter way round from one angle to another, at alpha (0..1) of the turn.
  float BlendAngle(float from, float to, double alpha)
  {
    constexpr auto Turn       = 2.0 * std::numbers::pi;
    const auto     difference = std::remainder(static_cast<double>(to) - from, Turn);
    return static_cast<float>(std::remainder(from + difference * alpha, Turn));
  }

  // Adjacent movement samples of one context replace each other: both absent,
  // or both in one space.
  bool SameContext(const std::optional<PlayerLocation>& previous, const std::optional<PlayerLocation>& next)
  {
    return previous.has_value() == next.has_value() && (!previous || Spatial::SameSpace(*previous, *next));
  }

  // A sample moves a placed player within its space and view.
  void Apply(PlayerLocation& location, const MovementPose& pose)
  {
    location.position    = pose.position;
    location.rotation    = pose.rotation;
    location.sampledAtUs = pose.sampledAtUs;
  }

  // When a sample happened on this receiver's clock: the previous sample's
  // time plus the source's own elapsed microseconds when both carry a source
  // stamp, else the receive time. Absent when the timeline breaks: the source
  // clock restarted or paused beyond maxGap, only one sample is stamped, or
  // the time strays from receipt beyond the interpolation budget (ahead by
  // more than delay, behind by more than maxGap). Ordinary jitter is kept.
  template <class Clock>
  std::optional<typename Clock::time_point> SourceTime(
    typename Clock::time_point previousTime,
    std::uint64_t              previousStampUs,
    std::uint64_t              stampUs,
    typename Clock::time_point receivedAt,
    std::chrono::milliseconds  delay,
    std::chrono::milliseconds  maxGap)
  {
    auto time = receivedAt;
    if (stampUs != 0 && previousStampUs != 0)
    {
      // A source clock restart is a discontinuity, never unsigned wraparound.
      if (stampUs < previousStampUs) return std::nullopt;
      const auto elapsed = stampUs - previousStampUs;
      if (elapsed > static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(maxGap).count())) return std::nullopt;
      time = previousTime + std::chrono::duration_cast<typename Clock::duration>(std::chrono::microseconds{elapsed});
    }
    else if ((stampUs == 0) != (previousStampUs == 0))
      return std::nullopt;
    if (time - receivedAt > delay || receivedAt - time > maxGap) return std::nullopt;
    return time;
  }

  // The pose between two samples of one space at alpha (0..1); a rendered pose
  // is not a new source measurement, so it carries no source stamp.
  PlayerLocation Blend(const PlayerLocation& from, const PlayerLocation& to, double alpha)
  {
    const auto lerp = [alpha](float a, float b) {
      return static_cast<float>(std::lerp(static_cast<double>(a), static_cast<double>(b), alpha));
    };
    auto result     = to;
    result.position = {lerp(from.position.X, to.position.X), lerp(from.position.Y, to.position.Y), lerp(from.position.Z, to.position.Z)};
    result.rotation = {
        BlendAngle(from.rotation.X, to.rotation.X, alpha),
        BlendAngle(from.rotation.Y, to.rotation.Y, alpha),
        BlendAngle(from.rotation.Z, to.rotation.Z, alpha)
    };
    result.sampledAtUs = 0;
    return result;
  }

}

export namespace Domain::Players
{

  // A full profile of the same character without newer motion keeps the
  // motion already known: the server's snapshot may lag behind the stream.
  bool KeepsMotion(const Player& known, const Player& next)
  {
    return known.characterGeneration == next.characterGeneration && known.viewRevision != 0 &&
           (next.viewRevision == 0 || (next.viewRevision == known.viewRevision && next.movementSequence < known.movementSequence));
  }

  // The full profile next as stored: the motion already known stays when
  // KeepsMotion says the profile lags behind the stream.
  Player Merge(const Player& known, Player next)
  {
    if (KeepsMotion(known, next))
    {
      next.viewRevision     = known.viewRevision;
      next.location         = known.location;
      next.movementSequence = known.movementSequence;
    }
    return next;
  }

  // Actor values worth sending again: another key, name or kind, or a
  // resource moved by more than epsilon.
  bool SameActorValues(const ActorValueStorage& left, const ActorValueStorage& right, float epsilon)
  {
    if (left.size() != right.size()) return false;
    for (const auto& [key, info] : left)
    {
      const auto found = right.find(key);
      if (found == right.end() || found->second.displayName != info.displayName) return false;
      const auto* a = std::get_if<ResourceActorValue>(&info.state);
      const auto* b = std::get_if<ResourceActorValue>(&found->second.state);
      if (!a || !b)
      {
        if (!(info.state == found->second.state)) return false;
        continue;
      }
      if (std::abs(a->current - b->current) > epsilon || std::abs(a->maximum - b->maximum) > epsilon) return false;
    }
    return true;
  }

  // A movement sample applies to a placed player of the same view, and only
  // after the samples already applied.
  bool AcceptsMovement(const Player& player, std::uint64_t viewRevision, std::uint64_t sequence)
  {
    return player.location && viewRevision != 0 && player.viewRevision == viewRevision && sequence > player.movementSequence;
  }

}

export namespace Domain::Chat
{

  // A system channel carries only announcements; other kinds never do.
  bool FitsChannel(ChatChannelKind kind, const ChatMessage& message)
  {
    return message.announcement.has_value() == (kind == ChatChannelKind::System);
  }

}

export namespace Domain::Announcements
{

  // Within what the server's welcome announced for a client announcement: an
  // allowed source and lengths in Unicode scalar values.
  bool Admits(const AnnouncementPolicy& policy, ClientAnnouncementSource source, std::string_view text, std::string_view signature)
  {
    using Dreamsleeve::Utils::Text::CodePoints;
    return policy.Allows(source) && CodePoints(text) <= policy.maxTextLength && CodePoints(signature) <= policy.maxSignatureLength;
  }

}
