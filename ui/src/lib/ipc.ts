// The UI end of the host IPC (RappyRuns.App/Host/IpcHost.cs has the other;
// keep the two in step).
//
//   UI -> host   {kind:"request", id, method, params}
//   host -> UI   {kind:"response", id, ok:true, result} | {kind:"response", id, ok:false, error}
//   host -> UI   {kind:"event", name, data}

type Response = { kind: 'response'; id: number; ok: true; result: unknown } | { kind: 'response'; id: number; ok: false; error: string };
type HostEvent = { kind: 'event'; name: string; data: unknown };

interface WebView {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (e: MessageEvent) => void): void;
}

const webview: WebView | undefined = (globalThis as { chrome?: { webview?: WebView } }).chrome?.webview;

let nextId = 1;
const pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
const listeners = new Map<string, Set<(data: unknown) => void>>();

webview?.addEventListener('message', (e) => {
  const message = e.data as Response | HostEvent;
  if (message.kind === 'response') {
    const call = pending.get(message.id);
    if (!call) return;
    pending.delete(message.id);
    if (message.ok) call.resolve(message.result);
    else call.reject(new Error(message.error));
  } else if (message.kind === 'event') {
    listeners.get(message.name)?.forEach((fn) => fn(message.data));
  }
});

/** True inside the desktop client (false when the page is opened in a plain browser). */
export const inHost = webview !== undefined;

export function call<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  if (!webview) return Promise.reject(new Error('not running inside Rappy Runs Client'));
  const id = nextId++;
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
    webview.postMessage({ kind: 'request', id, method, params });
  });
}

/** Subscribes to a host event; returns the unsubscribe function. */
export function on<T>(name: string, fn: (data: T) => void): () => void {
  let set = listeners.get(name);
  if (!set) listeners.set(name, (set = new Set()));
  const wrapped = fn as (data: unknown) => void;
  set.add(wrapped);
  return () => set.delete(wrapped);
}

// ---- Methods (P0) ----

export interface AppSnapshot {
  version: string;
  debug: boolean;
  language: string;
  languages: { code: string; label: string }[];
  strings: Record<string, string>;
}

export const app = {
  hello: () => call<AppSnapshot>('app.hello'),
  setLanguage: (language: string) => call<AppSnapshot>('app.setLanguage', { language }),
  openExternal: (url: string) => call<null>('app.openExternal', { url }),
};
