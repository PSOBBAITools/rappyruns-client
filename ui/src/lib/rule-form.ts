// The quest-rule form's pure logic (rule-form.lisp / qrd-ok in gui.lisp):
// the end choices, manual trigger resolution and the validation order.

import type { RoomRow, RuleCreateParams, Trigger } from './ipc-types.ts';

export type ManualKind = 'monster' | 'floor-switch' | 'register';

/** One entry of the "Clears when" dropdown: a room/enemy of the run, or a manual kind. */
export type EndChoice = { kind: 'row'; row: RoomRow; trigger: Trigger } | { kind: 'manual'; manual: ManualKind };

export const manualKinds: readonly ManualKind[] = ['monster', 'floor-switch', 'register'];

export const manualLabelKey: Record<ManualKind, string> = {
  monster: 'rule-end-monster',
  'floor-switch': 'rule-end-floor-switch',
  register: 'rule-end-register',
};

/**
 * rule-end-items: the preset row first (a Rooms-tab double-click), then this
 * run's rows that carry a trigger, then the three manual entries.
 */
export function endChoices(rows: readonly RoomRow[], preset: RoomRow | null = null): EndChoice[] {
  const out: EndChoice[] = [];
  const add = (row: RoomRow) => {
    if (row.trigger) out.push({ kind: 'row', row, trigger: row.trigger });
  };
  if (preset) add(preset);
  for (const row of rows) if (row !== preset) add(row);
  for (const manual of manualKinds) out.push({ kind: 'manual', manual });
  return out;
}

/** parse-int-in-range: an optionally signed integer, surrounding spaces allowed, inside [min, max]. */
export function parseIntInRange(s: string, min: number, max: number): number | null {
  const t = s.trim();
  if (!/^[+-]?\d+$/.test(t)) return null;
  const n = Number(t);
  return n >= min && n <= max ? n : null;
}

/** resolve-manual-trigger: monster 0..65535, floor 0..17 + switch 0..255, register 0..255. */
export function resolveManualTrigger(kind: ManualKind, val1: string, val2: string): Trigger | null {
  switch (kind) {
    case 'monster': {
      const id = parseIntInRange(val1, 0, 65535);
      return id === null ? null : { type: 'monster', monster: id };
    }
    case 'floor-switch': {
      const floor = parseIntInRange(val1, 0, 17);
      const sw = parseIntInRange(val2, 0, 255);
      return floor === null || sw === null ? null : { type: 'floor-switch', floor, switch: sw };
    }
    case 'register': {
      const n = parseIntInRange(val1, 0, 255);
      return n === null ? null : { type: 'register', register: n };
    }
  }
}

export interface RuleFormInput {
  parent: string | null;
  name: string;
  description: string;
  end: EndChoice | null;
  val1: string;
  val2: string;
  startWarpIn: boolean;
}

/**
 * qrd-ok: the first failing check as an i18n key (the dialog stays open), or
 * the request to send. Name and description are trimmed of spaces.
 */
export function validateRuleForm(f: RuleFormInput): { error: string } | { rule: RuleCreateParams } {
  const name = f.name.trim();
  const description = f.description.trim();
  if (!f.parent) return { error: 'rule-need-quest' };
  if (name === '') return { error: 'rule-need-name' };
  if (description === '') return { error: 'rule-need-desc' };
  if (!f.end) return { error: 'rule-need-end' };
  const end = f.end.kind === 'row' ? f.end.trigger : resolveManualTrigger(f.end.manual, f.val1, f.val2);
  if (!end) return { error: 'rule-need-values' };
  return {
    rule: { parent: f.parent, name, description, end, start: f.startWarpIn ? { type: 'warp-in' } : null },
  };
}
