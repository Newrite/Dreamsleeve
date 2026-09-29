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
  | { type: "disconnect" };
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
    }
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
