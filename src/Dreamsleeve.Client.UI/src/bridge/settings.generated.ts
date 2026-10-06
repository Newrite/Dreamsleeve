// Generated from src/Dreamsleeve.Client/Host/UiSettings.ixx by the native test
// "The web UI settings module is generated from UiSettings"; do not edit.
// Regenerate: set DREAMSLEEVE_WRITE_GENERATED=1 and run Dreamsleeve.Client.Tests.
export interface Settings {
  fireflyGuildmatesOnly: boolean;
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
  combatHideFireflies: boolean;
  combatHideNames: boolean;
  combatHideBubbles: boolean;
  announcementChannels: "tab" | "all" | "current";
  announcementsServer: boolean;
  announcementsTrustedClient: boolean;
  announcementsThirdParty: boolean;
  announcementsEvents: boolean;
  announcementsPeriodic: boolean;
  bubbleBorder: boolean;
  bubbleTextColor: string;
  fireflyNameColor: string;
  fireflyHeightOffset: number;
  showGroundNotes: boolean;
  showDeathMarks: boolean;
  markGuildmatesOnly: boolean;
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
  markDateStyle: "tamriel" | "earth";
  deathDateHeader: boolean;
  noteDateHeader: boolean;
  markDateColor: string;
  publishPhantoms: boolean;
  showPhantoms: boolean;
  phantomFallback: boolean;
  combatHidePhantoms: boolean;
  maxVisiblePhantoms: number;
  phantomDrawDistance: number;
  phantomOpacity: number;
  phantomColor: string;
  phantomSampleRate: number;
  phantomDelayMs: number;
  phantomExtrapolationMs: number;
  phantomTimeoutMs: number;
  phantomMemoryMiB: number;
  phantomCacheMiB: number;
  phantomUploadKiB: number;
  phantomDownloadKiB: number;
}

export const defaults: Settings = {
  fireflyGuildmatesOnly: false,
  showFireflyNames: true,
  fireflyNameOcclusion: true,
  fireflyNameFontSize: 18,
  fireflyNameOffset: 35,
  showBubbles: true,
  bubbleDuration: 8,
  bubbleFade: true,
  bubbleFadeDuration: 1,
  bubbleFontSize: 16,
  bubbleMaxWidth: 320,
  bubbleBackground: 0.65,
  onlineView: "cards",
  fade: true,
  delay: 12,
  duration: 1,
  idleOpacity: 0,
  scale: 1,
  fontSize: 16,
  font: "sans",
  lineHeight: 1.45,
  background: 0.82,
  timestamps: true,
  fullColor: false,
  nameMode: "display",
  streamerMode: false,
  textFilter: "off",
  locked: true,
  x: 0.025,
  y: 0.42,
  width: 680,
  height: 390,
  activationKey: "Enter",
  theme: "skyrim",
  combatHideFireflies: false,
  combatHideNames: false,
  combatHideBubbles: false,
  announcementChannels: "all",
  announcementsServer: true,
  announcementsTrustedClient: true,
  announcementsThirdParty: true,
  announcementsEvents: true,
  announcementsPeriodic: true,
  bubbleBorder: true,
  bubbleTextColor: "#EEECE5",
  fireflyNameColor: "#EEECE5",
  fireflyHeightOffset: 110,
  showGroundNotes: true,
  showDeathMarks: true,
  markGuildmatesOnly: false,
  maxVisibleNotes: 16,
  maxVisibleDeaths: 16,
  groundDrawDistance: 4096,
  groundNoteOffset: 5,
  deathMarkOffset: 5,
  groundNameDistance: 600,
  groundTextDistance: 150,
  groundFontSize: 16,
  groundMaxWidth: 320,
  groundBackground: 0.65,
  groundBorder: true,
  groundTextColor: "#EEECE5",
  deathTextColor: "#D9534F",
  deathBackground: 0.65,
  deathBorder: true,
  combatHideGroundMarks: false,
  combatHideGroundText: false,
  markDateStyle: "tamriel",
  deathDateHeader: true,
  noteDateHeader: false,
  markDateColor: "#A9A69B",
  publishPhantoms: true,
  showPhantoms: true,
  phantomFallback: true,
  combatHidePhantoms: false,
  maxVisiblePhantoms: 4,
  phantomDrawDistance: 4096,
  phantomOpacity: 0.6,
  phantomColor: "#8CCCCC",
  phantomSampleRate: 20,
  phantomDelayMs: 100,
  phantomExtrapolationMs: 100,
  phantomTimeoutMs: 1000,
  phantomMemoryMiB: 512,
  phantomCacheMiB: 1024,
  phantomUploadKiB: 5120,
  phantomDownloadKiB: 5120,
};

// Accepted ranges of the numeric settings; the host clamps to them.
export const limits = {
  fireflyNameFontSize: { min: 8, max: 48 },
  fireflyNameOffset: { min: 0, max: 512 },
  bubbleDuration: { min: 1, max: 60 },
  bubbleFadeDuration: { min: 0.1, max: 5 },
  bubbleFontSize: { min: 8, max: 48 },
  bubbleMaxWidth: { min: 120, max: 800 },
  bubbleBackground: { min: 0, max: 1 },
  delay: { min: 0, max: 120 },
  duration: { min: 0, max: 5 },
  idleOpacity: { min: 0, max: 1 },
  scale: { min: 0.7, max: 1.5 },
  fontSize: { min: 12, max: 26 },
  lineHeight: { min: 1.1, max: 2 },
  background: { min: 0, max: 1 },
  x: { min: 0, max: 1 },
  y: { min: 0, max: 1 },
  width: { min: 320, max: 1600 },
  height: { min: 220, max: 1200 },
  fireflyHeightOffset: { min: 0, max: 512 },
  maxVisibleNotes: { min: 1, max: 64 },
  maxVisibleDeaths: { min: 1, max: 64 },
  groundDrawDistance: { min: 0, max: 16384 },
  groundNoteOffset: { min: -64, max: 256 },
  deathMarkOffset: { min: -64, max: 256 },
  groundNameDistance: { min: 50, max: 4096 },
  groundTextDistance: { min: 50, max: 4096 },
  groundFontSize: { min: 8, max: 48 },
  groundMaxWidth: { min: 120, max: 800 },
  groundBackground: { min: 0, max: 1 },
  deathBackground: { min: 0, max: 1 },
  maxVisiblePhantoms: { min: 0, max: 16 },
  phantomDrawDistance: { min: 0, max: 16384 },
  phantomOpacity: { min: 0, max: 1 },
  phantomSampleRate: { min: 1, max: 50 },
  phantomDelayMs: { min: 0, max: 500 },
  phantomExtrapolationMs: { min: 0, max: 250 },
  phantomTimeoutMs: { min: 500, max: 5000 },
  phantomMemoryMiB: { min: 64, max: 2048 },
  phantomCacheMiB: { min: 0, max: 8192 },
  phantomUploadKiB: { min: 64, max: 8192 },
  phantomDownloadKiB: { min: 64, max: 8192 },
} as const;

// Sent with displaySettings: the host applies and saves them at once, on every surface.
export const instantKeys = [
  "nameMode",
  "streamerMode",
  "textFilter",
  "markDateStyle",
  "markGuildmatesOnly",
] as const;
