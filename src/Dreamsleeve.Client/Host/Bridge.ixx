module;

#include <glaze/glaze.hpp>

export module Dreamsleeve.Host.Bridge;

import std;
import Dreamsleeve.Client.Exchange;
import Dreamsleeve.Client.Utils;
export import Dreamsleeve.Host.UiSettings;
export import Dreamsleeve.Host.Names;
export import Dreamsleeve.Host.GameDates;

// In-process JSON contract with the web UI (src/Dreamsleeve.Client.UI/src/bridge/types.ts).
// Host -> UI payloads are handed to InteropCall as a string argument and parsed
// with JSON.parse; they are never evaluated as JavaScript. uint64 IDs are strings.
// The type names and enum strings below are generated into bridge.generated.ts.
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
  constexpr auto FailureNames     = std::to_array<std::string_view>({
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
  });
  // Auth::RegistrationMode from GET /auth/methods.
  constexpr auto RegistrationNames = std::to_array<std::string_view>({"unknown", "open", "steam", "manual"});
  constexpr auto OriginNames      = std::to_array<std::string_view>({"server", "trustedClient", "thirdParty"});
  constexpr auto KindNames        = std::to_array<std::string_view>({"announcement", "event", "admin", "periodic"});
  constexpr auto MarkKindNames    = std::to_array<std::string_view>({"note", "death"});
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
  // The "action" of the guild command, in Client::GuildAction order.
  constexpr auto GuildActionNames =
    std::to_array<std::string_view>({"create", "invite", "answer", "leave", "exclude", "setRole", "transfer", "mute", "unmute", "disband"});
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
  constexpr std::size_t MaxSnapshotRows = 500;
  constexpr std::size_t MaxCommandBytes = 1 << 20;
  // parse.ts drops an event whose error exceeds 512 UTF-16 units; a UTF-8 byte
  // bound is never looser, and the cut stays on a code point boundary.
  constexpr std::size_t MaxErrorBytes = 512;
  // Bytes of a moderator's reason; the server applies its own, smaller limit.
  constexpr std::size_t MaxReasonBytes = 1024;
  // Bytes of a requested guild name; the server applies its own, smaller limit.
  constexpr std::size_t MaxGuildNameBytes = 1024;

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
    {
    };

    // A guild request: action is GuildActionNames and decides which values
    // count (role: member or officer; no minutes: until lifted). The server
    // judges the roles, the name and the limits.
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
    Commands::Guild>;

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
    std::string                              id;
    std::string                              name;
    std::optional<std::string>               color;
    std::optional<std::string>               alias;
    std::string                              displayName;
    std::string                              username;
    std::optional<std::string>               character;
    bool                                     inCharacter{};
    std::optional<std::uint32_t>             level;
    std::optional<std::string>               location;
    std::optional<std::string>               zone;
    std::optional<std::string>               race;
    std::optional<std::string>               nearbyMarker;
    std::optional<std::string>               markerKind;
    std::optional<bool>                      interior;
    std::optional<std::string>               activity;
    std::optional<std::string>               activityTarget;
    std::optional<std::string>               lockDifficulty;
    std::optional<std::string>               menu;
    std::optional<std::int64_t>              gameStartedAt;
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
    // Same session re-projected (names or ignore list changed): the UI keeps
    // its pending rows, filters and scroll instead of treating it as new.
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
  // role: GuildRoleNames.
  struct UiGuildMember
  {
    std::string                id;
    std::string                name;
    std::string                role;
    bool                       online{};
    std::int64_t               joinedAt{};
    std::optional<UiGuildMute> mute;
  };

  struct UiGuild
  {
    std::string                id;
    std::string                name;
    std::string                channelId;
    std::int64_t               createdAt{};
    std::vector<UiGuildMember> members;
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
  struct UiGuildLimits
  {
    std::uint32_t perPlayer{};
    std::uint32_t members{};
    std::uint32_t nameMin{};
    std::uint32_t nameMax{};
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
    std::vector<UiGuild>        guilds;
    std::vector<UiGuildInvite>  invites;
    UiGuildLimits               limits;
    std::vector<UiGuildRemoval> removed;
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
    MuteEvent,
    SessionEndedEvent,
    RoleEvent,
    MessagesRemovedEvent,
    ModerationResultEvent,
    GuildsEvent,
    GuildResultEvent,
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
      "mute",
      "sessionEnded",
      "role",
      "messagesRemoved",
      "moderationResult",
      "guilds",
      "guildResult",
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
      return Correlated(command, "guild");
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

  // Labels are display text for the Russian UI; keys stay in the protocol.
  std::string_view ActivityLabel(Domain::ActivityKind kind)
  {
    using Domain::ActivityKind;
    switch (kind)
    {
      case ActivityKind::Exploring:
        return "Исследование";
      case ActivityKind::Combat:
        return "Бой";
      case ActivityKind::Talking:
        return "Разговор";
      case ActivityKind::Bartering:
        return "Торговля";
      case ActivityKind::Training:
        return "Обучение";
      case ActivityKind::Reading:
        return "Чтение";
      case ActivityKind::Lockpicking:
        return "Взлом замка";
      case ActivityKind::Crafting:
        return "Ремесло";
      case ActivityKind::UsingObject:
        return "Использует объект";
      case ActivityKind::Riding:
        return "Верхом";
      case ActivityKind::Sneaking:
        return "Скрытность";
      case ActivityKind::Swimming:
        return "Плавание";
      case ActivityKind::Flying:
        return "Полёт";
      case ActivityKind::Dead:
        return "Погиб";
      case ActivityKind::Ragdoll:
        return "Сбит с ног";
      case ActivityKind::Menu:
        return "В меню";
      case ActivityKind::NewGame:
        return "Новая игра";
      case ActivityKind::Loading:
        return "Загрузка";
      case ActivityKind::Unknown:
        break;
    }
    return "";
  }

  std::string_view LockLabel(Domain::LockDifficulty level)
  {
    using Domain::LockDifficulty;
    switch (level)
    {
      case LockDifficulty::Unlocked:
        return "Открыт";
      case LockDifficulty::VeryEasy:
        return "Очень лёгкий";
      case LockDifficulty::Easy:
        return "Лёгкий";
      case LockDifficulty::Average:
        return "Средний";
      case LockDifficulty::Hard:
        return "Сложный";
      case LockDifficulty::VeryHard:
        return "Очень сложный";
      case LockDifficulty::RequiresKey:
        return "Нужен ключ";
      case LockDifficulty::Unknown:
        break;
    }
    return "";
  }

  std::string_view MenuLabel(std::string_view key)
  {
    static const std::pair<std::string_view, std::string_view> labels[] = {
        {"main",         "Главное меню"    },
        {"inventory",    "Инвентарь"       },
        {"magic",        "Магия"           },
        {"map",          "Карта"           },
        {"journal",      "Журнал"          },
        {"stats",        "Навыки"          },
        {"tween",        "Меню персонажа"  },
        {"sleepwait",    "Сон и ожидание"  },
        {"favorites",    "Избранное"       },
        {"levelup",      "Повышение уровня"},
        {"console",      "Консоль"         },
        {"messagebox",   "Сообщение"       },
        {"racesex",      "Внешность"       },
        {"container",    "Контейнер"       },
        {"gift",         "Подарок"         },
        {"tutorial",     "Обучение"        },
        {"creationclub", "Creation Club"   },
        {"modmanager",   "Модификации"     },
        {"credits",      "Титры"           },
    };
    for (const auto& [name, label] : labels)
      if (name == key) return label;
    return key;
  }

  std::string_view MarkerLabel(std::string_view kind)
  {
    static const std::pair<std::string_view, std::string_view> labels[] = {
        {"city",               "Город"              },
        {"town",               "Городок"            },
        {"settlement",         "Поселение"          },
        {"cave",               "Пещера"             },
        {"camp",               "Лагерь"             },
        {"fort",               "Форт"               },
        {"nordicruin",         "Нордские руины"     },
        {"dwemer",             "Двемерские руины"   },
        {"shipwreck",          "Кораблекрушение"    },
        {"grove",              "Роща"               },
        {"landmark",           "Ориентир"           },
        {"dragonlair",         "Логово дракона"     },
        {"farm",               "Ферма"              },
        {"woodmill",           "Лесопилка"          },
        {"mine",               "Шахта"              },
        {"imperialcamp",       "Имперский лагерь"   },
        {"stormcloakcamp",     "Лагерь Братьев Бури"},
        {"doomstone",          "Камень судьбы"      },
        {"wheatmill",          "Мельница"           },
        {"smelter",            "Плавильня"          },
        {"stable",             "Конюшня"            },
        {"imperialtower",      "Имперская башня"    },
        {"clearing",           "Поляна"             },
        {"pass",               "Перевал"            },
        {"altar",              "Алтарь"             },
        {"rock",               "Скала"              },
        {"lighthouse",         "Маяк"               },
        {"orcstronghold",      "Орочья крепость"    },
        {"giantcamp",          "Лагерь великанов"   },
        {"shack",              "Хижина"             },
        {"nordictower",        "Нордская башня"     },
        {"nordicdwelling",     "Нордское жилище"    },
        {"docks",              "Причал"             },
        {"shrine",             "Святилище"          },
        {"castle",             "Замок"              },
        {"capital",            "Столица"            },
        {"templeofmiraak",     "Храм Мираака"       },
        {"redoransettlement",  "Поселение Редоран"  },
        {"allmakerstone",      "Камень Всесоздателя"},
        {"telvannisettlement", "Поселение Телванни" },
    };
    for (const auto& [name, label] : labels)
      if (name == kind) return label;
    return kind;
  }

  std::optional<std::string> Text(const std::string& value)
  {
    if (value.empty()) return std::nullopt;
    return value;
  }

  std::optional<std::string> Label(std::string_view value)
  {
    if (value.empty()) return std::nullopt;
    return std::string{value};
  }

  std::string ClipError(std::string_view value)
  {
    return std::string{Dreamsleeve::Utils::Text::ClipBytes(value, MaxErrorBytes)};
  }

  UiPlayer ToUiAuthor(
    const Domain::PlayerData&                   data,
    const std::optional<Domain::CharacterName>& character,
    bool                                        inCharacter,
    Names&                                      names,
    const UiSettings&                           settings)
  {
    UiPlayer player;
    player.id           = Id(data.playerId);
    player.name         = names.NameFor(data.playerId, data, character, settings);
    player.inCharacter  = inCharacter;
    player.pseudonymous = data.pseudonymous;
    if (data.nameColor) player.color = ColorText(*data.nameColor);
    if (settings.streamerMode || data.pseudonymous)
    {
      if (settings.streamerMode) player.alias = player.name;
      player.displayName = player.name;
      return player;
    }
    player.displayName = data.displayName;
    player.username    = data.username;
    player.character   = character;
    return player;
  }

  // The UI description of a channel entity; the UI's "all" view is its own
  // aggregate. A guild's channel is named after the guild.
  UiChannel ToUiChannel(Domain::ChatChannelId id, Domain::ChatChannelKind kind, std::string_view guild = {})
  {
    if (kind == Domain::ChatChannelKind::System) return {Id(id), std::string{ChannelKindNames[2]}, "Объявления", false};
    if (kind == Domain::ChatChannelKind::Guild)
      return {Id(id), std::string{ChannelKindNames[1]}, guild.empty() ? std::string{"Гильдия"} : std::string{guild}, true};
    return {Id(id), std::string{ChannelKindNames[0]}, "Общий", true};
  }

  std::string_view GuildRoleName(Domain::GuildRole role)
  {
    return NameOf(GuildRoleNames, role, Domain::GuildRole::Member, GuildRoleNames.front());
  }

  std::string_view GuildRemovalName(Domain::GuildRemovalReason reason)
  {
    return NameOf(GuildRemovalNames, reason, Domain::GuildRemovalReason::Left, GuildRemovalNames.front());
  }

  // Named like every other surface: the real profile, or the alias in streamer mode.
  UiGuildMember ToUiGuildMember(const Domain::GuildMember& member, Names& names, const UiSettings& settings)
  {
    UiGuildMember result{
        .id       = Id(member.profile.playerId),
        .name     = names.NameFor(member.profile.playerId, member.profile, std::nullopt, settings),
        .role     = std::string{GuildRoleName(member.role)},
        .online   = member.online,
        .joinedAt = member.joinedAtUnixMs,
    };
    if (member.mute) result.mute = UiGuildMute{member.mute->reason, member.mute->untilUnixMs};
    return result;
  }

  // The values of the command its action uses; the rest are ignored.
  Dreamsleeve::Client::GuildAction GuildActionOf(Commands::Guild& command)
  {
    namespace Client   = Dreamsleeve::Client;
    const auto  guild  = command.guildId.value;
    const auto  player = command.playerId.value;
    const auto& action = command.action;
    if (action == "invite") return Client::InviteToGuild{guild, player};
    if (action == "answer") return Client::AnswerGuildInvite{guild, command.accept};
    if (action == "leave") return Client::LeaveGuild{guild};
    if (action == "exclude") return Client::ExcludeGuildMember{guild, player};
    if (action == "setRole")
      return Client::SetGuildRole{guild, player, command.role == GuildRoleNames[1] ? Domain::GuildRole::Officer : Domain::GuildRole::Member};
    if (action == "transfer") return Client::TransferGuild{guild, player};
    if (action == "mute") return Client::MuteGuildMember{guild, player, command.minutes, std::move(command.reason)};
    if (action == "unmute") return Client::UnmuteGuildMember{guild, player};
    if (action == "disband") return Client::DisbandGuild{guild};
    return Client::CreateGuild{std::move(command.name)};
  }

  // An unknown source is shown with the least trust.
  std::string_view OriginName(Domain::AnnouncementSource source)
  {
    return NameOf(OriginNames, source, Domain::AnnouncementSource::Server, OriginNames.back());
  }

  std::string_view KindName(Domain::AnnouncementKind kind)
  {
    return NameOf(KindNames, kind, Domain::AnnouncementKind::Announcement, KindNames.front());
  }

  // A mod label is already one checked line (by the server, or by the plugin
  // API for a local refusal); the UI and the log keep a bounded copy.
  constexpr std::size_t MaxLabelCodePoints = 64;

  std::string ModLabel(std::string_view value)
  {
    return std::string{Dreamsleeve::Utils::Text::Prefix(value, MaxLabelCodePoints)};
  }

  UiPlayer ToUiPlayer(const Domain::Player& source, Names& names, const UiSettings& settings)
  {
    auto player =
      ToUiAuthor(source.data, source.characterName, source.characterName.has_value() || source.characterNameWithheld, names, settings);
    const auto& details = source.details;
    player.level        = details.level;
    if (details.race) player.race = Text(details.race->name);
    if (details.place)
    {
      player.zone         = Text(details.place->worldspaceName);
      player.location     = Text(details.place->locationName);
      player.nearbyMarker = Text(details.place->nearbyMarkerName);
      player.markerKind   = Label(MarkerLabel(details.place->markerKind));
      player.interior     = details.place->isInterior;
    }
    else if (source.location)
      player.location = Text(source.location->location.locationName);

    player.activity       = Label(ActivityLabel(details.activity.kind));
    player.activityTarget = details.activity.targetName;
    player.lockDifficulty = Label(LockLabel(details.activity.lockDifficulty));
    if (details.activity.menuKey) player.menu = Label(MenuLabel(*details.activity.menuKey));
    player.gameStartedAt = details.gameStartedAtUnixMs;

    if (!source.actorValues.empty())
    {
      std::vector<UiActorValue> values;
      values.reserve(source.actorValues.size());
      for (const auto& [key, info] : source.actorValues)
      {
        UiActorValue value{key, info.displayName};
        if (const auto* resource = std::get_if<Domain::ResourceActorValue>(&info.state))
          value.value = UiResource{static_cast<double>(resource->current), static_cast<double>(resource->maximum)};
        else
          value.value = static_cast<double>(std::get<Domain::ScalarActorValue>(info.state).value);
        values.push_back(std::move(value));
      }
      std::ranges::sort(values, {}, &UiActorValue::key);
      player.actorValues = std::move(values);
    }
    return player;
  }

  constexpr std::string_view HiddenOwnText = "[скрыто фильтром]";

  // Each code point inside a flagged range becomes one star; whitespace stays so
  // a masked phrase keeps its shape. Ranges were validated on code point bounds.
  std::string MaskFlagged(std::string_view text, const std::vector<Domain::TextSpan>& spans)
  {
    std::string result;
    result.reserve(text.size());
    std::size_t at = 0;
    for (const auto& span : spans)
    {
      result.append(text.substr(at, span.start - at));
      for (std::size_t index = span.start; index < span.start + span.length; ++index)
      {
        const auto byte = text[index];
        if (Dreamsleeve::Utils::Text::Continuation(byte)) continue;
        result += byte == ' ' || byte == '\t' || byte == '\n' || byte == '\r' ? byte : '*';
      }
      at = span.start + span.length;
    }
    result.append(text.substr(at));
    return result;
  }

  // What the local filter lets through: the text, a masked copy, or nothing.
  // One's own hidden text stays as a placeholder so its pending row settles
  // visibly. The one rule for chat lines, bubbles and ground marks.
  std::optional<std::string> FilterText(
    const std::string&                   text,
    const std::vector<Domain::TextSpan>& flagged,
    const UiSettings&                    settings,
    bool                                 own)
  {
    if (flagged.empty() || settings.textFilter == "off") return text;
    if (settings.textFilter == "mask") return MaskFlagged(text, flagged);
    if (own) return std::string{HiddenOwnText};
    return std::nullopt;
  }

  std::optional<std::string> ShownText(const Domain::ChatMessage& message, const UiSettings& settings, bool own)
  {
    return FilterText(message.messageText, message.flagged, settings, own);
  }

  std::string_view MarkKindName(Domain::GroundMarkKind kind)
  {
    return NameOf(MarkKindNames, kind, Domain::GroundMarkKind::Note, MarkKindNames.front());
  }

  // A mark for the UI lists; a hidden own text shows the placeholder, a
  // hidden foreign text is left empty. Streamer mode drops the character snapshot.
  UiGroundMark ToUiGroundMark(const Domain::GroundMark& mark, const UiSettings& settings, bool own)
  {
    UiGroundMark result;
    result.id       = Id(mark.markId);
    result.kind     = std::string{MarkKindName(mark.kind)};
    result.text     = FilterText(mark.text, mark.flagged, settings, own).value_or(own ? std::string{HiddenOwnText} : std::string{});
    result.time     = Domain::ToUnixMilliseconds(mark.createdAt);
    result.location = std::format("{}:{:06X}", mark.placement.locationId.pluginName, mark.placement.locationId.localFormId);
    result.x        = mark.placement.position.X;
    result.y        = mark.placement.position.Y;
    result.z        = mark.placement.position.Z;
    if (!settings.streamerMode) result.character = mark.characterName;
    if (mark.gameDate) result.gameDate = FormatGameDate(*mark.gameDate, settings.markDateStyle);
    return result;
  }

  // The author is named from the snapshot taken at sending, never from the
  // character the player uses now.
  // Announcements are system lines; a client announcement names its player.
  UiMessage ToUiMessage(const Domain::ChatMessage& message, Names& names, const UiSettings& settings)
  {
    UiMessage result;
    result.id        = Id(message.messageId);
    result.channelId = Id(message.channelId);
    result.text      = message.messageText;
    result.time      = Domain::ToUnixMilliseconds(message.sentAt);
    if (message.announcement)
    {
      const auto& value   = *message.announcement;
      result.source       = "system";
      result.announcement = UiAnnouncement{std::string{OriginName(value.source)}, std::string{KindName(value.kind)}, std::nullopt};
      if (!value.signature.empty()) result.announcement->signature = ModLabel(value.signature);
    }
    if (message.author)
      result.author = ToUiAuthor(*message.author, message.characterName, message.characterName.has_value(), names, settings);
    return result;
  }

  // Chat projection with the local filter of server-flagged ranges applied.
  std::optional<UiMessage> ToShownMessage(
    const Domain::ChatMessage&      message,
    Names&                          names,
    const UiSettings&               settings,
    std::optional<Domain::PlayerId> self)
  {
    const bool own  = self && message.author && message.author->playerId == *self;
    auto       text = ShownText(message, settings, own);
    if (!text) return std::nullopt;
    auto result     = ToUiMessage(message, names, settings);
    result.filtered = *text != message.messageText;
    result.text     = std::move(*text);
    return result;
  }

  // The UI phase: a stopped application is disconnected, and a disconnected
  // one that signs in is authenticating.
  ConnectionPhase PhaseOf(const ClientStatus& status)
  {
    if (status.stopped) return ConnectionPhase::Disconnected;
    switch (status.phase)
    {
      case SessionPhase::Disconnected:
        return status.authenticating ? ConnectionPhase::Authenticating : ConnectionPhase::Disconnected;
      case SessionPhase::Connecting:
        return ConnectionPhase::Connecting;
      case SessionPhase::Opening:
        return ConnectionPhase::Opening;
      case SessionPhase::Ready:
        return ConnectionPhase::Connected;
      case SessionPhase::Disconnecting:
        return ConnectionPhase::Disconnecting;
      case SessionPhase::Faulted:
        return ConnectionPhase::Faulted;
    }
    return ConnectionPhase::Disconnected;
  }

  std::string_view PhaseName(const ClientStatus& status)
  {
    return NameOf(PhaseNames, PhaseOf(status), ConnectionPhase::Disconnected, PhaseNames.front());
  }

  ConnectionEvent ConnectionState(const ClientStatus& status)
  {
    return {.connected = PhaseOf(status) == ConnectionPhase::Connected, .phase = std::string{PhaseName(status)}};
  }

  // The saved account name a surface may show: streamer mode hides it.
  std::string ShownUsername(const ClientStatus& status, bool streamerMode)
  {
    return streamerMode ? std::string{} : status.savedUsername;
  }

  AuthEvent AuthState(const ClientStatus& status, bool streamerMode = false)
  {
    AuthEvent event;
    event.authenticating = status.authenticating;
    event.operation      = NameOf(OperationNames, status.authOperation, AuthOperation::None, OperationNames.front());
    event.failure        = NameOf(FailureNames, status.authFailure, ClientAuth::FailureCode::None, FailureNames.front());
    event.error          = ClipError(status.error);
    event.savedLogin     = status.savedLogin;
    event.savedUsername  = ShownUsername(status, streamerMode);
    event.registration   = NameOf(RegistrationNames, status.methods.registration, ClientAuth::RegistrationMode::Unknown, RegistrationNames.front());
    event.steam          = status.methods.steam;
    event.browserFailed  = !status.steamBrowserError.empty();
    event.phase          = PhaseName(status);
    return event;
  }

  MuteEvent MuteState(const ClientStatus& status)
  {
    if (!status.mute) return {};
    return {.muted = true, .reason = status.mute->reason, .until = status.mute->untilUnixMs};
  }

  RoleEvent Role(const ClientStatus& status)
  {
    return {.moderator = status.role == Domain::PlayerRole::Moderator};
  }

  std::string_view SanctionKindName(Domain::SanctionKind kind)
  {
    return NameOf(SanctionKindNames, kind, Domain::SanctionKind::Mute, SanctionKindNames.front());
  }

  // Bridge::Admit let only SanctionKindNames through.
  Domain::SanctionKind SanctionKindOf(std::string_view name)
  {
    return name == SanctionKindNames[1] ? Domain::SanctionKind::Ban : Domain::SanctionKind::Mute;
  }

  UiSanction ToUiSanction(const Domain::Sanction& sanction, std::optional<std::string> name)
  {
    return {
        .playerId = Id(sanction.playerId),
        .name     = std::move(name),
        .kind     = std::string{SanctionKindName(sanction.kind)},
        .reason   = sanction.reason,
        .issuedAt = sanction.issuedAtUnixMs,
        .until    = sanction.untilUnixMs
    };
  }

  SessionEndedEvent Ended(const Domain::SessionEnd& end)
  {
    return {
        .reason = std::string{NameOf(EndNames, end.reason, Domain::SessionEndReason::AccessRevoked, EndNames.back())},
        .text   = end.text,
        .until  = end.untilUnixMs
    };
  }

  // Known server refusals in the UI language; unknown codes keep the server text.
  std::string RejectionText(Dreamsleeve::Client::RequestRejectionCode code, std::string_view message)
  {
    using Code = Dreamsleeve::Client::RequestRejectionCode;
    switch (code)
    {
      case Code::TextNotAllowed:
        return "Сообщение содержит запрещённые слова";
      case Code::Muted:
        if (message.starts_with("Muted in this guild")) return "Мут в этой гильдии: можно только читать";
        return "Вы в муте: писать сейчас нельзя";
      case Code::RateLimited:
        return "Слишком часто или повтор того же сообщения. Подождите немного";
      case Code::AnnouncementNotAllowed:
        return "Сервер не принимает объявления от этого источника";
      case Code::GroundMarkAreaFull:
        return "Здесь уже слишком много меток";
      case Code::GroundMarkNotFound:
        return "Метка не найдена или уже удалена";
      case Code::HiddenIdentityNotAllowed:
        return "Сервер не разрешает скрывать имя";
      case Code::NotChannelMember:
        if (message.starts_with("Player is not a member of this guild")) return "Вы не состоите в этой гильдии";
        break;
      case Code::InvalidRequest:
        if (message.starts_with("Message exceeds")) return "Сообщение слишком длинное";
        if (message.starts_with("Note exceeds")) return "Текст метки слишком длинный";
        if (message.starts_with("The mark is not where")) return "Метку нельзя оставить здесь";
        break;
      default:
        break;
    }
    return message.empty() ? std::string{"Сервер отклонил сообщение"} : std::string{message};
  }

  // Refusals of a display name change, in the UI language.
  std::string DisplayNameRejectionText(Dreamsleeve::Client::RequestRejectionCode code, std::string_view message)
  {
    using Code = Dreamsleeve::Client::RequestRejectionCode;
    switch (code)
    {
      case Code::TextNotAllowed:
        return "Имя содержит запрещённые слова";
      case Code::DisplayNameChangeNotAllowed:
        return "Сервер не разрешает менять имя";
      case Code::RateLimited: {
        // "The display name can be changed again in N min."
        constexpr std::string_view prefix = "The display name can be changed again in ";
        if (message.starts_with(prefix))
        {
          std::uint64_t minutes{};
          const auto*   begin  = message.data() + prefix.size();
          const auto    parsed = std::from_chars(begin, message.data() + message.size(), minutes);
          if (parsed.ec == std::errc{} && minutes > 0)
          {
            if (minutes < 120) return std::format("Имя можно сменить снова через {} мин", minutes);
            return std::format("Имя можно сменить снова через {} ч", (minutes + 59) / 60);
          }
        }
        return "Имя меняли недавно. Попробуйте позже";
      }
      case Code::InvalidRequest:
        if (message.starts_with("Display name exceeds")) return "Имя слишком длинное";
        return "Имя пустое или содержит недопустимые символы";
      case Code::Overloaded:
        return "Сервер занят. Попробуйте позже";
      default:
        break;
    }
    return message.empty() ? std::string{"Сервер отклонил имя"} : std::string{message};
  }

  // Refusals of a name color change, in the UI language.
  std::string NameColorRejectionText(Dreamsleeve::Client::RequestRejectionCode code, std::string_view message)
  {
    using Code = Dreamsleeve::Client::RequestRejectionCode;
    switch (code)
    {
      case Code::NameColorUnreadable:
        return "Цвет слишком тёмный: имя будет плохо видно в чате";
      case Code::RateLimited: {
        // "The name color can be changed again in N s."
        constexpr std::string_view prefix = "The name color can be changed again in ";
        std::uint64_t              seconds{};
        if (message.starts_with(prefix) &&
            std::from_chars(message.data() + prefix.size(), message.data() + message.size(), seconds).ec == std::errc{} && seconds > 0)
          return std::format("Цвет можно сменить снова через {} с", seconds);
        return "Цвет меняли только что. Попробуйте чуть позже";
      }
      case Code::InvalidRequest:
        return "Цвет задаётся как #RRGGBB";
      case Code::Overloaded:
        return "Сервер занят. Попробуйте позже";
      default:
        break;
    }
    return message.empty() ? std::string{"Сервер отклонил цвет"} : std::string{message};
  }

  // Refusals of a guild request, in the UI language.
  std::string GuildRejectionText(Dreamsleeve::Client::RequestRejectionCode code, std::string_view message)
  {
    using Code = Dreamsleeve::Client::RequestRejectionCode;
    // "A guild name has at least N characters." and "... at most N ...".
    const auto count = [&](std::string_view prefix) -> std::optional<std::uint64_t> {
      if (!message.starts_with(prefix)) return std::nullopt;
      std::uint64_t value{};
      const auto*   begin = message.data() + prefix.size();
      if (std::from_chars(begin, message.data() + message.size(), value).ec != std::errc{}) return std::nullopt;
      return value;
    };
    switch (code)
    {
      case Code::GuildNameTaken:
        return "Гильдия с таким названием уже есть";
      case Code::GuildFull:
        return "В гильдии нет свободных мест";
      case Code::GuildPlayerLimit:
        return "Достигнут предел гильдий на игрока";
      case Code::GuildServerLimit:
        return "На сервере уже предельное число гильдий";
      case Code::GuildInvitesFull:
        return "У гильдии слишком много ожидающих приглашений";
      case Code::GuildAlreadyMember:
        return "Игрок уже состоит в гильдии";
      case Code::GuildAlreadyInvited:
        return "Игрок уже приглашён";
      case Code::GuildMasterStays:
        return "Глава не может выйти: сначала передайте роль или распустите гильдию";
      case Code::NotPermitted:
        return "Ваша роль в гильдии этого не позволяет";
      case Code::TextNotAllowed:
        return "Название содержит запрещённые слова";
      case Code::TargetNotFound:
        if (message.starts_with("The player is not online")) return "Игрок не в сети";
        if (message.starts_with("No such guild")) return "Гильдия не найдена";
        return "Игрок не в гильдии или приглашения уже нет";
      case Code::InvalidRequest:
        if (const auto minimum = count("A guild name has at least ")) return std::format("Название не короче {} символов", *minimum);
        if (const auto maximum = count("A guild name has at most ")) return std::format("Название не длиннее {} символов", *maximum);
        if (message.starts_with("A guild name has letters")) return "В названии только буквы и цифры";
        if (message.starts_with("A guild role")) return "Роль: участник или офицер";
        break;
      case Code::Overloaded:
        return "Сервер занят. Попробуйте позже";
      default:
        break;
    }
    return message.empty() ? std::string{"Сервер отклонил запрос"} : std::string{message};
  }

  // Refusals of a moderator request, in the UI language.
  std::string ModerationRejectionText(Dreamsleeve::Client::RequestRejectionCode code, std::string_view message, std::string_view field)
  {
    using Code = Dreamsleeve::Client::RequestRejectionCode;
    switch (code)
    {
      case Code::NotPermitted:
        if (message.starts_with("The guild role")) return "Ваша роль в гильдии не позволяет удалить это сообщение";
        if (message.starts_with("Only a moderator")) return "Это может только модератор";
        return "Модератор не может наказать себя или другого модератора";
      case Code::TargetNotFound:
        if (message.starts_with("No such sanction")) return "Такого наказания уже нет";
        if (message.starts_with("No such message")) return "Сообщение не найдено или уже удалено";
        return "Игрок не найден";
      case Code::ChannelNotFound:
        return "Канал не найден";
      case Code::Overloaded:
        return "Сервер занят. Попробуйте позже";
      case Code::InvalidRequest:
        if (field == "reason") return "Нужна короткая причина в одну строку";
        if (field == "minutes") return "Срок — от минуты до десяти лет";
        if (field == "kinds") return "Выберите заметки, метки смерти или оба вида";
        return "Некорректный запрос";
      default:
        break;
    }
    return message.empty() ? std::string{"Сервер отклонил запрос"} : std::string{message};
  }

  std::string_view FailureText(CommandFailureCode code)
  {
    switch (code)
    {
      case CommandFailureCode::StaleGeneration:
        return "Сессия сменилась, сообщение не отправлено";
      case CommandFailureCode::SessionNotReady:
        return "Нет соединения с сервером";
      case CommandFailureCode::Busy:
        return "Слишком много ожидающих сообщений";
      case CommandFailureCode::InvalidRequest:
        return "Некорректный запрос";
      case CommandFailureCode::EncodingFailed:
        return "Не удалось закодировать сообщение";
    }
    return "Сообщение не отправлено";
  }

}
