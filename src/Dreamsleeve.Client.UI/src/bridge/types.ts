import type { Settings } from "./settings.generated";
import type {
  announcementKinds,
  announcementOrigins,
  authFailures,
  authOperations,
  channelKinds,
  commandTypes,
  connectionPhases,
  eventTypes,
  groundMarkKinds,
  guildActions,
  guildRemovalReasons,
  guildRoles,
  hidingModes,
  registrationModes,
  sanctionKinds,
  sessionEndReasons,
} from "./bridge.generated";
export type { Settings };
export type Id = string;
// The enum strings are the host's (Host/Bridge.ixx, bridge.generated.ts).
export type ConnectionPhase = (typeof connectionPhases)[number];
export type AuthOperation = (typeof authOperations)[number];
export type AuthFailure = (typeof authFailures)[number];
export type RegistrationMode = (typeof registrationModes)[number];
// Typed authentication state; no password or token ever crosses the bridge.
export interface AuthState {
  authenticating: boolean;
  operation: AuthOperation;
  failure: AuthFailure;
  error: string;
  savedLogin: boolean;
  savedUsername: string;
  // Who may register on the server ("unknown" until it answers) and whether
  // it signs in through Steam.
  registration: RegistrationMode;
  steam: boolean;
  // A Steam sign-in waits, but the browser did not open its page.
  browserFailed: boolean;
}
export type ChannelKind = (typeof channelKinds)[number];
export interface Channel {
  id: Id;
  kind: ChannelKind;
  name: string;
  writable: boolean;
}
export interface ActorValue {
  key: string;
  name: string;
  value: number | { current: number; maximum: number };
}
// `name` is the host-resolved label for the current name settings. In streamer
// mode `alias` carries the local pseudonym and the real username, displayName
// and character never arrive (displayName repeats the alias, username is empty).
// `pseudonymous`: the player hides their names from everyone; the server sent
// only its pseudonym (displayName), no username, no character and no color.
// `color` ("#RRGGBB") is how the player's name is drawn in chat.
export interface Player {
  id: Id;
  name: string;
  color?: string;
  alias?: string;
  inCharacter: boolean;
  displayName: string;
  username: string;
  character?: string;
  level?: number;
  location?: string;
  zone?: string;
  race?: string;
  nearbyMarker?: string;
  markerKind?: string;
  interior?: boolean;
  activity?: string;
  activityTarget?: string;
  lockDifficulty?: string;
  menu?: string;
  gameStartedAt?: number;
  actorValues?: ActorValue[];
  pseudonymous?: boolean;
}
// The server assigns the origin. A third-party signature is declared by the
// mod itself and does not raise trust; it is shown as received, as plain text.
export type AnnouncementOrigin = (typeof announcementOrigins)[number];
export type AnnouncementKind = (typeof announcementKinds)[number];
export interface Announcement {
  origin: AnnouncementOrigin;
  kind: AnnouncementKind;
  signature?: string;
}
// filtered: the host masked or replaced the text by the local filter of
// server-flagged ranges; the original text never crossed the bridge.
// A system line is an announcement of a system channel; its `author` is the
// player whose client posted it, absent for the server.
export type Message = {
  id: Id;
  channelId: Id;
  text: string;
  time: number;
  filtered?: boolean;
} & (
  | { source: "player"; author: Player }
  | { source: "system"; announcement: Announcement; author?: Player }
);
// off: the names are shown; everywhere: online, fireflies, chat and ground
// marks; exceptGroundMarks: ground marks keep the real profile.
export type HideIdentity = (typeof hidingModes)[number];
// "Hide my name from other players" as the host reports it: mode is the
// choice (the requested one while pending), pending waits for the server,
// pseudonym is what the others see now, error the last refusal.
export interface IdentityState {
  mode: HideIdentity;
  pending: boolean;
  pseudonym?: string;
  error?: string;
}
// A change of the own display name as the host reports it: pending waits
// for the server, changed is the name it has just stored, error the last refusal.
export interface DisplayNameState {
  pending: boolean;
  changed?: string;
  error?: string;
}
// The routes of client.toml by name, the main one first, when there are
// others: active carries the traffic, reached tells whether it answered,
// chosen is the player's pick ("" automatic).
export interface RoutesState {
  routes: string[];
  active: string;
  chosen: string;
  reached: boolean;
}
// A change of the own name color, like DisplayNameState: changed is the
// "#RRGGBB" the server has just stored.
export interface NameColorState {
  pending: boolean;
  changed?: string;
  error?: string;
}
// The player's own mute as the host reports it: the moderator's reason and
// when it ends (Unix ms; absent: until lifted). muted is false without one.
export interface MuteState {
  muted: boolean;
  reason: string;
  until?: number;
}
export type SessionEndReason = (typeof sessionEndReasons)[number];
// Why the server ended the session or refused sign-in: the moderator's reason
// and the end of a ban (Unix ms; absent: until lifted).
export interface SessionEndState {
  reason: SessionEndReason;
  text: string;
  until?: number;
}
// A ground mark for the lists: own marks (the server's complete list) and
// marks the server shows nearby. `author` is the host-resolved name of a
// nearby mark, `character` the snapshot at placement (absent in streamer
// mode), `location` the WRLD/CELL key and x/y/z the position in game units.
export type GroundMarkKind = (typeof groundMarkKinds)[number];
export interface GroundMark {
  id: Id;
  kind: GroundMarkKind;
  text: string;
  time: number;
  author?: string;
  character?: string;
  location: string;
  x: number;
  y: number;
  z: number;
  // Formatted by the host in the chosen markDateStyle; absent on old marks.
  gameDate?: string;
}
export type SanctionKind = (typeof sanctionKinds)[number];
// A sanction in force as a moderator lists it: the player by ID, named by the
// host when it has met them; until is Unix ms, absent until lifted.
export interface Sanction {
  playerId: Id;
  name?: string;
  kind: SanctionKind;
  reason: string;
  issuedAt: number;
  until?: number;
}
export type GuildRole = (typeof guildRoles)[number];
export type GuildRemovalReason = (typeof guildRemovalReasons)[number];
// A mute inside one guild: reading only; until is Unix ms, absent until lifted.
export interface GuildMute {
  reason: string;
  until?: number;
}
// A member as guildmates see them: `name` is the host's label of the real
// profile (the local alias in streamer mode); never a server pseudonym.
export interface GuildMember {
  id: Id;
  name: string;
  role: GuildRole;
  online: boolean;
  joinedAt: number;
  mute?: GuildMute;
}
export interface Guild {
  id: Id;
  name: string;
  channelId: Id;
  createdAt: number;
  members: GuildMember[];
}
// An invitation waiting for the player's answer; inviter is the host's name
// for the inviting player when it has met them.
export interface GuildInvite {
  guildId: Id;
  guildName: string;
  invitedBy: Id;
  inviter?: string;
  expires: number;
}
// The server's limits; a lowered limit removes nobody.
export interface GuildLimits {
  perPlayer: number;
  members: number;
  nameMin: number;
  nameMax: number;
}
// What a guild command asks; the server judges roles, the name and limits.
export type GuildAction =
  | { action: "create"; name: string }
  | { action: "invite"; guildId: Id; playerId: Id }
  | { action: "answer"; guildId: Id; accept: boolean }
  | { action: "leave"; guildId: Id }
  | { action: "exclude"; guildId: Id; playerId: Id }
  | {
      action: "setRole";
      guildId: Id;
      playerId: Id;
      role: Exclude<GuildRole, "master">;
    }
  | { action: "transfer"; guildId: Id; playerId: Id }
  // No minutes: until lifted.
  | {
      action: "mute";
      guildId: Id;
      playerId: Id;
      minutes?: number;
      reason: string;
    }
  | { action: "unmute"; guildId: Id; playerId: Id }
  | { action: "disband"; guildId: Id };
export type Command =
  | { type: "sendChat"; channelId: Id; text: string; requestId: string }
  | { type: "close" }
  | { type: "saveSettings"; settings: Settings; revision: number }
  // displayName present and non-empty: register first, then sign in.
  | {
      type: "signIn";
      username: string;
      password: string;
      remember: boolean;
      displayName?: string;
    }
  | { type: "ignore"; playerId: Id }
  | { type: "unignore"; playerId: Id }
  // The host takes the instantKeys settings, saves them and re-projects every surface.
  | { type: "displaySettings"; settings: Settings }
  | { type: "signInSaved" }
  // The browser opens Steam; remember saves the login like a password sign-in.
  | { type: "signInSteam"; remember: boolean }
  // The page of the waiting Steam sign-in onto the clipboard.
  | { type: "copySteamLink" }
  // An administrator's one-time code and the new password; then a normal sign-in.
  | { type: "resetPassword"; code: string; password: string }
  | { type: "signOut" }
  | { type: "forgetLogin" }
  | { type: "disconnect" }
  // A note where the character stands; the host fills the placement.
  | { type: "placeGroundNote"; requestId: string; text: string }
  | { type: "removeGroundMark"; requestId: string; markId: Id }
  // Outside a session only the choice for the next one changes.
  | { type: "setIdentityVisibility"; hiding: HideIdentity }
  // In a session only; the server applies its word list and change interval.
  | { type: "changeDisplayName"; displayName: string }
  | { type: "setNameColor"; color: string }
  | { type: "chooseRoute"; route: string }
  // Moderator tools; the server checks the role and whom a moderator may act
  // on. No minutes: until lifted. Each is answered with moderationResult.
  | {
      type: "sanctionPlayer";
      requestId: string;
      playerId: Id;
      kind: SanctionKind;
      minutes?: number;
      reason: string;
      // A ban also covers the player's devices; never sent for a mute.
      devices?: boolean;
    }
  | {
      type: "liftSanction";
      requestId: string;
      playerId: Id;
      kind: SanctionKind;
    }
  | { type: "kickPlayer"; requestId: string; playerId: Id; reason: string }
  | { type: "listSanctions"; requestId: string }
  | { type: "listPlayerMarks"; requestId: string; playerId: Id }
  | {
      type: "clearPlayerMarks";
      requestId: string;
      playerId: Id;
      notes: boolean;
      deaths: boolean;
    }
  | {
      type: "deleteChatMessage";
      requestId: string;
      channelId: Id;
      messageId: Id;
    }
  // Answered with guildResult.
  | ({ type: "guild"; requestId: string } & GuildAction);
export type AuthEvent = { type: "auth"; phase: ConnectionPhase } & AuthState;
export type HostEvent =
  | {
      type: "snapshot";
      channels: Channel[];
      messages: Message[];
      players: Player[];
      selfId: Id;
      serverName: string;
      // The session the page already shows, projected again (names or ignore
      // list changed, or Core's state queue overflowed while the game stood still).
      refresh?: boolean;
      // The session can place marks; the player's own marks as the server
      // lists them, and the marks it shows nearby.
      groundMarksSupported?: boolean;
      groundMarks?: GroundMark[];
      nearbyMarks?: GroundMark[];
    }
  // The server replaced the own list: placed, evicted, removed or expired.
  | { type: "groundMarks"; marks: GroundMark[] }
  // The marks shown near the player changed.
  | { type: "nearbyMarks"; marks: GroundMark[] }
  // Outcome of placeGroundNote (markId, evictedId) or removeGroundMark (removed).
  | {
      type: "markResult";
      requestId: string;
      markId?: Id;
      evictedId?: Id;
      removed?: boolean;
      error?: string;
    }
  | ({ type: "identity" } & IdentityState)
  | ({ type: "displayName" } & DisplayNameState)
  | ({ type: "nameColor" } & NameColorState)
  | ({ type: "routes" } & RoutesState)
  | ({ type: "mute" } & MuteState)
  | ({ type: "sessionEnded" } & SessionEndState)
  // The player's role; a moderator gets the moderator tools.
  | { type: "role"; moderator: boolean }
  // Messages a moderator removed: gone from every surface.
  | { type: "messagesRemoved"; channelId: Id; messageIds: Id[] }
  // The answer to a moderator request: error on a refusal, otherwise the
  // fields of its kind.
  | {
      type: "moderationResult";
      requestId: string;
      error?: string;
      sanction?: Sanction;
      sanctions?: Sanction[];
      playerId?: Id;
      marks?: GroundMark[];
      removed?: number;
    }
  // The player's guilds and invitations, complete, once the server sent them;
  // removed names the guilds the player left since the previous one.
  | {
      type: "guilds";
      guilds: Guild[];
      invites: GuildInvite[];
      limits: GuildLimits;
      removed: { guildId: Id; name: string; reason: GuildRemovalReason }[];
    }
  // The answer to a guild command: the guild (a new one's ID for create) or the refusal.
  | { type: "guildResult"; requestId: string; guildId?: Id; error?: string }
  // The channels of the session changed: a guild came or went.
  | { type: "channels"; channels: Channel[] }
  // Personal ignore list of this server, already named for current settings.
  | { type: "ignored"; players: { id: Id; name: string }[] }
  | { type: "messages"; messages: Message[] }
  | { type: "players"; players: Player[] }
  | { type: "show" }
  | { type: "hide" }
  | { type: "activate" }
  | { type: "deactivate" }
  | { type: "connection"; connected: boolean; phase?: ConnectionPhase }
  // The host's settings, normalized, every key present: sent when the page is
  // (re)created, before any snapshot, and when the host changes them itself.
  // Snapshots carry none, so a re-projection never undoes unsaved edits.
  | { type: "settings"; settings: Settings }
  | AuthEvent
  | { type: "sendResult"; requestId: string; messageId: Id; error?: never }
  | { type: "sendResult"; requestId: string; error: string; messageId?: never }
  | { type: "settingsResult"; revision: number; error?: string }
  // An announcement of another mod through the API was not published.
  | {
      type: "announcementResult";
      channelId: Id;
      source: string;
      text: string;
      error: string;
    };
export type Send = (command: Command) => boolean;
// The unions above name exactly the host's events and commands: a type added
// on one side only fails to compile.
type Same<A, B> = [A] extends [B] ? ([B] extends [A] ? true : false) : false;
type Assert<T extends true> = T;
export type EventTypesMatch = Assert<
  Same<HostEvent["type"], (typeof eventTypes)[number]>
>;
export type CommandTypesMatch = Assert<
  Same<Command["type"], (typeof commandTypes)[number]>
>;
export type GuildActionsMatch = Assert<
  Same<GuildAction["action"], (typeof guildActions)[number]>
>;
