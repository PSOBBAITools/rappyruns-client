import { test } from 'node:test';
import assert from 'node:assert/strict';
import { applyStatePatch, diffState } from './state.ts';
import type { HostState } from './ipc-types.ts';

const base: HostState = {
  moderator: false,
  pinshareAllowed: false,
  game: { msg: { key: 'game-searching' }, tone: 'busy' },
  server: { msg: { key: 'server-not-checked' } },
  token: { msg: { key: 'token-not-checked' } },
  quest: { kind: 'none' },
  pinshare: { msg: { key: 'pinshare-status-off' } },
  pinSet: { msg: { key: 'pinshare-pin-set-none-idle' } },
  version: { msg: { key: 'version-status', args: ['0.61.0', null] } },
  pairing: false,
  updating: false,
  settings: {
    language: 'en',
    serverUrl: 'https://example.invalid',
    apiToken: '',
    trackingOnly: false,
    trackingPrivate: false,
    recordAudio: true,
    recordMaxTotalGb: 20,
    autoPublish: false,
    recordDir: 'C:\\Videos\\RappyRuns',
    ghostRace: true,
    ghostOverlay: false,
    ghostMarker: true,
    overlayCorner: 'top-right',
    pinshareEnabled: false,
    pinshareChannel: '',
    autoUpdate: true,
    closeToTray: true,
    autostart: false,
    startMinimized: false,
    rankToast: true,
    triggerLog: false,
  },
};

test('first diff sends everything', () => {
  assert.deepEqual(diffState(null, base), base);
});

test('an unchanged tick sends nothing', () => {
  assert.equal(diffState(base, structuredClone(base)), null);
});

test('only changed top-level keys are sent, whole', () => {
  const next: HostState = {
    ...structuredClone(base),
    game: { msg: { key: 'game-attached' }, tone: 'ok' },
    settings: { ...base.settings, recordAudio: false },
  };
  const patch = diffState(base, next);
  assert.deepEqual(Object.keys(patch ?? {}).sort(), ['game', 'settings']);
  assert.deepEqual(patch?.settings, next.settings);
});

test('applyStatePatch replaces keys and leaves the rest', () => {
  const quest = { kind: 'waiting' as const, name: 'Towards the Future' };
  const merged = applyStatePatch(base, { quest, moderator: true });
  assert.deepEqual(merged.quest, quest);
  assert.equal(merged.moderator, true);
  assert.equal(merged.token, base.token);
  assert.deepEqual(base.quest, { kind: 'none' }, 'input not mutated');
});

test('diff then apply round-trips', () => {
  const next: HostState = { ...structuredClone(base), pairing: true, token: { msg: { key: 'pairing-waiting' }, tone: 'busy' } };
  assert.deepEqual(applyStatePatch(base, diffState(base, next) ?? {}), next);
});
