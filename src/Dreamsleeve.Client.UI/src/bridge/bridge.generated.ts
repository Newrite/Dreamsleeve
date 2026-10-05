// Generated from src/Dreamsleeve.Client/Host/Bridge.ixx by the native test
// "The web UI bridge module is generated from the bridge tables"; do not edit.
// Regenerate: set DREAMSLEEVE_WRITE_GENERATED=1 and run Dreamsleeve.Client.Tests.

// The "type" of every host event and UI command.
export const eventTypes = [
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
  "channels",
  "show",
  "hide",
  "activate",
  "deactivate",
  "phantom",
] as const;
export const commandTypes = [
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
  "phantom",
] as const;

// Enum strings of the events.
export const connectionPhases = [
  "disconnected",
  "authenticating",
  "connecting",
  "opening",
  "connected",
  "disconnecting",
  "faulted",
] as const;
export const authOperations = [
  "none",
  "passwordLogin",
  "resume",
  "signOut",
  "forgetSavedLogin",
  "resetPassword",
  "steamLogin",
] as const;
export const authFailures = [
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
] as const;
export const announcementOrigins = [
  "server",
  "trustedClient",
  "thirdParty",
] as const;
export const announcementKinds = [
  "announcement",
  "event",
  "admin",
  "periodic",
] as const;
export const groundMarkKinds = [
  "note",
  "death",
] as const;
export const channelKinds = [
  "global",
  "guild",
  "system",
] as const;
export const hidingModes = [
  "off",
  "everywhere",
  "exceptGroundMarks",
] as const;
export const sessionEndReasons = [
  "revoked",
  "banned",
  "kicked",
  "addressBanned",
] as const;
export const sanctionKinds = [
  "mute",
  "ban",
] as const;
export const registrationModes = [
  "unknown",
  "open",
  "steam",
  "manual",
] as const;
export const guildRoles = [
  "member",
  "officer",
  "master",
] as const;
export const guildRemovalReasons = [
  "left",
  "excluded",
  "disbanded",
] as const;
export const guildActions = [
  "create",
  "invite",
  "answer",
  "leave",
  "exclude",
  "setRole",
  "transfer",
  "mute",
  "unmute",
  "disband",
] as const;

// Bounds of text the host sends: chat and mark text, snapshot lines per
// channel, error strings (UTF-8 bytes, never more UTF-16 units).
export const maxText = 16000;
export const maxSnapshotRows = 500;
export const maxError = 512;
// Bytes of a moderator's reason the host accepts; the server's limit is smaller.
export const maxReason = 1024;
