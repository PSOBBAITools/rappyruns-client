// Data shapes of the host <-> UI contract (desktop/docs/ipc.md is the prose
// version; RappyRuns.App must serialize exactly these, camelCase, via
// JsonSerializerDefaults.Web). Types only - no runtime code, so the pure
// helpers and their node:test files can import it.

// ---- Localizable text ----

/** An argument of a localizable message: plain values, or another message (rendered first). */
export type MsgArg = string | number | null | Msg;

/**
 * Text the UI renders in the current language: a strings.json key with its
 * arguments (the host never sends translated text, so a language switch is
 * instant and needs no round trip), or raw text that is not localized
 * (server error bodies, paths).
 */
export type Msg = { key: string; args?: MsgArg[] } | { text: string };

/** How a status line is drawn. 'error' is the Lisp client's red text. */
export type Tone = 'neutral' | 'ok' | 'busy' | 'error';

export interface Line {
  msg: Msg;
  tone?: Tone;
}

// ---- 4 Hz state ----

export type QuestStatus =
  | { kind: 'none' }
  /** Quest loaded in the lobby, timer not started (:quest-waiting). */
  | { kind: 'waiting'; name: string }
  | {
      kind: 'active';
      slug: string;
      /** Other matching definitions (the " (+N)" suffix). */
      others: number;
      /** Live clock, m:ss, formatted ONCE by the host and shared with the window title (ui-shell R21). */
      elapsed: string;
      recording: boolean;
      /** Ghost race: target time (m:ss.mmm) and the live gap ("-3.2s"), gap null before the first matched room. */
      ghost: { target: string; gap: string | null } | null;
    };

export type OverlayCorner =
  | 'top-right'
  | 'top-center'
  | 'top-left'
  | 'middle-right'
  | 'middle-left'
  | 'bottom-right'
  | 'bottom-center'
  | 'bottom-left'
  | 'custom';

/** The settings the UI shows (config.sexp keys in camelCase; autostart is read from the registry). */
export interface Settings {
  language: string;
  serverUrl: string;
  apiToken: string;
  trackingOnly: boolean;
  trackingPrivate: boolean;
  recordAudio: boolean;
  /** For :record-storage-note; 0 = unlimited (note hidden). */
  recordMaxTotalGb: number;
  /** Server-side value, cached (ui-shell §1.4.3). */
  autoPublish: boolean;
  /** Resolved recordings folder (never empty: the default folder when config is ""). */
  recordDir: string;
  ghostRace: boolean;
  ghostOverlay: boolean;
  ghostMarker: boolean;
  overlayCorner: OverlayCorner;
  pinshareEnabled: boolean;
  pinshareChannel: string;
  autoUpdate: boolean;
  closeToTray: boolean;
  autostart: boolean;
  startMinimized: boolean;
  rankToast: boolean;
  triggerLog: boolean;
}

/** Settings written with the plain `settings.set` (save-and-apply on click, no side effects beyond those in ipc.md). */
export type SimpleSettingKey =
  | 'trackingOnly'
  | 'trackingPrivate'
  | 'recordAudio'
  | 'ghostRace'
  | 'ghostOverlay'
  | 'ghostMarker'
  | 'overlayCorner'
  | 'autoUpdate'
  | 'closeToTray'
  | 'startMinimized'
  | 'rankToast';

/**
 * Everything the status panes and settings show. The `state` event carries a
 * PATCH: only the top-level keys whose value changed since the last event,
 * each replacing the previous value whole (applyStatePatch in state.ts).
 */
export interface HostState {
  /** Moderator or admin: Rooms tab, rule registration. */
  moderator: boolean;
  /** Pin Share limited rollout verdict: the Pin Share group is shown only when true. */
  pinshareAllowed: boolean;
  game: Line;
  server: Line;
  token: Line;
  quest: QuestStatus;
  /** Pin Share relay status (pinshare-status-text) and pin set line. */
  pinshare: Line;
  pinSet: Line;
  /** Updates group status (:version-status). */
  version: Line;
  /** A pairing worker is running (Link button disabled). */
  pairing: boolean;
  /** An update check/download is running (Check button disabled). */
  updating: boolean;
  settings: Settings;
}

// ---- Lists ----

export interface RunRow {
  /** Stable id (GUID) of the queue entry. */
  id: string;
  /** :quest-name, else :quest-slug. */
  quest: string;
  timeMs: number;
  players: number;
  /** PB category: the party column shows "/PB". */
  pb: boolean;
  /** run-video-label, null = empty cell. */
  video: Msg | null;
  /** run-status-label then the entry notes; joined with " · ". */
  status: Msg[];
  /** Red status (rejected / failed / upload failed). */
  statusError?: boolean;
  /** Run page; null while unsent. */
  url: string | null;
  /** Has a saved recording (:video-path). */
  hasRecording: boolean;
}

export type Trigger =
  | { type: 'monster'; monster: number }
  | { type: 'floor-switch'; floor: number; switch: number }
  | { type: 'register'; register: number }
  | { type: 'warp-in' };

export interface RoomRow {
  id: string;
  area: string;
  kind: 'clear' | 'enemy';
  /** Enemy name (kind 'enemy'). */
  name: string | null;
  trigger: Trigger | null;
}

export interface QuestParent {
  slug: string;
  name: string;
}

// ---- Dialogs ----

/**
 * A message box the UI shows: the result of an action, or a host-initiated
 * report (`notice` event). With `confirmUrl`, it is a Yes/No question and Yes
 * opens that URL.
 */
export interface Notice {
  message: Msg;
  error?: boolean;
  confirmUrl?: string;
}

// ---- Envelope results ----

export interface AppSnapshot {
  version: string;
  debug: boolean;
  language: string;
  languages: { code: string; label: string }[];
  strings: Record<string, string>;
  state: HostState;
  runs: RunRow[];
  rooms: RoomRow[];
}

export type RulePrepareResult =
  | { ok: true; parents: QuestParent[]; detected: string | null; rows: RoomRow[] }
  | { ok: false; notice: Notice };

export interface RuleCreateParams {
  parent: string;
  name: string;
  description: string;
  end: Trigger;
  /** null = inherit the parent's start. */
  start: Trigger | null;
}

export type OverwriteCheckResult = { ok: true; name: string; pins: number; arrows: number } | { ok: false; notice: Notice };

export interface HostEvents {
  state: Partial<HostState>;
  runs: RunRow[];
  rooms: RoomRow[];
  notice: Notice;
}
