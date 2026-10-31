module;

#include <glaze/glaze.hpp>

export module Dreamsleeve.Host.Bridge;

import std;
import Dreamsleeve.Client.Exchange;
export import Dreamsleeve.Host.UiSettings;

// In-process JSON contract with the web UI (src/Dreamsleeve.Client.UI/src/bridge/types.ts).
// Host -> UI payloads are handed to InteropCall as a string argument and parsed
// with JSON.parse; they are never evaluated as JavaScript. uint64 IDs are strings.
// The type names and enum strings below are generated into bridge.generated.ts.
// Domain values become these types in BridgeProjection, words in BridgeTexts.
export namespace Dreamsleeve::Host::Bridge
{

  using Dreamsleeve::Client::AuthOperation;
  using Dreamsleeve::Client::ClientStatus;
  using Dreamsleeve::Client::CommandFailureCode;
  using Dreamsleeve::Client::SessionPhase;
  namespace ClientAuth = Dreamsleeve::Client::Auth;

  // Wire names of the enums the UI switches on, in enumerator order from the
  // first named value (Tests.Bridge checks them against the enumerators).
  enum class ConnectionPhase
  {
    Disconnected,
    Authenticating,
    Connecting,
    Opening,
    Connected,
    Disconnecting,
    Faulted
  };
  constexpr auto PhaseNames =
    std::to_array<std::string_view>({"disconnected", "authenticating", "connecting", "opening", "connected", "disconnecting", "faulted"});
  constexpr auto OperationNames =
    std::to_array<std::string_view>({"none", "passwordLogin", "resume", "signOut", "forgetSavedLogin", "resetPassword", "steamLogin"});
  constexpr auto FailureNames = std::to_array<std::string_view>({
      "none",
      "invalidCredentials",
      "usernameTaken",
      "invalidRequest",
      "registrationClosed",
      "busy",
      "unavailable",
      "invalidResponse",
      "credentialStorage",
      "canceled",
      "nameNotAllowed",
      "banned",
      "registrationSteamOnly",
      "addressBanned",
      "deviceBanned",
      "steamExpired",
      "unreachable",
  });

  // Auth::RegistrationMode from GET /auth/methods.
  constexpr auto RegistrationNames = std::to_array<std::string_view>({"unknown", "open", "steam", "manual"});
  constexpr auto OriginNames       = std::to_array<std::string_view>({"server", "trustedClient", "thirdParty"});
  constexpr auto KindNames         = std::to_array<std::string_view>({"announcement", "event", "admin", "periodic"});
  constexpr auto MarkKindNames     = std::to_array<std::string_view>({"note", "death"});

  // Domain::ChatChannelKind from Global; the numbers have gaps, the names follow the enumerators.
  constexpr auto ChannelKindNames = std::to_array<std::string_view>({"global", "guild", "system"});
  // Domain::SessionEndReason from AccessRevoked.
  constexpr auto EndNames = std::to_array<std::string_view>({"revoked", "banned", "kicked", "addressBanned"});
  // Domain::SanctionKind from Mute.
  constexpr auto SanctionKindNames = std::to_array<std::string_view>({"mute", "ban"});
  // Domain::GuildRole from Member.
  constexpr auto GuildRoleNames = std::to_array<std::string_view>({"member", "officer", "master"});
  // Domain::GuildRemovalReason from Left.
  constexpr auto GuildRemovalNames = std::to_array<std::string_view>({"left", "excluded", "disbanded"});
  // Domain::GuildApplicationEnd from Withdrawn.
  constexpr auto GuildApplicationEndNames = std::to_array<std::string_view>({"withdrawn", "accepted", "rejected", "expired", "disbanded"});
  // Domain::GuildCatalogSort from Name: the "sort" of guildCatalog.
  constexpr auto GuildCatalogSortNames = std::to_array<std::string_view>({"name", "members"});
  // The "action" of the guild command, in Client::GuildAction order.
  constexpr auto GuildActionNames = std::to_array<std::string_view>(
    {"create",
     "invite",
     "answer",
     "leave",
     "exclude",
     "setRole",
     "transfer",
     "mute",
     "unmute",
     "disband",
     "createTitle",
     "renameTitle",
     "deleteTitle",
     "setTitle",
     "setInfo",
     "setListed",
     "apply",
     "withdraw",
     "answerRequest"});
  static_assert(GuildActionNames.size() == std::variant_size_v<Dreamsleeve::Client::GuildAction>);

  // names[value - first]; a value outside the table (Unspecified, a newer
  // server value) takes fallback.
  template <class Enum, std::size_t Size>
  constexpr std::string_view NameOf(const std::array<std::string_view, Size>& names, Enum value, Enum first, std::string_view fallback)
  {
    const auto index = static_cast<std::int64_t>(value) - static_cast<std::int64_t>(first);
    return index >= 0 && index < static_cast<std::int64_t>(Size) ? names[static_cast<std::size_t>(index)] : fallback;
  }

  // The ui.toml and bridge names of the choice (HidingNames); anything else is "off".
  Domain::HiddenIdentity HidingOf(std::string_view name)
  {
    const auto found = std::ranges::find(HidingNames, name);
    return found == HidingNames.end() ? Domain::HiddenIdentity::None : static_cast<Domain::HiddenIdentity>(found - HidingNames.begin());
  }

  std::string_view HidingName(Domain::HiddenIdentity value)
  {
    return NameOf(HidingNames, value, Domain::HiddenIdentity::None, HidingNames.front());
  }

  constexpr std::size_t MaxChatText = 16000;
  // Bytes of a requested display name; the server applies its own, smaller limit.
  constexpr std::size_t MaxDisplayName  = 1024;
  constexpr std::size_t MaxRouteName    = 256;
  constexpr std::size_t MaxSnapshotRows = 500;
  constexpr std::size_t MaxCommandBytes = 1 << 20;
  // parse.ts drops an event whose error exceeds 512 UTF-16 units; a UTF-8 byte
  // bound is never looser, and the cut stays on a code point boundary.
  constexpr std::size_t MaxErrorBytes = 512;
  // Bytes of a moderator's reason; the server applies its own, smaller limit.
  constexpr std::size_t MaxReasonBytes = 1024;
  // Bytes of a requested guild or custom role name; the server applies its own, smaller limit.
  constexpr std::size_t MaxGuildNameBytes = 1024;
  // Bytes of requested guild information; the server applies its own, smaller limit.
  constexpr std::size_t MaxGuildInfoBytes = 16000;

  // ---- UI -> host ----------------------------------------------------------

  // A uint64 ID as the UI writes it: a decimal string (0 is Domain::InvalidId).
  struct UiId
  {
    std::uint64_t value{};
  };

  // One struct per command; ParseCommand reads the one named by "type" and
  // ignores unknown keys. The host checks only what it owns: the page's
  // correlation IDs, size bounds of page input and its own settings. What
  // travels further is checked by Core once (the codec's request shape).
  namespace Commands
  {

    struct SendChat
    {
      std::string requestId;
      UiId        channelId;
      std::string text;
    };

    struct Close
    {};

    struct SaveSettings
    {
      UiSettings   settings;
      std::int64_t revision{};
    };

    // displayName present: register first, then sign in.
    struct SignIn
    {
      std::string                username;
      std::string                password;
      bool                       remember{};
      std::optional<std::string> displayName;
    };

    struct Ignore
    {
      UiId playerId;
    };

    struct Unignore
    {
      UiId playerId;
    };

    // Only the InstantKeys settings are taken from it.
    struct DisplaySettings
    {
      UiSettings settings;
    };

    struct SignInSaved
    {};

    struct SignOut
    {};

    struct ForgetLogin
    {};

    struct Disconnect
    {};

    // A route of client.toml by name; empty: choose automatically.
    struct ChooseRoute
    {
      std::string route;
    };

    // A note where the character stands; the host fills the placement.
    struct PlaceGroundNote
    {
      std::string requestId;
      std::string text;
    };

    struct RemoveGroundMark
    {
      std::string requestId;
      UiId        markId;
    };

    struct SetIdentityVisibility
    {
      std::string hiding;
    };

    struct ChangeDisplayName
    {
      std::string displayName;
    };

    // The own name color in chat, "#RRGGBB".
    struct SetNameColor
    {
      std::string color;
    };

    // Moderator tools; the server checks the role and the target. kind is
    // SanctionKindNames; no minutes: until lifted.
    struct SanctionPlayer
    {
      std::string                  requestId;
      UiId                         playerId;
      std::string                  kind;
      std::optional<std::uint32_t> minutes;
      std::string                  reason;
      bool                         devices{};
    };

    struct LiftSanction
    {
      std::string requestId;
      UiId        playerId;
      std::string kind;
    };

    struct KickPlayer
    {
      std::string requestId;
      UiId        playerId;
      std::string reason;
    };

    struct ListSanctions
    {
      std::string requestId;
    };

    struct ListPlayerMarks
    {
      std::string requestId;
      UiId        playerId;
    };

    struct ClearPlayerMarks
    {
      std::string requestId;
      UiId        playerId;
      bool        notes{};
      bool        deaths{};
    };

    struct DeleteChatMessage
    {
      std::string requestId;
      UiId        channelId;
      UiId        messageId;
    };

    // An administrator's one-time code (a new account or a reset) and the new
    // password; the player signs in normally afterwards.
    struct ResetPassword
    {
      std::string code;
      std::string password;
    };

    // Sign-in through Steam in the browser; remember saves the login.
    struct SignInSteam
    {
      bool remember{};
    };

    // The page of the Steam sign-in in progress onto the clipboard.
    struct CopySteamLink
    {};

    // A guild request: action is GuildActionNames and decides which values
    // count (role: member or officer; no minutes: until lifted; name: of the
    // guild or the custom role; no titleId in setTitle: the member holds
    // none). The server judges the roles, the name and the limits.
    struct Guild
    {
      std::string                  requestId;
      std::string                  action;
      UiId                         guildId;
      UiId                         playerId;
      std::string                  name;
      bool                         accept{};
      std::string                  role;
      std::optional<std::uint32_t> minutes;
      std::string                  reason;
      UiId                         titleId;
      std::string                  info;
      bool                         listed{};
    };

    // The entry the previous catalog page ended with, as received.
    struct GuildCatalogAfter
    {
      UiId          guildId;
      std::string   name;
      std::uint32_t members{};
    };

    // One page of the public catalog, answered with the guildCatalog event;
    // sort: GuildCatalogSortNames; no after: the first page.
    struct GuildCatalog
    {
      std::string                      requestId;
      std::string                      search;
      std::uint32_t                    minMembers{};
      std::optional<std::uint32_t>     maxMembers;
      bool                             openOnly{};
      std::string                      sort;
      std::optional<GuildCatalogAfter> after;
    };

  }

  using UiCommand = std::variant<
    Commands::SendChat,
    Commands::Close,
    Commands::SaveSettings,
    Commands::SignIn,
    Commands::Ignore,
    Commands::Unignore,
    Commands::DisplaySettings,
    Commands::SignInSaved,
    Commands::SignOut,
    Commands::ForgetLogin,
    Commands::Disconnect,
    Commands::PlaceGroundNote,
    Commands::RemoveGroundMark,
    Commands::SetIdentityVisibility,
    Commands::ChangeDisplayName,
    Commands::SetNameColor,
    Commands::ChooseRoute,
    Commands::SanctionPlayer,
    Commands::LiftSanction,
    Commands::KickPlayer,
    Commands::ListSanctions,
    Commands::ListPlayerMarks,
    Commands::ClearPlayerMarks,
    Commands::DeleteChatMessage,
    Commands::ResetPassword,
    Commands::SignInSteam,
    Commands::CopySteamLink,
    Commands::Guild,
    Commands::GuildCatalog>;

  // The "type" of each UiCommand alternative, in variant order.
  constexpr auto CommandNames = std::to_array<std::string_view>({
      "sendChat",
      "close",
      "saveSettings",
      "signIn",
      "ignore",
      "unignore",
      "displaySettings",
      "signInSaved",
      "signOut",
      "forgetLogin",
      "disconnect",
      "placeGroundNote",
      "removeGroundMark",
      "setIdentityVisibility",
      "changeDisplayName",
      "setNameColor",
      "chooseRoute",
      "sanctionPlayer",
      "liftSanction",
      "kickPlayer",
      "listSanctions",
      "listPlayerMarks",
      "clearPlayerMarks",
      "deleteChatMessage",
      "resetPassword",
      "signInSteam",
      "copySteamLink",
      "guild",
      "guildCatalog",
  });
  static_assert(CommandNames.size() == std::variant_size_v<UiCommand>);

  // ---- host -> UI ----------------------------------------------------------

  struct UiResource
  {
    double current{};
    double maximum{};
  };

  struct UiActorValue
  {
    std::string                      key;
    std::string                      name;
    std::variant<double, UiResource> value{0.0};
  };

  // name is the one resolved label for every surface. In streamer mode the
  // real username/displayName/character never cross the bridge: displayName
  // and alias carry the local pseudonym, the others stay empty. A player who
  // hides their names (pseudonymous) arrives from the server with the server
  // pseudonym only; the UI marks such a player. color ("#RRGGBB") is how the
  // player's name is drawn in chat; absent for a pseudonym.
  struct UiPlayer
  {
    std::string                id;
    std::string                name;
    std::optional<std::string> color;
    std::optional<std::string> alias;
    std::string                displayName;
    std::string                username;
    std::optional<std::string> character;
    bool                       inCharacter{};

    std::optional<std::uint32_t> level;
    std::optional<std::string>   location;
    std::optional<std::string>   zone;
    std::optional<std::string>   race;
    std::optional<std::string>   nearbyMarker;
    std::optional<std::string>   markerKind;
    std::optional<bool>          interior;

    std::optional<std::string>  activity;
    std::optional<std::string>  activityTarget;
    std::optional<std::string>  lockDifficulty;
    std::optional<std::string>  menu;
    std::optional<std::int64_t> gameStartedAt;

    std::optional<std::vector<UiActorValue>> actorValues;
    bool                                     pseudonymous{};
  };

  // kind: ChannelKindNames.
  struct UiChannel
  {
    std::string id;
    std::string kind;
    std::string name;
    bool        writable{};
  };

  // origin: OriginNames; kind: KindNames.
  struct UiAnnouncement
  {
    std::string                origin;
    std::string                kind;
    std::optional<std::string> signature;
  };

  struct UiMessage
  {
    std::string  id;
    std::string  channelId;
    std::string  text;
    std::int64_t time{};
    std::string  source{"player"};
    // Absent for server announcements; a client announcement names its player.
    std::optional<UiPlayer> author;
    // The text was masked or replaced by the local filter of flagged ranges.
    bool                          filtered{};
    std::optional<UiAnnouncement> announcement;
  };

  // A ground mark for the UI lists: kind is MarkKindNames; the text is
  // already filtered like chat. author is the host-resolved name (nearby
  // marks only), character the snapshot at placement; location is the WRLD or
  // CELL key "plugin:formid" with the position in game units.
  struct UiGroundMark
  {
    std::string                id;
    std::string                kind;
    std::string                text;
    std::int64_t               time{};
    std::optional<std::string> author;
    std::optional<std::string> character;
    std::string                location;
    double                     x{}, y{}, z{};
    // The in-game date in the chosen markDateStyle; absent on marks stored before dates were kept.
    std::optional<std::string> gameDate;
    // A death: the character's level and the killer's; absent when the mark has none.
    std::optional<std::uint32_t> level;
    std::optional<std::uint32_t> killerLevel;
  };

  struct UiIgnored
  {
    std::string id;
    std::string name;
  };

  // A sanction in force for the moderator's list: kind is SanctionKindNames;
  // name is the host's name for a player it has met, absent for anyone else.
  struct UiSanction
  {
    std::string                 playerId;
    std::optional<std::string>  name;
    std::string                 kind;
    std::string                 reason;
    std::int64_t                issuedAt{};
    std::optional<std::int64_t> until;
  };

  struct SnapshotEvent
  {
    std::vector<UiChannel> channels;
    std::vector<UiMessage> messages;
    std::vector<UiPlayer>  players;
    std::string            selfId;
    std::string            serverName;
    // The page already shows this session (names, ignore list, or a snapshot
    // Core sent after its queue overflowed): the UI keeps its pending rows,
    // channel, filters and scroll instead of treating it as new.
    bool refresh{};
    // The session speaks a protocol with ground marks; the own marks the
    // server listed and the marks it shows nearby.
    bool                      groundMarksSupported{};
    std::vector<UiGroundMark> groundMarks;
    std::vector<UiGroundMark> nearbyMarks;
  };

  struct MessagesEvent
  {
    std::vector<UiMessage> messages;
  };

  struct PlayersEvent
  {
    std::vector<UiPlayer> players;
  };

  // phase: PhaseNames.
  struct ConnectionEvent
  {
    bool        connected{};
    std::string phase{PhaseNames.front()};
  };

  // Personal ignore list of this server, already named for the current settings.
  struct IgnoredEvent
  {
    std::vector<UiIgnored> players;
  };

  struct SendResultEvent
  {
    std::string                requestId;
    std::optional<std::string> messageId;
    std::optional<std::string> error;
  };

  // An announcement another mod requested through the plugin API was not published.
  struct AnnouncementResultEvent
  {
    std::string channelId;
    std::string source;
    std::string text;
    std::string error;
  };

  struct SettingsResultEvent
  {
    std::int64_t               revision{};
    std::optional<std::string> error;
  };

  // Typed authentication state; no password or token ever crosses this boundary.
  struct AuthEvent
  {
    bool        authenticating{};
    std::string operation{OperationNames.front()};
    std::string failure{FailureNames.front()};
    std::string error;
    bool        savedLogin{};
    std::string savedUsername;
    // Who may register on the server and whether it signs in through Steam.
    std::string registration{RegistrationNames.front()};
    bool        steam{};
    // A Steam sign-in waits, but the browser did not open its page.
    bool        browserFailed{};
    std::string phase{PhaseNames.front()};
  };

  // The host's copy of the settings: when a page is (re)created, so window
  // position and options apply before any snapshot, and when the host changes
  // it itself. Snapshots never carry settings: unsaved edits are the page's.
  struct SettingsEvent
  {
    UiSettings settings;
  };

  // The own marks changed: placed, removed, evicted or seen again.
  struct GroundMarksEvent
  {
    std::vector<UiGroundMark> marks;
  };

  // Outcome of placeGroundNote or removeGroundMark; not a delta of the visible set.
  struct MarkResultEvent
  {
    std::string                requestId;
    std::optional<std::string> markId;
    std::optional<std::string> evictedId;
    std::optional<bool>        removed;
    std::optional<std::string> error;
  };

  // The marks the server shows near the player changed.
  struct NearbyMarksEvent
  {
    std::vector<UiGroundMark> marks;
  };

  // "Hide my name from other players": mode is the choice (HidingNames; the
  // requested one while pending), pending waits for the server, pseudonym is
  // what the others see now, error the last refusal (the choice is back to
  // the server's state).
  struct IdentityEvent
  {
    std::string                mode{HidingNames.front()};
    bool                       pending{};
    std::optional<std::string> pseudonym;
    std::optional<std::string> error;

    bool operator==(const IdentityEvent&) const = default;
  };

  // A change of the own display name: pending waits for the server, changed is
  // the name the server has just stored (sent once), error the last refusal.
  // The own profile itself arrives with the players list.
  struct DisplayNameEvent
  {
    bool                       pending{};
    std::optional<std::string> changed;
    std::optional<std::string> error;

    bool operator==(const DisplayNameEvent&) const = default;
  };

  // The routes of client.toml by name, the main one first, when there are
  // others: active is the one the traffic goes by and reached whether it has
  // answered; chosen is the player's pick, empty for automatic.
  struct RoutesEvent
  {
    std::vector<std::string> routes;
    std::string              active;
    std::string              chosen;
    bool                     reached{};

    bool operator==(const RoutesEvent&) const = default;
  };

  // A change of the own name color, like DisplayNameEvent: changed is the
  // "#RRGGBB" the server has just stored (sent once).
  struct NameColorEvent
  {
    bool                       pending{};
    std::optional<std::string> changed;
    std::optional<std::string> error;

    bool operator==(const NameColorEvent&) const = default;
  };

  // The player's own mute: the moderator's reason and when it ends (absent:
  // until lifted). muted is false when there is none.
  struct MuteEvent
  {
    bool                        muted{};
    std::string                 reason;
    std::optional<std::int64_t> until;

    bool operator==(const MuteEvent&) const = default;
  };

  // Why the server ended the session, or refused sign-in: revoked, banned or
  // kicked, the moderator's reason and the end of a ban (absent: until lifted).
  struct SessionEndedEvent
  {
    std::string                 reason{EndNames.front()};
    std::string                 text;
    std::optional<std::int64_t> until;

    bool operator==(const SessionEndedEvent&) const = default;
  };

  // The player's role in the session; a moderator gets the moderator tools.
  struct RoleEvent
  {
    bool moderator{};

    bool operator==(const RoleEvent&) const = default;
  };

  // Messages a moderator removed: gone from every surface.
  struct MessagesRemovedEvent
  {
    std::string              channelId;
    std::vector<std::string> messageIds;
  };

  // The answer to a moderator request: error on a refusal, otherwise the
  // fields of its kind. The removal of one mark answers with markResult.
  struct ModerationResultEvent
  {
    std::string                              requestId;
    std::optional<std::string>               error;
    std::optional<UiSanction>                sanction;   // sanctionPlayer
    std::optional<std::vector<UiSanction>>   sanctions;  // listSanctions
    std::optional<std::string>               playerId;   // liftSanction, kickPlayer, listPlayerMarks, clearPlayerMarks
    std::optional<std::vector<UiGroundMark>> marks;      // listPlayerMarks
    std::optional<std::uint32_t>             removed;    // clearPlayerMarks
  };

  // A mute inside one guild: reading only; until is absent until lifted.
  struct UiGuildMute
  {
    std::string                 reason;
    std::optional<std::int64_t> until;
  };

  // A member as guildmates see them: name is the host's label of the real
  // profile for the current settings (the local alias in streamer mode).
  // role: GuildRoleNames; title: the ID of the guild's custom role they hold.
  struct UiGuildMember
  {
    std::string                id;
    std::string                name;
    std::string                role;
    bool                       online{};
    std::int64_t               joinedAt{};
    std::optional<UiGuildMute> mute;
    std::optional<std::string> title;
  };

  // A custom role of the guild: cosmetic, the master's to create and assign.
  struct UiGuildTitle
  {
    std::string id;
    std::string name;
  };

  // A join request as the master and officers see it: name is the host's
  // label of the real profile, like a member's.
  struct UiGuildJoinRequest
  {
    std::string  id;
    std::string  name;
    std::int64_t createdAt{};
    std::int64_t expires{};
  };

  struct UiGuild
  {
    std::string                     id;
    std::string                     name;
    std::string                     channelId;
    std::int64_t                    createdAt{};
    std::vector<UiGuildMember>      members;
    std::vector<UiGuildTitle>       titles;        // Oldest first.
    std::string                     info;          // The master's text; empty: nothing.
    bool                            listed{true};  // Shown in the public catalog.
    std::vector<UiGuildJoinRequest> joinRequests;  // Only for the master and officers.
  };

  // A join request of the player, waiting for the guild's answer.
  struct UiGuildApplication
  {
    std::string  guildId;
    std::string  guildName;
    std::int64_t expires{};
  };

  // A join request of the player that ended since the last guilds event;
  // reason: GuildApplicationEndNames.
  struct UiGuildApplicationEnd
  {
    std::string guildId;
    std::string name;
    std::string reason;
  };

  // An invitation waiting for the player's answer; inviter is the host's
  // name for the inviting player when it has met them.
  struct UiGuildInvite
  {
    std::string                guildId;
    std::string                guildName;
    std::string                invitedBy;
    std::optional<std::string> inviter;
    std::int64_t               expires{};
  };

  // The server's limits; a lowered limit removes nobody.
  // nameMin and nameMax bound custom role names too.
  struct UiGuildLimits
  {
    std::uint32_t perPlayer{};
    std::uint32_t members{};
    std::uint32_t nameMin{};
    std::uint32_t nameMax{};
    std::uint32_t titles{};
    std::uint32_t infoMax{};
    std::uint32_t joinRequests{};
    std::uint32_t applications{};
  };

  // A guild the player left since the last guilds event; reason: GuildRemovalNames.
  struct UiGuildRemoval
  {
    std::string guildId;
    std::string name;
    std::string reason;
  };

  // The player's guilds and invitations, complete; removed names the guilds
  // the player left since the previous event, for notices.
  struct GuildsEvent
  {
    std::vector<UiGuild>               guilds;
    std::vector<UiGuildInvite>         invites;
    UiGuildLimits                      limits;
    std::vector<UiGuildRemoval>        removed;
    std::vector<UiGuildApplication>    applications;
    std::vector<UiGuildApplicationEnd> applicationsEnded;
  };

  // A guild of the public catalog.
  struct UiCatalogGuild
  {
    std::string   id;
    std::string   name;
    std::string   info;
    std::uint32_t members{};
    std::int64_t  createdAt{};
  };

  // The answer to guildCatalog: a page (guilds, more, total) or the refusal.
  struct GuildCatalogEvent
  {
    std::string                                requestId;
    std::optional<std::vector<UiCatalogGuild>> guilds;
    std::optional<bool>                        more;
    std::optional<std::uint32_t>               total;
    std::optional<std::string>                 error;
  };

  // The answer to a guild command: the guild it acted on (a new one's ID for
  // create), or the refusal.
  struct GuildResultEvent
  {
    std::string                requestId;
    std::optional<std::string> guildId;
    std::optional<std::string> error;
  };

  // The channels of the session changed (a guild came or went): the complete list.
  struct ChannelsEvent
  {
    std::vector<UiChannel> channels;
  };

  // View visibility and chat focus, decided by the host.
  struct ShowEvent
  {};

  struct HideEvent
  {};

  struct ActivateEvent
  {};

  struct DeactivateEvent
  {};

  using HostEvent = std::variant<
    SnapshotEvent,
    MessagesEvent,
    PlayersEvent,
    ConnectionEvent,
    IgnoredEvent,
    SendResultEvent,
    AnnouncementResultEvent,
    SettingsResultEvent,
    AuthEvent,
    SettingsEvent,
    GroundMarksEvent,
    MarkResultEvent,
    NearbyMarksEvent,
    IdentityEvent,
    DisplayNameEvent,
    NameColorEvent,
    RoutesEvent,
    MuteEvent,
    SessionEndedEvent,
    RoleEvent,
    MessagesRemovedEvent,
    ModerationResultEvent,
    GuildsEvent,
    GuildResultEvent,
    GuildCatalogEvent,
    ChannelsEvent,
    ShowEvent,
    HideEvent,
    ActivateEvent,
    DeactivateEvent>;

  // The "type" of each HostEvent alternative, in variant order.
  constexpr auto EventNames = std::to_array<std::string_view>({
      "snapshot",
      "messages",
      "players",
      "connection",
      "ignored",
      "sendResult",
      "announcementResult",
      "settingsResult",
      "auth",
      "settings",
      "groundMarks",
      "markResult",
      "nearbyMarks",
      "identity",
      "displayName",
      "nameColor",
      "routes",
      "mute",
      "sessionEnded",
      "role",
      "messagesRemoved",
      "moderationResult",
      "guilds",
      "guildResult",
      "guildCatalog",
      "channels",
      "show",
      "hide",
      "activate",
      "deactivate",
  });
  static_assert(EventNames.size() == std::variant_size_v<HostEvent>);

}

// A command ID is a quoted decimal; the variants carry their name in "type".
template <>
struct glz::meta<Dreamsleeve::Host::Bridge::UiId>
{
  static constexpr auto value = glz::quoted_num<&Dreamsleeve::Host::Bridge::UiId::value>;
};

template <>
struct glz::meta<Dreamsleeve::Host::Bridge::HostEvent>
{
  static constexpr std::string_view tag = "type";
  static constexpr auto             ids = Dreamsleeve::Host::Bridge::EventNames;
};

export namespace Dreamsleeve::Host::Bridge
{

  using Encoded = std::expected<std::string, std::string>;

  namespace Detail
  {

    struct CommandHead
    {
      std::string type;
    };

    constexpr auto Lenient = glz::opts{.error_on_unknown_keys = false};

    std::expected<void, std::string> Admit(Commands::SendChat& command)
    {
      if (command.requestId.empty()) return std::unexpected{"sendChat requires requestId"};
      if (command.text.size() > MaxChatText) return std::unexpected{"sendChat text is too long"};
      return {};
    }

    std::expected<void, std::string> Admit(Commands::SaveSettings& command)
    {
      command.settings = Normalize(command.settings);
      return {};
    }

    // An empty registration name means none.
    std::expected<void, std::string> Admit(Commands::SignIn& command)
    {
      if (command.displayName && command.displayName->empty()) command.displayName.reset();
      return {};
    }

    std::expected<void, std::string> Admit(Commands::DisplaySettings& command)
    {
      command.settings = Normalize(command.settings);
      return {};
    }

    std::expected<void, std::string> Admit(Commands::PlaceGroundNote& command)
    {
      if (command.requestId.empty()) return std::unexpected{"placeGroundNote requires requestId"};
      if (command.text.size() > MaxChatText) return std::unexpected{"placeGroundNote text is too long"};
      return {};
    }

    std::expected<void, std::string> Admit(Commands::RemoveGroundMark& command)
    {
      if (command.requestId.empty()) return std::unexpected{"removeGroundMark requires requestId"};
      return {};
    }

    std::expected<void, std::string> Admit(Commands::SetIdentityVisibility& command)
    {
      if (!std::ranges::contains(HidingNames, command.hiding))
        return std::unexpected{"setIdentityVisibility requires hiding off, everywhere or exceptGroundMarks"};
      return {};
    }

    std::expected<void, std::string> Admit(Commands::ChangeDisplayName& command)
    {
      if (command.displayName.size() > MaxDisplayName) return std::unexpected{"changeDisplayName displayName is too long"};
      return {};
    }

    std::expected<void, std::string> Admit(Commands::ChooseRoute& command)
    {
      if (command.route.size() > MaxRouteName) return std::unexpected{"chooseRoute route is too long"};
      return {};
    }

    std::expected<void, std::string> Admit(Commands::SetNameColor& command)
    {
      if (!ParseColor(command.color)) return std::unexpected{"setNameColor color must be #RRGGBB"};
      return {};
    }

    // Every moderator request carries the page's correlation.
    template <class Command>
    requires requires(Command command) { command.requestId; }
    std::expected<void, std::string> Correlated(const Command& command, std::string_view name)
    {
      if (command.requestId.empty()) return std::unexpected{std::format("{} requires requestId", name)};
      return {};
    }

    std::expected<void, std::string> Admit(Commands::SanctionPlayer& command)
    {
      if (!std::ranges::contains(SanctionKindNames, command.kind)) return std::unexpected{"sanctionPlayer requires kind mute or ban"};
      if (command.reason.size() > MaxReasonBytes) return std::unexpected{"sanctionPlayer reason is too long"};
      return Correlated(command, "sanctionPlayer");
    }

    std::expected<void, std::string> Admit(Commands::LiftSanction& command)
    {
      if (!std::ranges::contains(SanctionKindNames, command.kind)) return std::unexpected{"liftSanction requires kind mute or ban"};
      return Correlated(command, "liftSanction");
    }

    std::expected<void, std::string> Admit(Commands::KickPlayer& command)
    {
      if (command.reason.size() > MaxReasonBytes) return std::unexpected{"kickPlayer reason is too long"};
      return Correlated(command, "kickPlayer");
    }

    std::expected<void, std::string> Admit(Commands::ListSanctions& command)
    {
      return Correlated(command, "listSanctions");
    }

    std::expected<void, std::string> Admit(Commands::ListPlayerMarks& command)
    {
      return Correlated(command, "listPlayerMarks");
    }

    std::expected<void, std::string> Admit(Commands::ClearPlayerMarks& command)
    {
      return Correlated(command, "clearPlayerMarks");
    }

    std::expected<void, std::string> Admit(Commands::DeleteChatMessage& command)
    {
      return Correlated(command, "deleteChatMessage");
    }

    std::expected<void, std::string> Admit(Commands::Guild& command)
    {
      if (!std::ranges::contains(GuildActionNames, command.action)) return std::unexpected{"guild requires a known action"};
      if (command.action == "setRole" && command.role != GuildRoleNames[0] && command.role != GuildRoleNames[1])
        return std::unexpected{"guild setRole requires role member or officer"};
      if (command.name.size() > MaxGuildNameBytes) return std::unexpected{"guild name is too long"};
      if (command.reason.size() > MaxReasonBytes) return std::unexpected{"guild reason is too long"};
      if (command.info.size() > MaxGuildInfoBytes) return std::unexpected{"guild info is too long"};
      return Correlated(command, "guild");
    }

    std::expected<void, std::string> Admit(Commands::GuildCatalog& command)
    {
      if (!std::ranges::contains(GuildCatalogSortNames, command.sort)) return std::unexpected{"guildCatalog requires sort name or members"};
      if (command.search.size() > MaxGuildNameBytes) return std::unexpected{"guildCatalog search is too long"};
      if (command.after && command.after->name.size() > MaxGuildNameBytes) return std::unexpected{"guildCatalog after is too long"};
      return Correlated(command, "guildCatalog");
    }

    // Commands without values have nothing to admit.
    template <class Command>
    std::expected<void, std::string> Admit(Command&)
    {
      return {};
    }

    template <std::size_t Index = 0>
    std::expected<UiCommand, std::string> Read(std::size_t index, std::string_view json)
    {
      if constexpr (Index == std::variant_size_v<UiCommand>)
        return std::unexpected{"Unknown UI command"};
      else
      {
        if (index != Index) return Read<Index + 1>(index, json);
        std::variant_alternative_t<Index, UiCommand> command;
        if (auto error = glz::read<Lenient>(command, json))
          return std::unexpected{std::format("Invalid {} command: {}", CommandNames[Index], glz::format_error(error, json))};
        if (auto admitted = Admit(command); !admitted) return std::unexpected{admitted.error()};
        return UiCommand{std::move(command)};
      }
    }

  }

  std::expected<UiCommand, std::string> ParseCommand(std::string_view json)
  {
    if (json.size() > MaxCommandBytes) return std::unexpected{"UI command exceeds limit"};
    Detail::CommandHead head;
    if (auto error = glz::read<Detail::Lenient>(head, json))
      return std::unexpected{"Invalid UI command: " + glz::format_error(error, json)};
    const auto found = std::ranges::find(CommandNames, head.type);
    if (found == CommandNames.end()) return std::unexpected{"Unknown UI command: " + head.type};
    return Detail::Read(static_cast<std::size_t>(found - CommandNames.begin()), json);
  }

  // Instantiated only inside this module: glaze internals live in its global
  // module fragment and are not reachable from importers' template instantiations.
  Encoded Encode(const HostEvent& event)
  {
    auto json = glz::write_json(event);
    if (!json) return std::unexpected{"Cannot encode UI event"};
    return std::move(*json);
  }

  std::string_view TypeOf(const HostEvent& event)
  {
    return EventNames[event.index()];
  }

  std::string Id(std::uint64_t value)
  {
    return std::to_string(value);
  }

}
