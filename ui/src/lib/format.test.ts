import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { translator } from './i18n.ts';
import { formatRunTime, ghostAhead, parentLabel, partyLabel, questStatusText, renderMsg, roomLabel, runStatusText, triggerLabel } from './format.ts';

const root = new URL('../../../', import.meta.url);
const load = (p: string) => JSON.parse(readFileSync(new URL(p, root), 'utf8')) as Record<string, { en: string; ja: string }>;
const table = { ...load('src/RappyRuns.Core/I18n/strings.json'), ...load('src/RappyRuns.Core/I18n/strings.extra.json') };
const tables = (lang: 'en' | 'ja') => Object.fromEntries(Object.entries(table).map(([k, v]) => [k, v[lang]]));
const en = translator(tables('en'));
const ja = translator(tables('ja'));

test('formatRunTime matches format-run-time', () => {
  assert.equal(formatRunTime(0), '0:00.000');
  assert.equal(formatRunTime(754_567), '12:34.567');
  assert.equal(formatRunTime(59_999), '0:59.999');
  assert.equal(formatRunTime(3_900_000), '65:00.000');
  assert.equal(formatRunTime(61_005), '1:01.005');
});

test('partyLabel', () => {
  assert.equal(partyLabel({ players: 4, pb: false }), '4P');
  assert.equal(partyLabel({ players: 1, pb: true }), '1P/PB');
});

test('renderMsg renders keys, nested messages and raw text', () => {
  assert.equal(renderMsg(en, { text: 'raw' }), 'raw');
  assert.equal(renderMsg(en, { key: 'token-ok', args: ['teapot'] }), 'Token: OK (teapot)');
  assert.equal(renderMsg(en, { key: 'status-failed', args: [{ key: 'hint-timeout' }] }), 'failed: connection timed out');
  assert.equal(renderMsg(en, { key: 'version-status', args: ['0.61.0', null] }), 'Version: 0.61.0');
  assert.equal(renderMsg(en, { key: 'version-status', args: ['0.61.0', { key: 'update-up-to-date' }] }), 'Version: 0.61.0 - up to date');
  assert.equal(renderMsg(en, { key: 'server-ok', args: [128, 1, null] }), 'Server: OK (128 quests, 1 timed category)');
  assert.equal(renderMsg(ja, { key: 'tab-runs' }), '記録');
});

test('runStatusText joins label and notes with a middle dot', () => {
  const row = { status: [{ key: 'status-draft-add' }, { key: 'standing-pb', args: ['1.20s'] }, { key: 'standing-rank', args: [2, 5] }] };
  assert.equal(runStatusText(en, row), 'draft - double-click to add video · PB! -1.20s · prov. #2 of 5');
  assert.equal(runStatusText(en, { status: [] }), '');
});

test('questStatusText follows the Lisp line', () => {
  assert.equal(questStatusText(en, { kind: 'none' }), 'No active quest');
  assert.equal(questStatusText(en, { kind: 'waiting', name: 'Mop-up Operation #1' }), 'Mop-up Operation #1 (waiting for start)');
  const active = { kind: 'active' as const, slug: 'mop-up-1', others: 0, elapsed: '4:05', recording: false, ghost: null };
  assert.equal(questStatusText(en, active), 'mop-up-1 - 4:05');
  assert.equal(
    questStatusText(en, { ...active, others: 2, recording: true, ghost: { target: '12:31.402', gap: '-3.2s' } }),
    'mop-up-1 (+2) - 4:05 [REC] | vs 12:31.402 -3.2s',
  );
  assert.equal(questStatusText(en, { ...active, ghost: { target: '12:31.402', gap: null } }), 'mop-up-1 - 4:05 | vs 12:31.402');
});

test('ghostAhead', () => {
  assert.equal(ghostAhead('-3.2s'), true);
  assert.equal(ghostAhead('+0:04.1'), false);
  assert.equal(ghostAhead(null), null);
  assert.equal(ghostAhead('0.0s'), null);
});

test('triggerLabel matches rule-trigger-label', () => {
  assert.equal(triggerLabel({ type: 'warp-in' }), 'warp-in');
  assert.equal(triggerLabel({ type: 'register', register: 7 }), 'register:7');
  assert.equal(triggerLabel({ type: 'floor-switch', floor: 3, switch: 12 }), 'floor-switch:3:12');
  assert.equal(triggerLabel({ type: 'monster', monster: 42 }), 'monster:42');
});

test('room and parent labels', () => {
  const clear = { id: 'a', area: 'Forest 1 #2', kind: 'clear' as const, name: null, trigger: null };
  const enemy = { id: 'b', area: 'Forest 1 #2', kind: 'enemy' as const, name: 'Booma', trigger: null };
  assert.equal(roomLabel(en, clear), 'Forest 1 #2 - clear');
  assert.equal(roomLabel(ja, clear), 'Forest 1 #2 - クリア');
  assert.equal(roomLabel(en, enemy), 'Forest 1 #2 - Booma');
  assert.equal(parentLabel({ slug: 'mop-up-1', name: 'Mop-up Operation #1' }), 'Mop-up Operation #1  (mop-up-1)');
});

test('strings.extra.json: both languages, no clash with the Lisp-generated table', () => {
  const base = load('src/RappyRuns.Core/I18n/strings.json');
  for (const [key, v] of Object.entries(load('src/RappyRuns.Core/I18n/strings.extra.json'))) {
    assert.ok(!(key in base), `${key} already in strings.json`);
    assert.ok(v.en.length > 0 && v.ja.length > 0, key);
  }
});

test('every literal tr() key used by the UI exists', () => {
  const files = [
    'App.svelte',
    'components/RunsTab.svelte',
    'components/RoomsTab.svelte',
    'components/SettingsTab.svelte',
    'components/MessageDialog.svelte',
    'components/RuleDialog.svelte',
    'lib/rule-form.ts',
    'lib/format.ts',
  ];
  const src = new URL('../', import.meta.url);
  for (const f of files) {
    const text = readFileSync(new URL(f, src), 'utf8');
    for (const m of text.matchAll(/(?:tr\(|key: |error: )'([a-z0-9-]+)'/g)) {
      assert.ok(m[1] in table, `${f}: unknown key ${m[1]}`);
    }
    for (const m of text.matchAll(/:\s*'(rule-[a-z-]+|group-[a-z]+)'/g)) {
      assert.ok(m[1] in table, `${f}: unknown key ${m[1]}`);
    }
  }
  for (const c of ['top-right', 'top-center', 'top-left', 'middle-right', 'middle-left', 'bottom-right', 'bottom-center', 'bottom-left', 'custom']) {
    assert.ok(`corner-${c}` in table);
  }
});
