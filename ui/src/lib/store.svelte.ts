// The UI's reactive copy of the host: the handshake snapshot, the merged
// `state` patches, the runs and rooms lists, and tr() for the current
// language. Components read `ui.*` directly.

import { app, on, rules } from './ipc.ts';
import type { AppSnapshot, HostState, Notice, QuestParent, RoomRow, RunRow } from './ipc-types.ts';
import { translator, type Arg } from './i18n.ts';
import { applyStatePatch } from './state.ts';
import { renderMsg } from './format.ts';

export type Tab = 'runs' | 'rooms' | 'settings';

export const ui = $state({
  ready: false,
  error: null as string | null,
  version: '',
  debug: false,
  language: 'en',
  languages: [] as AppSnapshot['languages'],
  strings: {} as Record<string, string>,
  host: null as HostState | null,
  runs: [] as RunRow[],
  rooms: [] as RoomRow[],
  tab: 'runs' as Tab,
});

/** Translates with the current language's table (reactive: re-runs on a language switch). */
export function tr(key: string, ...args: Arg[]): string {
  return translator(ui.strings)(key, ...args);
}

function adopt(s: AppSnapshot) {
  ui.version = s.version;
  ui.debug = s.debug;
  ui.language = s.language;
  ui.languages = s.languages;
  ui.strings = s.strings;
  document.documentElement.lang = s.language;
}

// ---- Message boxes ----

export interface Dialog {
  text: string;
  error: boolean;
  confirm: boolean;
  resolve: (yes: boolean) => void;
}

export const dialogs = $state({ queue: [] as Dialog[] });

function open(text: string, confirm: boolean, error: boolean): Promise<boolean> {
  return new Promise((resolve) => dialogs.queue.push({ text, confirm, error, resolve }));
}

export const alertBox = (text: string, error = false) => open(text, false, error).then(() => {});
export const confirmBox = (text: string) => open(text, true, false);

/** Shows a host notice; a confirmUrl notice opens the URL on Yes. */
export async function showNotice(n: Notice | null | undefined): Promise<void> {
  if (!n) return;
  const text = renderMsg(tr, n.message);
  if (n.confirmUrl) {
    if (await confirmBox(text)) await app.openExternal(n.confirmUrl);
  } else {
    await alertBox(text, n.error ?? false);
  }
}

/** Runs an IPC action and reports a transport failure in a box instead of an unhandled rejection. */
export async function act<T>(fn: () => Promise<T>): Promise<T | undefined> {
  try {
    return await fn();
  } catch (e) {
    await alertBox(e instanceof Error ? e.message : String(e), true);
    return undefined;
  }
}

// ---- Quest rule form (moderators) ----

export interface RuleFormData {
  parents: QuestParent[];
  detected: string | null;
  rows: RoomRow[];
  preset: RoomRow | null;
}

export const ruleForm = $state({ data: null as RuleFormData | null, loading: false });

/** ui-shell §3: fetch the quest catalog, then open the form (PRESET = a Rooms-tab row). */
export async function openRuleForm(preset: RoomRow | null = null): Promise<void> {
  if (ruleForm.loading || ruleForm.data) return;
  ruleForm.loading = true;
  try {
    const r = await act(() => rules.prepare());
    if (!r) return;
    if (!r.ok) await showNotice(r.notice);
    else ruleForm.data = { parents: r.parents, detected: r.detected, rows: r.rows, preset };
  } finally {
    ruleForm.loading = false;
  }
}

// ---- Startup ----

export async function start(): Promise<void> {
  on('state', (patch) => {
    if (ui.host) ui.host = applyStatePatch(ui.host, patch);
  });
  on('runs', (rows) => {
    ui.runs = rows;
  });
  on('rooms', (rows) => {
    ui.rooms = rows;
  });
  on('notice', (n) => void showNotice(n));
  try {
    const s = await app.hello();
    adopt(s);
    ui.host = s.state;
    ui.runs = s.runs;
    ui.rooms = s.rooms;
    // ui-shell §1: open on Settings until an API token is set.
    ui.tab = s.state.settings.apiToken.trim() === '' ? 'settings' : 'runs';
    ui.ready = true;
  } catch (e) {
    ui.error = String(e);
  }
}

export async function setLanguage(code: string): Promise<void> {
  const s = await act(() => app.setLanguage(code));
  if (s) adopt(s);
}
