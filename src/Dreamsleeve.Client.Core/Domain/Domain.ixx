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

  // A pseudonymous profile is what others see of a player who hides their
  // names: username empty, displayName the server pseudonym, no character name.
  struct PlayerData final
  {
    PlayerId    playerId{};
    Username    username{};
    DisplayName displayName{};
    bool        pseudonymous{};

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

  // Channel entities; the UI "all" view is an aggregate of them, not a channel.
  using ChatChannelKind          = ::Protocol::Chat::ChatChannelKind;
  using AnnouncementSource       = ::Protocol::Chat::AnnouncementSource;
  using AnnouncementKind         = ::Protocol::Chat::AnnouncementKind;
  using ClientAnnouncementSource = ::Protocol::Chat::ClientAnnouncementSource;

  // Marks a message of a system channel. The server assigns the source; the
  // signature is the requesting mod's own label and never raises trust.
  struct Announcement
  {
    AnnouncementSource source{AnnouncementSource::Server};
    AnnouncementKind   kind{AnnouncementKind::Announcement};
    std::string        signature{};

    bool operator==(const Announcement&) const = default;
  };

  // Client announcement rules announced in the welcome; the server still decides.
  struct AnnouncementPolicy
  {
    std::vector<ClientAnnouncementSource> allowedSources{};
    std::uint32_t                         maxTextLength{};       // Unicode scalar values.
    std::uint32_t                         maxSignatureLength{};  // Unicode scalar values.

    bool Allows(ClientAnnouncementSource source) const
    {
      return std::ranges::find(allowedSources, source) != allowedSources.end();
    }

    bool operator==(const AnnouncementPolicy&) const = default;
  };

  struct ChatMessage final
  {
    ChatMessageId messageId{};
    ChatChannelId channelId{};
    // Absent only for server announcements: the system is not a player.
    std::optional<PlayerData> author{};
    ChatMessageText           messageText{};
    MessageTime               sentAt{};
    // Published character name at sending; absent for old history and outside a character.
    std::optional<CharacterName> characterName{};
    // Ranges the server marked without refusing the message; ascending, disjoint.
    std::vector<TextSpan> flagged{};
    // Present exactly on messages of a system channel.
    std::optional<Announcement> announcement{};

    bool operator==(const ChatMessage&) const = default;
  };

  using GroundMarkId   = std::uint64_t;
  using GroundMarkKind = ::Protocol::Chat::GroundMarkKind;

  // Where the others see this player's server pseudonym: nowhere, everywhere,
  // or in presence and chat while ground marks keep the real profile.
  using HiddenIdentity = ::Protocol::Chat::HiddenIdentity;

  // Where a mark stands: the space, the point and the author's heading (Z
  // angle, radians) so the visual can face the way the author looked.
  struct GroundMarkPlacement
  {
    LocationId locationId{};
    Position   position{};
    Radian     heading{};

    bool operator==(const GroundMarkPlacement&) const = default;
  };

  // The in-game calendar at placement as the author's game showed it. Month
  // 1..12 (1 is Morning Star), day of week 0..6 (0 is Sundas); the vanilla
  // calendar has no era variable, its date line prints the Fourth Era (4).
  // Flavour for readers: order and lifetime follow createdAt.
  struct GameDate
  {
    std::uint32_t era{};
    std::uint32_t year{};
    std::uint32_t month{};
    std::uint32_t day{};
    std::uint32_t dayOfWeek{};
    std::uint32_t hour{};
    std::uint32_t minute{};

    bool operator==(const GameDate&) const = default;
  };

  // Persistent server data shown near the player: a note a player left or the
  // place a character died. Not a chat message: never in history or bubbles.
  struct GroundMark final
  {
    GroundMarkId markId{};
    // Author profile at the time of sending; the mark itself stores only the ID.
    PlayerData     author{};
    GroundMarkKind kind{GroundMarkKind::Note};
    // Note text, or the death label: the killer's name or one word of cause, possibly empty.
    std::string           text{};
    std::vector<TextSpan> flagged{};
    GroundMarkPlacement   placement{};
    MessageTime           createdAt{};
    // Published character name at placement; absent outside a character or when withheld.
    std::optional<CharacterName> characterName{};
    // The author's in-game date at placement; absent on marks stored before protocol 12.
    std::optional<GameDate> gameDate{};

    bool operator==(const GroundMark&) const = default;
  };

}
