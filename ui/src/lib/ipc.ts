// The UI end of the host IPC (RappyRuns.App/Host/IpcHost.cs has the other;
// keep the two in step). The method and event catalog is desktop/docs/ipc.md;
// the data shapes are in ipc-types.ts.
//
//   UI -> host   {kind:"request", id, method, params}
//   host -> UI   {kind:"response", id, ok:true, result} | {kind:"response", id, ok:false, error}
//   host -> UI   {kind:"event", name, data}
//
// Outside the desktop client (a plain browser during `npm run dev`), or with
// ?mock in a dev build, the same messages go to an in-page fake host
// (mock-host.ts) so the screens can be built and screenshot without the exe.

import type {
  AppSnapshot,
  HostEvents,
  Notice,
  OverwriteCheckResult,
  RuleCreateParams,
  RulePrepareResult,
  Settings,
  SimpleSettingKey,
} from './ipc-types.ts';

export type * from './ipc-types.ts';

type Response = { kind: 'response'; id: number; ok: true; result: unknown } | { kind: 'response'; id: number; ok: false; error: string };
type HostEvent = { kind: 'event'; name: string; data: unknown };

/** Where requests go: WebView2, or the dev mock. */
export interface Transport {
  post(message: unknown): void;
  listen(fn: (message: unknown) => void): void;
}

interface WebView {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (e: MessageEvent) => void): void;
}

const webview: WebView | undefined = (globalThis as { chrome?: { webview?: WebView } }).chrome?.webview;

/** True inside the desktop client (false when the page is opened in a plain browser). */
export const inHost = webview !== undefined;

/** The fake host is used outside the client, or in a dev build opened with ?mock. */
export const usingMock = !inHost || (import.meta.env.DEV && new URLSearchParams(location.search).has('mock'));

let nextId = 1;
const pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
const listeners = new Map<string, Set<(data: unknown) => void>>();

function dispatch(raw: unknown) {
  const message = raw as Response | HostEvent;
  if (message.kind === 'response') {
    const request = pending.get(message.id);
    if (!request) return;
    pending.delete(message.id);
    if (message.ok) request.resolve(message.result);
    else request.reject(new Error(message.error));
  } else if (message.kind === 'event') {
    listeners.get(message.name)?.forEach((fn) => fn(message.data));
  }
}

const transport: Promise<Transport> = (async () => {
  let t: Transport;
  if (usingMock) {
    const { createMockHost } = await import('./mock-host.ts');
    t = createMockHost(new URLSearchParams(location.search).get('mock') ?? '');
  } else {
    const view = webview!;
    t = {
      post: (m) => view.postMessage(m),
      listen: (fn) => view.addEventListener('message', (e) => fn(e.data)),
    };
  }
  t.listen(dispatch);
  return t;
})();

export async function call<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  const t = await transport;
  const id = nextId++;
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
    t.post({ kind: 'request', id, method, params });
  });
}

/** Subscribes to a host event; returns the unsubscribe function. */
export function on<K extends keyof HostEvents>(name: K, fn: (data: HostEvents[K]) => void): () => void {
  let set = listeners.get(name);
  if (!set) listeners.set(name, (set = new Set()));
  const wrapped = fn as (data: unknown) => void;
  set.add(wrapped);
  return () => set.delete(wrapped);
}

// ---- Typed wrappers, one object per host area (see ipc.md) ----

export const app = {
  /** Handshake: strings for the saved language plus the full state, runs and rooms. */
  hello: () => call<AppSnapshot>('app.hello'),
  /** Saves the language and returns the snapshot with the new strings. */
  setLanguage: (language: string) => call<AppSnapshot>('app.setLanguage', { language }),
  /** Opens an http(s) URL in the default browser (other schemes are ignored). */
  openExternal: (url: string) => call<null>('app.openExternal', { url }),
};

export const settings = {
  set: <K extends SimpleSettingKey>(key: K, value: Settings[K]) => call<Settings>('settings.set', { key, value }),
  /** Save & verify: trims and saves the URL (debug only) and token, re-checks server and token. */
  saveConnection: (apiToken: string, serverUrl: string | null) =>
    call<Notice | null>('settings.saveConnection', { apiToken, serverUrl }),
  /** Posts to the server; resolves with the value now in effect (rolled back on failure, with a notice). */
  setAutoPublish: (enabled: boolean) => call<{ enabled: boolean; notice: Notice | null }>('settings.setAutoPublish', { enabled }),
  /** Writes the Run key, then reads the registry back: the result is the real state. */
  setAutostart: (enabled: boolean) => call<{ enabled: boolean }>('settings.setAutostart', { enabled }),
  /** Native folder picker; null when cancelled. */
  chooseRecordDir: () => call<{ recordDir: string } | null>('settings.chooseRecordDir'),
  /** Toggles trigger-log.txt; turning it on returns the :trigger-log-on notice. */
  setTriggerLog: (enabled: boolean) => call<Notice | null>('settings.setTriggerLog', { enabled }),
};

export const account = {
  /** Starts browser pairing (no-op while one is running). Progress shows on state.token. */
  link: () => call<null>('account.link'),
};

export const runs = {
  open: (id: string) => call<null>('runs.open', { id }),
  /** Explorer on the recording + youtube.com/upload. id null = newest run a video can still be attached to. */
  uploadVideo: (id: string | null) => call<Notice | null>('runs.uploadVideo', { id }),
  openRecordingsFolder: () => call<null>('runs.openRecordingsFolder'),
  openMyRuns: () => call<null>('runs.openMyRuns'),
  retry: () => call<null>('runs.retry'),
  /** Forgets everything but unsent runs. The UI confirms first. */
  clear: () => call<null>('runs.clear'),
};

export const rules = {
  prepare: () => call<RulePrepareResult>('rules.prepare'),
  create: (rule: RuleCreateParams) => call<Notice>('rules.create', { ...rule }),
};

export const pinshare = {
  setEnabled: (enabled: boolean, channel: string) => call<null>('pinshare.setEnabled', { enabled, channel }),
  applyChannel: (channel: string) => call<null>('pinshare.applyChannel', { channel }),
  saveNew: () => call<Notice>('pinshare.saveNew'),
  overwriteCheck: () => call<OverwriteCheckResult>('pinshare.overwriteCheck'),
  overwrite: () => call<Notice>('pinshare.overwrite'),
};

export const updates = {
  /** Manual check. Every outcome but "downloading" answers with a notice. */
  check: () => call<Notice | null>('updates.check'),
};
