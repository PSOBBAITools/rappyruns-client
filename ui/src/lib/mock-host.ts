// A fake desktop host for developing the UI in a plain browser
// (`npm run dev`, then http://localhost:5173/?mock). It speaks the same
// messages as RappyRuns.App (docs/ipc.md) with made-up data, including a
// 4 Hz tick, so every screen and dialog can be exercised and screenshot.
//
//   ?mock          moderator with a linked token, a quest running, Pin Share on
//   ?mock=guest    fresh install: no token (opens on Settings), not a moderator
//   ?mock=notice   as ?mock, plus a host-initiated Yes/No notice after startup
//
// Never loaded inside the client (ipc.ts imports it only when usingMock).

import stringsJson from '../../../src/RappyRuns.Core/I18n/strings.json';
import extraJson from '../../../src/RappyRuns.Core/I18n/strings.extra.json';
import type { Transport } from './ipc.ts';
import type { AppSnapshot, HostState, Msg, MsgArg, Notice, RoomRow, RunRow, Settings } from './ipc-types.ts';
import { diffState } from './state.ts';

type Table = Record<string, { en: string; ja: string }>;
const table: Table = { ...(stringsJson as Table), ...(extraJson as Table) };

function stringsFor(lang: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(table)) out[k] = lang === 'ja' ? v.ja : v.en;
  return out;
}

const m = (key: string, ...args: MsgArg[]): Msg => ({ key, args });

const RELEASES = 'https://github.com/PSOBBAITools/rappyruns-client/releases';
const SERVER = 'https://rappyruns-production.up.railway.app';

function initialRuns(guest: boolean): RunRow[] {
  const run = (id: number, quest: string, timeMs: number, players: number, extra: Partial<RunRow>): RunRow => ({
    id: `run-${id}`,
    quest,
    timeMs,
    players,
    pb: false,
    video: null,
    status: [m('status-queued')],
    url: null,
    hasRecording: false,
    ...extra,
  });
  if (guest) {
    return [
      run(1, 'Mop-up Operation #1', 754_120, 1, {
        status: [m('status-draft-auto-upload')],
        video: m('video-uploading', 12),
        url: `${SERVER}/runs/5412`,
        hasRecording: true,
      }),
      run(2, 'Lost HEAT SWORD', 431_880, 1, { status: [m('status-queued-unlinked')], hasRecording: true, video: m('video-saved') }),
    ];
  }
  return [
    run(1, 'Mop-up Operation #1', 754_567, 4, {
      status: [m('status-draft-auto-upload'), m('standing-pb', '3.21s'), m('standing-rank', 2, 5), m('ghost-note', '-1.84s')],
      video: m('video-uploading', 42),
      url: `${SERVER}/runs/5412`,
      hasRecording: true,
    }),
    run(2, 'Towards the Future', 1_873_004, 1, {
      pb: true,
      status: [m('status-video-held')],
      video: m('video-uploaded'),
      url: `${SERVER}/runs/5409`,
      hasRecording: true,
    }),
    run(3, 'Phantasmal World #4', 2_401_310, 3, {
      status: [m('status-video-approved')],
      video: m('video-attached'),
      url: `${SERVER}/runs/5388`,
      hasRecording: true,
    }),
    run(4, 'Lost HEAT SWORD', 431_880, 2, { status: [m('status-queued')] }),
    run(5, 'Maximum Attack E: Forest', 1_212_450, 4, {
      status: [m('status-failed', m('hint-connect'))],
      statusError: true,
    }),
    run(6, 'Endless Nightmare #1', 3_902_117, 4, {
      status: [m('status-draft-aborted')],
      video: m('video-saved'),
      url: `${SERVER}/runs/5377`,
      hasRecording: true,
    }),
    run(7, 'Respective Tomorrow', 998_002, 1, {
      status: [m('status-rejected', 'unknown category')],
      statusError: true,
    }),
    run(8, 'Mop-up Operation #2', 812_345, 4, {
      status: [m('status-draft-upload'), m('standing-behind', '0:04.120')],
      video: m('video-upload-failed'),
      statusError: false,
      url: `${SERVER}/runs/5360`,
      hasRecording: true,
    }),
  ];
}

const mockRooms: RoomRow[] = [
  { id: 'r1', area: 'Forest 1 #1', kind: 'enemy', name: 'Booma', trigger: { type: 'monster', monster: 3 } },
  { id: 'r2', area: 'Forest 1 #1', kind: 'clear', name: null, trigger: { type: 'floor-switch', floor: 1, switch: 12 } },
  { id: 'r3', area: 'Forest 1 #2', kind: 'enemy', name: 'Rag Rappy', trigger: { type: 'monster', monster: 11 } },
  { id: 'r4', area: 'Forest 1 #2', kind: 'enemy', name: 'Mothmant', trigger: { type: 'monster', monster: 14 } },
  { id: 'r5', area: 'Forest 1 #2', kind: 'clear', name: null, trigger: { type: 'monster', monster: 19 } },
  { id: 'r6', area: 'Forest 2 #1', kind: 'enemy', name: 'Hildebear', trigger: { type: 'monster', monster: 42 } },
  { id: 'r7', area: 'Forest 2 #1', kind: 'clear', name: null, trigger: { type: 'floor-switch', floor: 2, switch: 5 } },
];

export function createMockHost(variant: string): Transport {
  const guest = variant === 'guest';
  const startupNotice = variant === 'notice';
  let listener: (message: unknown) => void = () => {};
  const emit = (name: string, data: unknown) => listener({ kind: 'event', name, data });

  let language = navigator.language.startsWith('ja') ? 'ja' : 'en';
  const settings: Settings = {
    language,
    serverUrl: SERVER,
    apiToken: guest ? '' : 'rr_5f0c1e9a7d2b4c6e8a1f3d5b7c9e0a2d',
    trackingOnly: false,
    trackingPrivate: false,
    recordAudio: true,
    recordMaxTotalGb: 20,
    autoPublish: false,
    recordDir: 'C:\\Users\\teapot\\Videos\\RappyRuns',
    ghostRace: true,
    ghostOverlay: !guest,
    ghostMarker: true,
    overlayCorner: 'top-right',
    pinshareEnabled: !guest,
    pinshareChannel: guest ? '' : 'rappy-party-42',
    autoUpdate: true,
    closeToTray: true,
    autostart: false,
    startMinimized: false,
    rankToast: true,
    triggerLog: false,
  };

  let questStart = Date.now() - 263_000;
  let state: HostState = {
    moderator: !guest,
    pinshareAllowed: !guest,
    game: { msg: m(guest ? 'game-searching' : 'game-attached'), tone: guest ? 'busy' : 'ok' },
    server: { msg: m('server-ok', 128, 342, guest ? null : 2), tone: 'ok' },
    token: guest ? { msg: m('token-unlinked') } : { msg: m('token-ok', 'teapot'), tone: 'ok' },
    quest: { kind: 'none' },
    pinshare: guest ? { msg: m('pinshare-status-off') } : { msg: m('pinshare-status-connected', 'rappy-party-42', 3), tone: 'ok' },
    pinSet: { msg: m('pinshare-pin-set-active', 'Fast route', 'teapot') },
    version: { msg: m('version-status', '0.61.0', m('update-up-to-date')) },
    pairing: false,
    updating: false,
    settings: { ...settings },
  };
  let sent: HostState = structuredClone(state);
  let runs = initialRuns(guest);
  let upload = 42;

  const questNow = (): HostState['quest'] => {
    if (guest) return { kind: 'none' };
    const ms = Date.now() - questStart;
    if (ms > 15 * 60_000) questStart = Date.now();
    const sec = Math.floor(ms / 1000);
    const gap = Math.sin(sec / 20) * 4;
    return {
      kind: 'active',
      slug: 'mop-up-1',
      others: 0,
      elapsed: `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, '0')}`,
      recording: true,
      ghost: { target: '12:31.402', gap: sec < 20 ? null : `${gap < 0 ? '-' : '+'}${Math.abs(gap).toFixed(1)}s` },
    };
  };

  const push = () => {
    const patch = diffState(sent, state);
    if (patch) {
      sent = structuredClone(state);
      emit('state', patch);
    }
  };
  const set = (patch: Partial<HostState>) => {
    state = { ...state, ...patch };
    push();
  };
  const setSettings = (patch: Partial<Settings>) => set({ settings: { ...state.settings, ...patch } });
  const updateRun = (id: string, patch: Partial<RunRow>) => {
    runs = runs.map((r) => (r.id === id ? { ...r, ...patch } : r));
    emit('runs', runs);
  };

  // The 4 Hz GUI tick.
  setInterval(() => {
    set({ quest: questNow() });
  }, 250);
  // Upload progress: one event per whole percent, as the host does.
  setInterval(() => {
    const row = runs.find((r) => r.id === 'run-1');
    if (!row || upload >= 100) return;
    upload = Math.min(100, upload + 1);
    updateRun('run-1', upload < 100 ? { video: m('video-uploading', upload) } : { video: m('video-uploaded'), status: [m('status-video-held')] });
  }, 1500);

  const snapshot = (): AppSnapshot => ({
    version: '0.61.0',
    debug: !guest,
    language,
    languages: [
      { code: 'en', label: 'English' },
      { code: 'ja', label: '日本語' },
    ],
    strings: stringsFor(language),
    state: structuredClone(state),
    runs,
    rooms: guest ? [] : mockRooms,
  });

  const later = (ms: number, fn: () => void) => setTimeout(fn, ms);
  const notice = (message: Msg, extra: Partial<Notice> = {}): Notice => ({ message, ...extra });

  type P = Record<string, unknown>;
  const handlers: Record<string, (p: P) => unknown | Promise<unknown>> = {
    'app.hello': () => {
      if (startupNotice) later(1500, () => emit('notice', notice(m('update-not-writable-confirm'), { confirmUrl: RELEASES })));
      return snapshot();
    },
    'app.setLanguage': (p) => {
      language = String(p.language);
      setSettings({ language });
      return snapshot();
    },
    'app.openExternal': (p) => {
      console.info('[mock] open', p.url);
      return null;
    },

    'settings.set': (p) => {
      setSettings({ [String(p.key)]: p.value } as Partial<Settings>);
      return state.settings;
    },
    'settings.saveConnection': async (p) => {
      const token = String(p.apiToken ?? '').trim();
      setSettings({ apiToken: token, ...(p.serverUrl != null ? { serverUrl: String(p.serverUrl).replace(/[/ ]+$/, '') } : {}) });
      if (!token) {
        set({ token: { msg: m('token-unlinked') }, moderator: false, pinshareAllowed: false });
        return null;
      }
      set({ token: { msg: m('token-checking'), tone: 'busy' } });
      await new Promise((r) => setTimeout(r, 700));
      if (token === 'bad') {
        set({ token: { msg: m('token-invalid'), tone: 'error' }, pinshareAllowed: false });
        return notice(m('token-rejected-dialog'), { error: true });
      }
      set({ token: { msg: m('token-ok', 'teapot'), tone: 'ok' }, moderator: true, pinshareAllowed: true });
      return notice(m('token-ok-dialog', 'teapot'));
    },
    'settings.setAutoPublish': async (p) => {
      await new Promise((r) => setTimeout(r, 400));
      setSettings({ autoPublish: Boolean(p.enabled) });
      return { enabled: state.settings.autoPublish, notice: null };
    },
    'settings.setAutostart': (p) => {
      setSettings({ autostart: Boolean(p.enabled) });
      return { enabled: state.settings.autostart };
    },
    'settings.chooseRecordDir': () => {
      const recordDir = 'D:\\Captures\\RappyRuns';
      setSettings({ recordDir });
      return { recordDir };
    },
    'settings.setTriggerLog': (p) => {
      setSettings({ triggerLog: Boolean(p.enabled) });
      return p.enabled ? notice(m('trigger-log-on', 'C:\\Users\\teapot\\AppData\\Roaming\\ephinea-ta-client\\trigger-log.txt')) : null;
    },

    'account.link': () => {
      if (state.pairing) return null;
      set({ pairing: true, token: { msg: m('pairing-waiting'), tone: 'busy' } });
      later(4000, () => {
        setSettings({ apiToken: 'rr_paired_0a1b2c3d4e5f60718293a4b5c6d7e8f9' });
        set({ pairing: false, token: { msg: m('token-checking'), tone: 'busy' } });
        later(600, () => set({ token: { msg: m('token-ok', 'teapot'), tone: 'ok' }, pinshareAllowed: true }));
      });
      return null;
    },

    'runs.open': (p) => {
      console.info('[mock] open run', p.id);
      return null;
    },
    'runs.uploadVideo': (p) => {
      const row = p.id ? runs.find((r) => r.id === p.id) : runs.find((r) => r.hasRecording);
      if (!row) return notice(m('no-recordings-yet'));
      if (!row.hasRecording) return notice(m('no-recording-for-run'));
      console.info('[mock] explorer /select + youtube upload for', row.id);
      return null;
    },
    'runs.openRecordingsFolder': () => null,
    'runs.openMyRuns': () => null,
    'runs.retry': () => {
      later(800, () => {
        runs = runs.map((r) =>
          r.status[0] && 'key' in r.status[0] && (r.status[0].key === 'status-queued' || r.status[0].key === 'status-failed')
            ? { ...r, status: [m('status-draft-add'), m('standing-first')], statusError: false, url: `${SERVER}/runs/5413` }
            : r,
        );
        emit('runs', runs);
      });
      return null;
    },
    'runs.clear': () => {
      runs = runs.filter((r) => r.status[0] && 'key' in r.status[0] && ['status-queued', 'status-queued-unlinked', 'status-failed'].includes(r.status[0].key));
      emit('runs', runs);
      return null;
    },

    'rules.prepare': async () => {
      await new Promise((r) => setTimeout(r, 300));
      return {
        ok: true,
        parents: [
          { slug: 'mop-up-1', name: 'Mop-up Operation #1' },
          { slug: 'mop-up-2', name: 'Mop-up Operation #2' },
          { slug: 'towards-the-future', name: 'Towards the Future' },
          { slug: 'lost-heat-sword', name: 'Lost HEAT SWORD' },
        ],
        detected: 'mop-up-1',
        rows: mockRooms,
      };
    },
    'rules.create': (p) => notice(m('rule-created', `${p.parent}-${String(p.name).toLowerCase().replace(/\s+/g, '-')}`)),

    'pinshare.setEnabled': (p) => {
      setSettings({ pinshareEnabled: Boolean(p.enabled), pinshareChannel: String(p.channel ?? '') });
      if (!p.enabled) set({ pinshare: { msg: m('pinshare-status-off') } });
      else {
        set({ pinshare: { msg: m('pinshare-status-connecting'), tone: 'busy' } });
        later(900, () => set({ pinshare: { msg: m('pinshare-status-connected', state.settings.pinshareChannel, 3), tone: 'ok' } }));
      }
      return null;
    },
    'pinshare.applyChannel': (p) => {
      const channel = String(p.channel ?? '');
      setSettings({ pinshareChannel: channel });
      if (state.settings.pinshareEnabled) {
        set({ pinshare: channel ? { msg: m('pinshare-status-connected', channel, 1), tone: 'ok' } : { msg: m('pinshare-status-no-channel') } });
      }
      return null;
    },
    'pinshare.saveNew': () => notice(m('pinshare-save-created', `${SERVER}/pin-sets/77/edit`)),
    'pinshare.overwriteCheck': () => ({ ok: true, name: 'Fast route', pins: 6, arrows: 2 }),
    'pinshare.overwrite': () => notice(m('pinshare-save-updated', 6, 2)),

    'updates.check': async () => {
      set({ updating: true, version: { msg: m('version-status', '0.61.0', m('update-checking')) } });
      await new Promise((r) => setTimeout(r, 900));
      set({ updating: false, version: { msg: m('version-status', '0.61.0', m('update-up-to-date')) } });
      return notice(m('update-latest-dialog', '0.61.0'));
    },
  };

  return {
    listen: (fn) => {
      listener = fn;
    },
    post: (raw) => {
      const req = raw as { id: number; method: string; params: P };
      const handler = handlers[req.method];
      // Answer asynchronously like the real host.
      setTimeout(async () => {
        if (!handler) {
          listener({ kind: 'response', id: req.id, ok: false, error: `unknown method ${req.method}` });
          return;
        }
        try {
          const result = await handler(req.params ?? {});
          listener({ kind: 'response', id: req.id, ok: true, result: result ?? null });
        } catch (e) {
          listener({ kind: 'response', id: req.id, ok: false, error: String(e) });
        }
      }, 30);
    },
  };
}
