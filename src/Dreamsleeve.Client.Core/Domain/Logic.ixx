export module Dreamsleeve.Client.Domain.Logic;

export import Dreamsleeve.Client.Domain;
import std;

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

namespace Domain::Detail
{

  constexpr char AsciiLower(char value) noexcept
  {
    return value >= 'A' && value <= 'Z' ? static_cast<char>(value + ('a' - 'A')) : value;
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

  // Straight-line distance in game units; the caller compares points of one space.
  double Distance(const Position& left, const Position& right) noexcept
  {
    return std::sqrt(DistanceSquared(left, right));
  }

}

export namespace Domain::Normalize
{

  // Boundary helper for game-derived plugin names. Server-accepted state already
  // carries canonical keys; stores do not normalize it again. Beyond ASCII case
  // folding, identifiers remain opaque: no trimming or Unicode changes.
  std::string PluginName(std::string_view source)
  {
    std::string result{source};
    std::ranges::transform(result, result.begin(), Detail::AsciiLower);
    return result;
  }

}
