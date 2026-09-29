export module Dreamsleeve.Client.Domain;

import std;
export import Dreamsleeve.Protocol;

export namespace Domain
{

  using PlayerId      = std::uint64_t;
  using ChatMessageId = std::uint64_t;
  using ChatChannelId = std::uint64_t;

  using LocalFormId = std::uint32_t;

  using Username        = std::string;
  using DisplayName     = std::string;
  using ChatMessageText = std::string;
  using PluginName      = std::string;
  using LocationName    = std::string;
  using CharacterName   = std::string;
  using ActorValueKey   = std::string;
  using ActorValueName  = std::string;

  using MessageTime = std::chrono::sys_time<std::chrono::milliseconds>;

  using WorldUnit  = float;
  using Radian     = float;
  using ActorValue = float;

  struct ScalarActorValue
  {
    ActorValue value{};

    bool operator==(const ScalarActorValue&) const = default;
  };

  struct ResourceActorValue
  {
    ActorValue current{};
    ActorValue maximum{};

    bool operator==(const ResourceActorValue&) const = default;
  };

  using ActorValueState = std::variant<ScalarActorValue, ResourceActorValue>;

  struct ActorValueInfo
  {
    ActorValueName  displayName{};
    ActorValueState state{};

    bool operator==(const ActorValueInfo&) const = default;
  };

  using ActorValueStorage = std::unordered_map<ActorValueKey, ActorValueInfo>;

  struct FormKey
  {
    PluginName  pluginName{};
    LocalFormId localFormId{};

    // Keys use canonical ASCII casing, supplied by the game adapter or server.
    bool operator==(const FormKey&) const = default;
  };

  using LocationId = FormKey;

  struct Location
  {
    LocationId   locationId{};
    LocationName locationName{};

    bool operator==(const Location&) const = default;
  };

  struct Position
  {
    WorldUnit X{};
    WorldUnit Y{};
    WorldUnit Z{};

    bool operator==(const Position&) const = default;
  };

  struct Rotation
  {
    Radian X{};
    Radian Y{};
    Radian Z{};

    bool operator==(const Rotation&) const = default;
  };

  struct MovementPose
  {
    Position      position{};
    Rotation      rotation{};
    std::uint64_t sampledAtUs{};
  };

  struct PlayerLocation
  {
    Location      location{};
    Position      position{};
    Rotation      rotation{};
    std::uint64_t sampledAtUs{};  // Sender monotonic clock, not UTC.

    bool operator==(const PlayerLocation&) const = default;
  };

  using ActivityKind   = ::Protocol::Chat::ActivityKind;
  using LockDifficulty = ::Protocol::Chat::LockDifficulty;

  struct NamedForm
  {
    FormKey     form{};
    std::string name{};
    bool        operator==(const NamedForm&) const = default;
  };

  struct PlayerActivity
  {
    ActivityKind               kind{ActivityKind::Unknown};
    std::optional<std::string> targetName;
    LockDifficulty             lockDifficulty{LockDifficulty::Unknown};
    std::optional<std::string> menuKey;
    bool                       operator==(const PlayerActivity&) const = default;
  };

  struct PlaceDescription
  {
    std::string worldspaceName;
    std::string locationName;
    std::string nearbyMarkerName;
    std::string markerKind;
    bool        isInterior{};
    bool        operator==(const PlaceDescription&) const = default;
  };

  struct PlayerDetails
  {
    std::optional<NamedForm>        race;
    std::optional<std::uint32_t>    level;
    PlayerActivity                  activity{};
    std::optional<PlaceDescription> place;
    std::optional<std::int64_t>     gameStartedAtUnixMs;
    bool                            operator==(const PlayerDetails&) const = default;
  };

  struct PlayerData final
  {
    PlayerId    playerId{};
    Username    username{};
    DisplayName displayName{};

    bool operator==(const PlayerData&) const = default;
  };

  struct Player
  {
    PlayerData                    data{};
    std::optional<CharacterName>  characterName{};
    std::optional<PlayerLocation> location{};
    ActorValueStorage             actorValues{};
    std::uint64_t                 characterGeneration{};
    PlayerDetails                 details{};
    std::uint64_t                 viewRevision{};
    std::uint64_t                 movementSequence{};
    // In a character whose game name the server refused to publish;
    // characterName is then empty and display falls back to the profile.
    bool characterNameWithheld{};

    bool operator==(const Player&) const = default;
  };

  // UTF-8 byte range of a message text.
  struct TextSpan
  {
    std::uint32_t start{};
    std::uint32_t length{};

    bool operator==(const TextSpan&) const = default;
  };

  struct ChatMessage final
  {
    ChatMessageId   messageId{};
    ChatChannelId   channelId{};
    PlayerData      author{};
    ChatMessageText messageText{};
    MessageTime     sentAt{};
    // Published character name at sending; absent for old history and outside a character.
    std::optional<CharacterName> characterName{};
    // Ranges the server marked without refusing the message; ascending, disjoint.
    std::vector<TextSpan> flagged{};

    bool operator==(const ChatMessage&) const = default;
  };

}
