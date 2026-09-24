import { test } from 'node:test';
import assert from 'node:assert/strict';
import { endChoices, parseIntInRange, resolveManualTrigger, validateRuleForm, type RuleFormInput } from './rule-form.ts';
import type { RoomRow } from './ipc-types.ts';

const rows: RoomRow[] = [
  { id: 'a', area: 'Forest 1 #1', kind: 'enemy', name: 'Booma', trigger: { type: 'monster', monster: 3 } },
  { id: 'b', area: 'Forest 1 #1', kind: 'clear', name: null, trigger: { type: 'floor-switch', floor: 1, switch: 12 } },
  { id: 'c', area: 'Forest 1 #2', kind: 'clear', name: null, trigger: null },
];

test('endChoices: run rows with a trigger, then the three manual kinds', () => {
  const c = endChoices(rows);
  assert.deepEqual(
    c.map((x) => (x.kind === 'row' ? x.row.id : x.manual)),
    ['a', 'b', 'monster', 'floor-switch', 'register'],
  );
});

test('endChoices: a preset row goes first and is not repeated', () => {
  const c = endChoices(rows, rows[1]);
  assert.deepEqual(
    c.map((x) => (x.kind === 'row' ? x.row.id : x.manual)),
    ['b', 'a', 'monster', 'floor-switch', 'register'],
  );
});

test('parseIntInRange mirrors parse-integer + range', () => {
  assert.equal(parseIntInRange(' 42 ', 0, 255), 42);
  assert.equal(parseIntInRange('+7', 0, 255), 7);
  assert.equal(parseIntInRange('256', 0, 255), null);
  assert.equal(parseIntInRange('-1', 0, 255), null);
  assert.equal(parseIntInRange('', 0, 255), null);
  assert.equal(parseIntInRange('4a', 0, 255), null);
  assert.equal(parseIntInRange('1.5', 0, 255), null);
});

test('resolveManualTrigger ranges', () => {
  assert.deepEqual(resolveManualTrigger('monster', '65535', ''), { type: 'monster', monster: 65535 });
  assert.equal(resolveManualTrigger('monster', '65536', ''), null);
  assert.deepEqual(resolveManualTrigger('floor-switch', '17', '255'), { type: 'floor-switch', floor: 17, switch: 255 });
  assert.equal(resolveManualTrigger('floor-switch', '18', '1'), null);
  assert.equal(resolveManualTrigger('floor-switch', '3', ''), null);
  assert.deepEqual(resolveManualTrigger('register', '0', 'ignored'), { type: 'register', register: 0 });
});

const form = (over: Partial<RuleFormInput> = {}): RuleFormInput => ({
  parent: 'mop-up-1',
  name: ' Room 3 ',
  description: 'Clear the third room',
  end: endChoices(rows)[0],
  val1: '',
  val2: '',
  startWarpIn: false,
  ...over,
});

test('validateRuleForm checks in qrd-ok order', () => {
  assert.deepEqual(validateRuleForm(form({ parent: null, name: '' })), { error: 'rule-need-quest' });
  assert.deepEqual(validateRuleForm(form({ name: '   ', description: '' })), { error: 'rule-need-name' });
  assert.deepEqual(validateRuleForm(form({ description: ' ' })), { error: 'rule-need-desc' });
  assert.deepEqual(validateRuleForm(form({ end: null })), { error: 'rule-need-end' });
  assert.deepEqual(validateRuleForm(form({ end: { kind: 'manual', manual: 'register' }, val1: 'x' })), { error: 'rule-need-values' });
});

test('validateRuleForm builds the request', () => {
  assert.deepEqual(validateRuleForm(form()), {
    rule: { parent: 'mop-up-1', name: 'Room 3', description: 'Clear the third room', end: { type: 'monster', monster: 3 }, start: null },
  });
  const manual = validateRuleForm(form({ end: { kind: 'manual', manual: 'floor-switch' }, val1: '2', val2: '5', startWarpIn: true }));
  assert.deepEqual(manual, {
    rule: {
      parent: 'mop-up-1',
      name: 'Room 3',
      description: 'Clear the third room',
      end: { type: 'floor-switch', floor: 2, switch: 5 },
      start: { type: 'warp-in' },
    },
  });
});
