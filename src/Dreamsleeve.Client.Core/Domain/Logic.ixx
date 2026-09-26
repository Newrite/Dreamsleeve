export module Dreamsleeve.Client.Domain.Logic;

export import Dreamsleeve.Client.Domain;
import std;

export namespace Domain
{

  enum class ErrorCode
  {
    NonFiniteValue,
    InvalidRadius,
    InvalidConfig,
    ChannelMismatch,
    ConflictingMessage,
    DuplicatePlayer,
    DuplicateKey,
    UnknownPlayer,
    UnknownChannel,
    IdentityMismatch,
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

  inline std::unexpected<Error> Failure(ErrorCode code, std::string_view field)
  {
    return std::unexpected{
        Error{code, std::string{field}}
    };
  }

  constexpr char AsciiLower(char value) noexcept
  {
    return value >= 'A' && value <= 'Z' ? static_cast<char>(value + ('a' - 'A')) : value;
  }

}

export namespace Domain::ActorValues
{

  constexpr ActorValue GetCurrent(const ActorValueState& state)
  {
    return std::visit(
      [](const auto& value) -> ActorValue {
        if constexpr (std::is_same_v<std::remove_cvref_t<decltype(value)>, ScalarActorValue>)
          return value.value;
        else
          return value.current;
      },
      state);
  }

  constexpr std::optional<ActorValue> TryGetMaximum(const ActorValueState& state) noexcept
  {
    if (const auto* resource = std::get_if<ResourceActorValue>(&state)) return resource->maximum;
    return std::nullopt;
  }

  void SetCurrent(ActorValueState& state, ActorValue current)
  {
    std::visit(
      [current](auto& value) {
        if constexpr (std::is_same_v<std::remove_cvref_t<decltype(value)>, ScalarActorValue>)
        {
          value.value = current;
        }
        else
        {
          value.current = current;
        }
      },
      state);
  }

}

export namespace Domain::Spatial
{

  // Accepted player state contains canonical FormKeys. A location label is not
  // part of space identity: use WRLD for exteriors and CELL for interiors.
  bool IsSameSpace(const PlayerLocation& left, const PlayerLocation& right)
  {
    return left.location.locationId == right.location.locationId;
  }

  constexpr double DistanceSquared(const Position& left, const Position& right) noexcept
  {
    const double dx = static_cast<double>(left.X) - static_cast<double>(right.X);
    const double dy = static_cast<double>(left.Y) - static_cast<double>(right.Y);
    const double dz = static_cast<double>(left.Z) - static_cast<double>(right.Z);
    return dx * dx + dy * dy + dz * dz;
  }

  double Distance(const Position& left, const Position& right) noexcept
  {
    return std::sqrt(DistanceSquared(left, right));
  }

  // An absent result means different spaces
  // IsWithinRadius supplies a diagnostic when a calculation cannot proceed.
  std::optional<double> TryDistance(const PlayerLocation& left, const PlayerLocation& right)
  {
    if (!IsSameSpace(left, right)) return std::nullopt;
    return Distance(left.position, right.position);
  }

  Result<bool> IsWithinRadius(WorldUnit radius, const PlayerLocation& left, const PlayerLocation& right)
  {
    if (radius < 0.0f) return Detail::Failure(ErrorCode::InvalidRadius, "radius");
    if (!IsSameSpace(left, right)) return false;

    const double wideRadius = static_cast<double>(radius);
    return DistanceSquared(left.position, right.position) <= wideRadius * wideRadius;
  }

}

export namespace Domain::Normalize
{

  // Boundary helpers for game-derived keys. Server-accepted state already
  // carries canonical keys; stores do not normalize it again. Beyond ASCII
  // case folding, identifiers remain opaque: no trimming or Unicode changes.
  std::string PluginName(std::string_view source)
  {
    std::string result{source};
    std::ranges::transform(result, result.begin(), Detail::AsciiLower);
    return result;
  }

  std::string ActorValueKey(std::string_view source)
  {
    std::string result{source};
    std::ranges::transform(result, result.begin(), Detail::AsciiLower);
    return result;
  }

}

export namespace Domain::Players
{

  void ClearGameState(Player& player) noexcept
  {
    player.characterName.reset();
    player.location.reset();
    player.actorValues.clear();
  }

  // Even an equal character name may refer to a different save. Rename by
  // replacing only characterName; a new character always clears telemetry.
  void BeginCharacter(Player& player, CharacterName name)
  {
    ClearGameState(player);
    player.characterName = std::move(name);
  }

}
