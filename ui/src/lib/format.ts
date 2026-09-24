// Pure display helpers of the UI (no DOM, no IPC) - unit-tested in
// format.test.ts. The run-label logic (run-status-label and friends) stays in
// the host (RappyRuns.Core, golden-tested); these only turn the host's data
// into text.

import type { Arg } from './i18n.ts';
import type { Msg, MsgArg, OverlayCorner, QuestParent, QuestStatus, RoomRow, RunRow, Trigger } from './ipc-types.ts';

export type Tr = (key: string, ...args: Arg[]) => string;

export const overlayCorners: readonly OverlayCorner[] = [
  'top-right',
  'top-center',
  'top-left',
  'middle-right',
  'middle-left',
  'bottom-right',
  'bottom-center',
  'bottom-left',
  'custom',
];

/** Renders a host message in the current language; nested messages first. */
export function renderMsg(tr: Tr, msg: Msg): string {
  if ('text' in msg) return msg.text;
  return tr(msg.key, ...(msg.args ?? []).map((a) => renderArg(tr, a)));
}

function renderArg(tr: Tr, a: MsgArg): Arg {
  return a !== null && typeof a === 'object' ? renderMsg(tr, a) : a;
}

/** m:ss.mmm - format-run-time (store.lisp): past an hour it keeps counting minutes ("65:00.000"). */
export function formatRunTime(ms: number): string {
  const totalSeconds = Math.floor(ms / 1000);
  const msec = ms - totalSeconds * 1000;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds - minutes * 60;
  return `${minutes}:${String(seconds).padStart(2, '0')}.${String(msec).padStart(3, '0')}`;
}

/** The party column: "<n>P", "/PB" appended for PB categories. */
export function partyLabel(row: Pick<RunRow, 'players' | 'pb'>): string {
  return `${row.players}P${row.pb ? '/PB' : ''}`;
}

/** The status column: the label and the entry notes joined with " · ". */
export function runStatusText(tr: Tr, row: Pick<RunRow, 'status'>): string {
  return row.status.map((m) => renderMsg(tr, m)).join(' · ');
}

/**
 * The quest line, exactly as the Lisp client wrote it:
 * "<slug>[ (+N)] - <elapsed>[ [REC]][ | vs <target>[ <gap>]]".
 */
export function questStatusText(tr: Tr, q: QuestStatus): string {
  switch (q.kind) {
    case 'none':
      return tr('no-active-quest');
    case 'waiting':
      return tr('quest-waiting', q.name);
    case 'active': {
      let s = q.slug;
      if (q.others > 0) s += ` (+${q.others})`;
      s += ` - ${q.elapsed}`;
      if (q.recording) s += ' [REC]';
      if (q.ghost) s += ` | vs ${q.ghost.target}${q.ghost.gap ? ` ${q.ghost.gap}` : ''}`;
      return s;
    }
  }
}

/** Ahead of the ghost when the gap is negative ("-3.2s"), behind when positive; null when unknown. */
export function ghostAhead(gap: string | null): boolean | null {
  if (!gap) return null;
  if (gap.startsWith('-')) return true;
  if (gap.startsWith('+')) return false;
  return null;
}

/** rule-trigger-label (rule-form.lisp): warp-in / register:N / floor-switch:F:S / monster:ID. */
export function triggerLabel(t: Trigger): string {
  switch (t.type) {
    case 'warp-in':
      return 'warp-in';
    case 'register':
      return `register:${t.register}`;
    case 'floor-switch':
      return `floor-switch:${t.floor}:${t.switch}`;
    case 'monster':
      return `monster:${t.monster}`;
  }
}

/** The Rooms condition column: "clear" or the enemy's name. */
export function roomCondition(tr: Tr, row: RoomRow): string {
  return row.kind === 'clear' ? tr('rooms-clear') : (row.name ?? '');
}

/** "<area> - <condition>" (rooms-row-label), the rule form's end-item label. */
export function roomLabel(tr: Tr, row: RoomRow): string {
  return `${row.area} - ${roomCondition(tr, row)}`;
}

/** quest-parent-label: "<name>  (<slug>)" (two spaces, as the Lisp client). */
export function parentLabel(p: QuestParent): string {
  return `${p.name}  (${p.slug})`;
}
