export type Id = string;
export type ConnectionPhase =
  | "disconnected"
  | "authenticating"
  | "connecting"
  | "opening"
  | "connected"
  | "disconnecting"
  | "faulted";
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
export interface Player {
  id: Id;
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
export type Message = { id: Id; channelId: Id; text: string; time: number } & (
  { source: "player"; author: Player } | { source: "system" }
);
export interface Settings {
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
  nameMode: "display" | "account";
  locked: boolean;
  x: number;
  y: number;
  width: number;
  height: number;
  activationKey: "Enter" | "F2";
  theme: "skyrim" | "contrast";
}
export type Command =
  | { type: "sendChat"; channelId: Id; text: string; requestId: string }
  | { type: "close" }
  | { type: "saveSettings"; settings: Settings; revision: number };
export type HostEvent =
  | {
      type: "snapshot";
      channels: Channel[];
      messages: Message[];
      players: Player[];
      selfId: Id;
      serverName: string;
      settings?: Partial<Settings>;
    }
  | { type: "messages"; messages: Message[] }
  | { type: "players"; players: Player[] }
  | { type: "show" }
  | { type: "hide" }
  | { type: "activate" }
  | { type: "deactivate" }
  | { type: "connection"; connected: boolean; phase?: ConnectionPhase }
  | { type: "sendResult"; requestId: string; messageId: Id; error?: never }
  | { type: "sendResult"; requestId: string; error: string; messageId?: never }
  | { type: "settingsResult"; revision: number; error?: string };
export type Send = (command: Command) => boolean;
