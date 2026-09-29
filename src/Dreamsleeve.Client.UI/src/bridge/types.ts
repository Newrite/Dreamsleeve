export type Id = string;
export type ConnectionPhase =
  | "disconnected"
  | "authenticating"
  | "connecting"
  | "opening"
  | "connected"
  | "disconnecting"
  | "faulted";
export type AuthOperation =
  | "none"
  | "passwordLogin"
  | "resume"
  | "signOut"
  | "forgetSavedLogin"
  | "resetPassword";
export type AuthFailure =
  | "none"
  | "invalidCredentials"
  | "usernameTaken"
  | "invalidRequest"
  | "registrationDisabled"
  | "busy"
  | "unavailable"
  | "invalidResponse"
  | "credentialStorage"
  | "canceled"
  | "nameNotAllowed";
// Typed authentication state; no password or token ever crosses the bridge.
export interface AuthState {
  authenticating: boolean;
  operation: AuthOperation;
  failure: AuthFailure;
  error: string;
  savedLogin: boolean;
  savedUsername: string;
}
export type ChannelKind =
  "global" | "local" | "party" | "guild" | "whisper" | "system";
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
// only its pseudonym (displayName), no username and no character.
export interface Player {
  id: Id;
  name: string;
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
export type AnnouncementOrigin = "server" | "trustedClient" | "thirdParty";
export type AnnouncementKind = "announcement" | "event" | "admin" | "periodic";
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
export interface Settings {
  showFireflyNames: boolean;
  fireflyNameOcclusion: boolean;
  fireflyNameFontSize: number;
  fireflyNameOffset: number;
  showBubbles: boolean;
  bubbleDuration: number;
  bubbleFade: boolean;
  bubbleFadeDuration: number;
  bubbleFontSize: number;
  bubbleMaxWidth: number;
  bubbleBackground: number;
  combatHideFireflies: boolean;
  combatHideNames: boolean;
  combatHideBubbles: boolean;
  onlineView: "cards" | "list";
  fade: boolean;
  delay: number;
  duration: number;
  idleOpacity: number;
  scale: number;
  fontSize: number;
  font: "serif" | "sans";
  lineHeight: number;
  background: number;
  timestamps: boolean;
  fullColor: boolean;
  nameMode: "username" | "display" | "character";
  streamerMode: boolean;
  textFilter: "off" | "mask" | "hide";
  locked: boolean;
  x: number;
  y: number;
  width: number;
  height: number;
  activationKey: "Enter" | "F2";
  theme: "skyrim" | "contrast";
  // tab: only the announcements tab; all: also "Все"; current: every tab.
  announcementChannels: "tab" | "all" | "current";
  announcementsServer: boolean;
  announcementsTrustedClient: boolean;
  announcementsThirdParty: boolean;
  announcementsEvents: boolean;
  announcementsPeriodic: boolean;
  // Bubble look above fireflies: fill opacity is bubbleBackground; the border
  // and the text colour ("#RRGGBB") are separate. Native Scaleform render only.
  bubbleBorder: boolean;
  bubbleTextColor: string;
  fireflyNameColor: string;
  fireflyHeightOffset: number;
  // Ground marks drawn by the SKSE DLL: which kinds, how many, how far, and
  // the look of their labels. Stored and passed through only.
  showGroundNotes: boolean;
  showDeathMarks: boolean;
  maxVisibleNotes: number;
  maxVisibleDeaths: number;
  groundDrawDistance: number;
  groundNoteOffset: number;
  deathMarkOffset: number;
  groundNameDistance: number;
  groundTextDistance: number;
  groundFontSize: number;
  groundMaxWidth: number;
  groundBackground: number;
  groundBorder: boolean;
  groundTextColor: string;
  deathTextColor: string;
  deathBackground: number;
  deathBorder: boolean;
  combatHideGroundMarks: boolean;
  combatHideGroundText: boolean;
  // Where others see a server pseudonym instead of this player's names.
  // Changed by setIdentityVisibility only; the host saves it once the server agrees.
  hideIdentity: HideIdentity;
}
// off: the names are shown; everywhere: online, fireflies, chat and ground
// marks; exceptGroundMarks: ground marks keep the real profile.
export type HideIdentity = "off" | "everywhere" | "exceptGroundMarks";
// "Hide my name from other players" as the host reports it: mode is the
// choice (the requested one while pending), pending waits for the server,
// pseudonym is what the others see now, error the last refusal.
export interface IdentityState {
  mode: HideIdentity;
  pending: boolean;
  pseudonym?: string;
  error?: string;
}
// A ground mark for the lists: own marks (the server's complete list) and
// marks the server shows nearby. `author` is the host-resolved name of a
// nearby mark, `character` the snapshot at placement (absent in streamer
// mode), `location` the WRLD/CELL key and x/y/z the position in game units.
export type GroundMarkKind = "note" | "death";
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
}
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
  // Applied and saved by the host at once; it re-projects every surface.
  | {
      type: "displaySettings";
      nameMode: Settings["nameMode"];
      streamerMode: boolean;
      textFilter: Settings["textFilter"];
    }
  | { type: "signInSaved" }
  | { type: "signOut" }
  | { type: "forgetLogin" }
  | { type: "disconnect" }
  // A note where the character stands; the host fills the placement.
  | { type: "placeGroundNote"; requestId: string; text: string }
  | { type: "removeGroundMark"; requestId: string; markId: Id }
  // Outside a session only the choice for the next one changes.
  | { type: "setIdentityVisibility"; hiding: HideIdentity };
export type AuthEvent = { type: "auth"; phase: ConnectionPhase } & AuthState;
export type HostEvent =
  | {
      type: "snapshot";
      channels: Channel[];
      messages: Message[];
      players: Player[];
      selfId: Id;
      serverName: string;
      settings?: Partial<Settings>;
      // Same session projected again (names or ignore list changed).
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
  // Personal ignore list of this server, already named for current settings.
  | { type: "ignored"; players: { id: Id; name: string }[] }
  | { type: "messages"; messages: Message[] }
  | { type: "players"; players: Player[] }
  | { type: "show" }
  | { type: "hide" }
  | { type: "activate" }
  | { type: "deactivate" }
  | { type: "connection"; connected: boolean; phase?: ConnectionPhase }
  // Sent when the page is (re)created: applies the saved window settings before any snapshot.
  | { type: "settings"; settings: Partial<Settings> }
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
