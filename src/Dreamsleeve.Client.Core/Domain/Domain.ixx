export module Dreamsleeve.Client.Domain;

import std;
export import Dreamsleeve.Protocol;

export namespace Domain
{

  using PlayerId      = std::uint64_t;
  using ChatMessageId = std::uint64_t;
  using ChatChannelId = std::uint64_t;

  // No player, message, channel, mark, form or request has ID 0: it means
  // "none" in memory, and protobuf reads an absent ID as 0 as well.
  constexpr std::uint64_t InvalidId = 0;

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

  // Whole points, as the protocol carries them. The game does not clamp
  // readings: a hit larger than the health left makes current negative.
  struct ResourceActorValue
  {
    std::int32_t current{};
    std::int32_t maximum{};

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

  // What changed in a player's actor values: keys removed first, then the new
  // and changed readings.
  struct ActorValuesPatch
  {
    std::vector<ActorValueKey>                            removed;
    std::vector<std::pair<ActorValueKey, ActorValueInfo>> set;
    bool                                                  operator==(const ActorValuesPatch&) const = default;
  };

  // Present components replace the old ones; an empty inner value clears an
  // optional component.
  struct PlayerDetailsPatch
  {
    std::optional<std::optional<NamedForm>>        race;
    std::optional<std::optional<std::uint32_t>>    level;
    std::optional<PlayerActivity>                  activity;
    std::optional<std::optional<PlaceDescription>> place;
    std::optional<std::optional<std::int64_t>>     gameStartedAtUnixMs;
    bool                                           operator==(const PlayerDetailsPatch&) const = default;
  };

  // A pseudonymous profile is what others see of a player who hides their
  // names: username empty, displayName the server pseudonym, no character name
  // and no name color.
  struct PlayerData final
  {
    PlayerId    playerId{};
    Username    username{};
    DisplayName displayName{};
    bool        pseudonymous{};
    // 0xRRGGBB the player chose for their name in chat; absent for a pseudonym.
    std::optional<std::uint32_t> nameColor;

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

  // A mute on this player: chat, notes, mod announcements and the display name
  // are refused until it ends. Reading is not limited.
  struct MuteState
  {
    std::string                 reason;
    std::optional<std::int64_t> untilUnixMs;  // Absent: until lifted.

    bool operator==(const MuteState&) const = default;
  };

  using SessionEndReason = ::Protocol::Chat::SessionEndReason;

  // Why the server ended this player's session, or refused a sign-in (a ban).
  struct SessionEnd
  {
    SessionEndReason            reason{SessionEndReason::Kicked};
    std::string                 text;         // The moderator's reason; empty when access was revoked.
    std::optional<std::int64_t> untilUnixMs;  // A ban with an end.

    bool operator==(const SessionEnd&) const = default;
  };

  using PlayerRole   = ::Protocol::Chat::PlayerRole;
  using SanctionKind = ::Protocol::Chat::SanctionKind;

  // A mute or a ban in force, as moderators list it: the player by PlayerId
  // only, the name is the client's to resolve.
  struct Sanction
  {
    PlayerId                    playerId{};
    SanctionKind                kind{SanctionKind::Mute};
    std::string                 reason;
    std::int64_t                issuedAtUnixMs{};
    std::optional<std::int64_t> untilUnixMs;  // Absent: until lifted.

    bool operator==(const Sanction&) const = default;
  };

  using GuildId             = std::uint64_t;
  using GuildTitleId        = std::uint64_t;
  using GuildRole           = ::Protocol::Chat::GuildRole;
  using GuildRemovalReason  = ::Protocol::Chat::GuildRemovalReason;
  using GuildApplicationEnd = ::Protocol::Chat::GuildApplicationEnd;
  using GuildCatalogSort    = ::Protocol::Chat::GuildCatalogSort;

  // A guild's chat channel: above every server-wide channel (guild.proto).
  inline constexpr ChatChannelId GuildChannelBase = 4294967296ULL;

  constexpr bool IsGuildChannel(ChatChannelId channelId) noexcept
  {
    return channelId > GuildChannelBase;
  }

  // A member as guildmates see them: the real profile, never a pseudonym.
  struct GuildMember
  {
    PlayerData                  profile{};
    GuildRole                   role{GuildRole::Member};
    bool                        online{};
    std::optional<MuteState>    mute;     // Muted in this guild: reading only.
    std::int64_t                joinedAtUnixMs{};
    std::optional<GuildTitleId> titleId;  // One of the guild's custom roles.

    bool operator==(const GuildMember&) const = default;
  };

  // A custom role of a guild, such as "Казначей": cosmetic, the master's to
  // create and assign.
  struct GuildTitle
  {
    GuildTitleId titleId{};
    std::string  name;

    bool operator==(const GuildTitle&) const = default;
  };

  // A player's request to join, as the master and officers see it: the real
  // profile, like a member's.
  struct GuildJoinRequest
  {
    PlayerData   profile{};
    std::int64_t createdAtUnixMs{};
    std::int64_t expiresAtUnixMs{};

    bool operator==(const GuildJoinRequest&) const = default;
  };

  // A guild of this player; its chat is the channel channelId.
  struct Guild
  {
    GuildId                       guildId{};
    std::string                   name;
    ChatChannelId                 channelId{};
    std::int64_t                  createdAtUnixMs{};
    std::vector<GuildMember>      members;
    std::vector<GuildTitle>       titles;        // Oldest first.
    std::string                   info;          // What the master tells about it; empty: nothing.
    bool                          listed{true};  // Shown in the public catalog.
    std::vector<GuildJoinRequest> joinRequests;  // Oldest first; only the master and officers receive them.

    bool operator==(const Guild&) const = default;
  };

  // A join request of this player, waiting for the guild's answer.
  struct GuildApplication
  {
    GuildId      guildId{};
    std::string  guildName;
    std::int64_t expiresAtUnixMs{};

    bool operator==(const GuildApplication&) const = default;
  };

  // An invitation waiting for this player's answer. The inviter is a player
  // ID, resolved like any other: outside the guild a pseudonym stays one.
  struct GuildInvite
  {
    GuildId      guildId{};
    std::string  guildName;
    PlayerId     invitedBy{};
    std::int64_t expiresAtUnixMs{};

    bool operator==(const GuildInvite&) const = default;
  };

  // The server's guild limits; a lowered limit removes nobody.
  struct GuildLimits
  {
    std::uint32_t maxGuildsPerPlayer{};  // Own guilds included.
    std::uint32_t maxMembers{};
    std::uint32_t nameMinLength{};       // Unicode scalar values; custom role names too.
    std::uint32_t nameMaxLength{};
    std::uint32_t maxTitles{};           // Custom roles of one guild.
    std::uint32_t infoMaxLength{};       // Unicode scalar values of the guild information.
    std::uint32_t maxJoinRequests{};     // Pending join requests of one guild.
    std::uint32_t maxApplications{};     // Pending join requests of one player.

    bool operator==(const GuildLimits&) const = default;
  };

  // The last catalog entry received: the next page follows it.
  struct GuildCatalogAfter
  {
    GuildId       guildId{};
    std::string   name;
    std::uint32_t members{};

    bool operator==(const GuildCatalogAfter&) const = default;
  };

  // A search of the public catalog; the server decides the page size.
  struct GuildCatalogQuery
  {
    std::string                      search;      // Part of the name; empty: any.
    std::uint32_t                    minMembers{};
    std::optional<std::uint32_t>     maxMembers;  // Absent: no upper bound.
    bool                             openOnly{};  // Only guilds with free places.
    GuildCatalogSort                 sort{GuildCatalogSort::Name};
    std::optional<GuildCatalogAfter> after;       // Absent: the first page.

    bool operator==(const GuildCatalogQuery&) const = default;
  };

  struct GuildCatalogEntry
  {
    GuildId       guildId{};
    std::string   name;
    std::string   info;
    std::uint32_t members{};
    std::int64_t  createdAtUnixMs{};

    bool operator==(const GuildCatalogEntry&) const = default;
  };

  struct GuildCatalogPage
  {
    std::vector<GuildCatalogEntry> guilds;
    bool                           more{};   // Another page follows the last entry.
    std::uint32_t                  total{};  // Every guild the query matches.

    bool operator==(const GuildCatalogPage&) const = default;
  };

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

  // Where the character stands and when: a mark placed there now.
  struct MarkSpot
  {
    GroundMarkPlacement placement;
    GameDate            gameDate;
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
    // A death: the character's level and the killer's at the moment of death.
    // Zero is none: a note, a mark stored before protocol 22, no killer or one without a level.
    std::uint32_t characterLevel{};
    std::uint32_t killerLevel{};

    bool operator==(const GroundMark&) const = default;
  };

}
