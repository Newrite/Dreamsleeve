export module Dreamsleeve.Client.Domain.Logic;

export import Dreamsleeve.Client.Domain;
import std;

export namespace Domain
{

  enum class ErrorCode
  {
    InvalidId,
    InvalidText,
    InvalidUtf8,
    InvalidLocalFormId,
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

  constexpr bool IsAsciiLetterOrDigit(char value) noexcept
  {
    return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') || (value >= '0' && value <= '9');
  }

  // Unicode White_Space, matching the server's Char.IsWhiteSpace checks.
  constexpr bool IsWhitespace(char32_t value) noexcept
  {
    return (value >= U'\t' && value <= U'\r') || value == U' ' || value == 0x0085 || value == 0x00A0 || value == 0x1680 ||
           (value >= 0x2000 && value <= 0x200A) || value == 0x2028 || value == 0x2029 || value == 0x202F || value == 0x205F ||
           value == 0x3000;
  }

  constexpr bool IsControl(char32_t value) noexcept
  {
    return value <= 0x001F || (value >= 0x007F && value <= 0x009F);
  }

  // Reject overlong encodings, surrogate code points, truncated sequences,
  // stray continuation bytes, and code points outside Unicode's scalar range.
  bool ReadScalar(std::string_view text, std::size_t& offset, char32_t& scalar) noexcept
  {
    const auto first = static_cast<unsigned char>(text[offset++]);
    if (first < 0x80)
    {
      scalar = first;
      return true;
    }

    std::size_t remaining{};
    char32_t    minimum{};
    if (first >= 0xC2 && first <= 0xDF)
    {
      remaining = 1;
      minimum   = 0x80;
      scalar    = first & 0x1F;
    }
    else if (first >= 0xE0 && first <= 0xEF)
    {
      remaining = 2;
      minimum   = 0x800;
      scalar    = first & 0x0F;
    }
    else if (first >= 0xF0 && first <= 0xF4)
    {
      remaining = 3;
      minimum   = 0x10000;
      scalar    = first & 0x07;
    }
    else
    {
      return false;
    }

    if (text.size() - offset < remaining) return false;

    for (std::size_t index = 0; index < remaining; ++index)
    {
      const auto next = static_cast<unsigned char>(text[offset++]);
      if ((next & 0xC0) != 0x80) return false;
      scalar = (scalar << 6) | (next & 0x3F);
    }

    return scalar >= minimum && scalar <= 0x10FFFF && !(scalar >= 0xD800 && scalar <= 0xDFFF);
  }

  struct TextProperties
  {
    char32_t first{};
    char32_t last{};
  };

  // Limits are the codec/configuration's policy. This layer only checks shape;
  // it neither truncates input nor normalizes display text.
  Result<TextProperties> InspectText(std::string_view text, std::string_view field, bool multiline = false)
  {
    std::size_t    offset{};
    bool           nonWhitespace    = false;
    bool           forbiddenControl = false;
    TextProperties properties;

    while (offset < text.size())
    {
      const bool first = offset == 0;
      char32_t   scalar{};
      if (!ReadScalar(text, offset, scalar)) return Failure(ErrorCode::InvalidUtf8, field);

      if (first) properties.first = scalar;
      properties.last  = scalar;
      nonWhitespace    = nonWhitespace || !IsWhitespace(scalar);
      forbiddenControl = forbiddenControl || (multiline ? IsControl(scalar) && scalar != U'\r' && scalar != U'\n' && scalar != U'\t'
                                                        : IsControl(scalar) || scalar == 0x2028 || scalar == 0x2029);
    }

    if (!nonWhitespace || forbiddenControl) return Failure(ErrorCode::InvalidText, field);

    return properties;
  }

  OperationResult ValidateText(std::string_view text, std::string_view field, bool multiline = false)
  {
    if (auto result = InspectText(text, field, multiline); !result) return std::unexpected{std::move(result.error())};
    return {};
  }

  OperationResult ValidateUsername(std::string_view username)
  {
    if (auto result = ValidateText(username, "Username"); !result) return result;
    if (!std::ranges::all_of(username, [](char value) { return IsAsciiLetterOrDigit(value) || value == '_' || value == '.'; }))
      return Failure(ErrorCode::InvalidText, "Username");
    return {};
  }

  OperationResult ValidatePluginName(std::string_view plugin)
  {
    if (auto result = ValidateText(plugin, "PluginName"); !result) return result;
    if (plugin.size() <= 4 || plugin.find_first_of("<>:\"/\\|?*") != std::string_view::npos)
      return Failure(ErrorCode::InvalidText, "PluginName");

    const auto extension = plugin.substr(plugin.size() - 4);
    const auto matches   = [extension](std::string_view expected) {
      return std::ranges::equal(extension, expected, [](char left, char right) { return AsciiLower(left) == right; });
    };
    if (!matches(".esm") && !matches(".esp") && !matches(".esl")) return Failure(ErrorCode::InvalidText, "PluginName");

    return ValidateText(plugin.substr(0, plugin.size() - 4), "PluginName");
  }

  OperationResult ValidateActorValueKey(std::string_view key)
  {
    if (auto result = ValidateText(key, "ActorValueKey"); !result) return result;
    const auto separator = key.find(':');
    if (separator == std::string_view::npos || separator == 0 || separator + 1 == key.size())
      return Failure(ErrorCode::InvalidText, "ActorValueKey");
    if (!std::ranges::all_of(key.substr(0, separator), [](char value) {
          return IsAsciiLetterOrDigit(value) || value == '.' || value == '_' || value == '-';
        }))
      return Failure(ErrorCode::InvalidText, "ActorValueKey");

    const auto name = InspectText(key.substr(separator + 1), "ActorValueKey");
    if (!name) return std::unexpected{name.error()};
    if (IsWhitespace(name->first) || IsWhitespace(name->last)) return Failure(ErrorCode::InvalidText, "ActorValueKey");
    return {};
  }

  OperationResult ValidateId(std::uint64_t id, std::string_view field)
  {
    if (id == 0) return Failure(ErrorCode::InvalidId, field);
    return {};
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

  bool IsFinite(const ActorValueState& state)
  {
    return std::visit(
      [](const auto& value) {
        if constexpr (std::is_same_v<std::remove_cvref_t<decltype(value)>, ScalarActorValue>)
          return std::isfinite(value.value);
        else
          return std::isfinite(value.current) && std::isfinite(value.maximum);
      },
      state);
  }

  OperationResult SetCurrent(ActorValueState& state, ActorValue current)
  {
    if (!std::isfinite(current)) return Detail::Failure(ErrorCode::NonFiniteValue, "ActorValue.current");
    if (const auto* resource = std::get_if<ResourceActorValue>(&state))
    {
      if (!std::isfinite(resource->maximum)) return Detail::Failure(ErrorCode::NonFiniteValue, "ActorValue.maximum");
      state = ResourceActorValue{current, resource->maximum};
    }
    else
    {
      state = ScalarActorValue{current};
    }
    return {};
  }

}

export namespace Domain::Spatial
{

  bool IsFinite(const Position& value) noexcept
  {
    return std::isfinite(value.X) && std::isfinite(value.Y) && std::isfinite(value.Z);
  }

  bool IsFinite(const Rotation& value) noexcept
  {
    return std::isfinite(value.X) && std::isfinite(value.Y) && std::isfinite(value.Z);
  }

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

  // An absent result means different spaces or invalid coordinates. For a
  // diagnostic error, validate incoming PlayerLocation before storing it.
  std::optional<double> TryDistance(const PlayerLocation& left, const PlayerLocation& right)
  {
    if (!IsSameSpace(left, right) || !IsFinite(left.position) || !IsFinite(right.position)) return std::nullopt;
    return Distance(left.position, right.position);
  }

  Result<bool> IsWithinRadius(WorldUnit radius, const PlayerLocation& left, const PlayerLocation& right)
  {
    if (!std::isfinite(radius) || radius < 0.0f) return Detail::Failure(ErrorCode::InvalidRadius, "radius");
    if (!IsFinite(left.position) || !IsFinite(right.position)) return Detail::Failure(ErrorCode::NonFiniteValue, "Position");
    if (!IsSameSpace(left, right)) return false;

    const double wideRadius = static_cast<double>(radius);
    return DistanceSquared(left.position, right.position) <= wideRadius * wideRadius;
  }

}

export namespace Domain::Validation
{

  OperationResult ActorValueKey(std::string_view value)
  {
    return Detail::ValidateActorValueKey(value);
  }

  OperationResult Validate(const Position& value)
  {
    if (!Spatial::IsFinite(value)) return Detail::Failure(ErrorCode::NonFiniteValue, "Position");
    return {};
  }

  OperationResult Validate(const Rotation& value)
  {
    if (!Spatial::IsFinite(value)) return Detail::Failure(ErrorCode::NonFiniteValue, "Rotation");
    return {};
  }

  OperationResult Validate(const ActorValueState& value)
  {
    if (!ActorValues::IsFinite(value)) return Detail::Failure(ErrorCode::NonFiniteValue, "ActorValue");
    return {};
  }

  OperationResult Validate(const ActorValueInfo& value)
  {
    if (auto result = Detail::ValidateText(value.displayName, "ActorValueName"); !result) return result;
    return Validate(value.state);
  }

  OperationResult Validate(const FormKey& value)
  {
    if (value.localFormId == 0 || value.localFormId > 0x00FFFFFFu) return Detail::Failure(ErrorCode::InvalidLocalFormId, "LocalFormId");
    return Detail::ValidatePluginName(value.pluginName);
  }

  OperationResult Validate(const Location& value)
  {
    if (auto result = Validate(value.locationId); !result) return result;
    return Detail::ValidateText(value.locationName, "LocationName");
  }

  OperationResult Validate(const PlayerData& value)
  {
    if (auto result = Detail::ValidateId(value.playerId, "PlayerId"); !result) return result;
    if (auto result = Detail::ValidateUsername(value.username); !result) return result;
    return Detail::ValidateText(value.displayName, "DisplayName");
  }

  OperationResult Validate(const PlayerLocation& value)
  {
    if (auto result = Validate(value.location); !result) return result;
    if (auto result = Validate(value.position); !result) return result;
    return Validate(value.rotation);
  }

  OperationResult Validate(const Player& value)
  {
    if (auto result = Validate(value.data); !result) return result;
    if (value.characterName)
    {
      if (auto result = Detail::ValidateText(*value.characterName, "CharacterName"); !result) return result;
    }
    if (value.location)
    {
      if (auto result = Validate(*value.location); !result) return result;
    }
    for (const auto& [key, info] : value.actorValues)
    {
      if (auto result = Detail::ValidateActorValueKey(key); !result) return result;
      if (auto result = Validate(info); !result) return result;
    }
    return {};
  }

  OperationResult Validate(const ChatMessage& value)
  {
    if (auto result = Detail::ValidateId(value.messageId, "ChatMessageId"); !result) return result;
    if (auto result = Detail::ValidateId(value.channelId, "ChatChannelId"); !result) return result;
    if (auto result = Validate(value.author); !result) return result;
    return Detail::ValidateText(value.messageText, "ChatMessageText", true);
  }

}

export namespace Domain::Normalize
{

  // Imported machine identifiers are opaque UTF-8 beyond ASCII case folding:
  // do not trim, apply locale-sensitive casing, or normalize them to NFC.
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

  std::string Username(std::string_view source)
  {
    std::size_t offset{};
    std::size_t begin = source.size();
    std::size_t end{};
    bool        validUtf8 = true;
    while (offset < source.size())
    {
      const auto start = offset;
      char32_t   scalar{};
      if (!Detail::ReadScalar(source, offset, scalar))
      {
        validUtf8 = false;
        break;
      }
      if (!Detail::IsWhitespace(scalar))
      {
        begin = std::min(begin, start);
        end   = offset;
      }
    }

    // Match .NET Trim's Unicode White_Space handling. Keep malformed input
    // intact so normalization cannot hide invalid UTF-8 from validation.
    if (validUtf8) source = end == 0 ? std::string_view{} : source.substr(begin, end - begin);
    std::string result{source};
    std::ranges::transform(result, result.begin(), Detail::AsciiLower);
    return result;
  }

  Result<PlayerData> Profile(PlayerData value)
  {
    // Validate controls before trimming so a newline is not silently accepted.
    if (auto result = Detail::ValidateText(value.username, "Username"); !result) return std::unexpected{std::move(result.error())};
    value.username = Username(value.username);
    if (auto result = Validation::Validate(value); !result) return std::unexpected{std::move(result.error())};
    return value;
  }

  Result<Player> PlayerValue(Player value)
  {
    auto profile = Profile(std::move(value.data));
    if (!profile) return std::unexpected{std::move(profile.error())};
    value.data = std::move(*profile);

    if (value.location)
    {
      auto& plugin = value.location->location.locationId.pluginName;
      plugin       = PluginName(plugin);
    }

    ActorValueStorage normalized;
    normalized.reserve(value.actorValues.size());
    for (auto& [key, info] : value.actorValues)
    {
      if (!normalized.try_emplace(ActorValueKey(key), std::move(info)).second)
        return Detail::Failure(ErrorCode::DuplicateKey, "ActorValueKey");
    }
    value.actorValues = std::move(normalized);

    if (auto result = Validation::Validate(value); !result) return std::unexpected{std::move(result.error())};
    return value;
  }

  Result<ChatMessage> Message(ChatMessage value)
  {
    auto profile = Profile(std::move(value.author));
    if (!profile) return std::unexpected{std::move(profile.error())};
    value.author = std::move(*profile);
    if (auto result = Validation::Validate(value); !result) return std::unexpected{std::move(result.error())};
    return value;
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
  OperationResult BeginCharacter(Player& player, CharacterName name)
  {
    if (auto result = Detail::ValidateText(name, "CharacterName"); !result) return result;
    ClearGameState(player);
    player.characterName = std::move(name);
    return {};
  }

}
