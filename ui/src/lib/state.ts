// The `state` event protocol: the host keeps the last HostState it sent and
// emits only the top-level keys whose value changed (diffState); the UI
// merges them (applyStatePatch). A 4 Hz tick with nothing new sends nothing,
// and a changed key re-renders only what reads it (ui-shell R1).

import type { HostState } from './ipc-types.ts';

/** Returns a new state with every key of `patch` replacing the old value whole. */
export function applyStatePatch(state: HostState, patch: Partial<HostState>): HostState {
  return { ...state, ...patch };
}

/** The keys of `next` whose JSON differs from `prev`; null when nothing changed (send no event). */
export function diffState(prev: HostState | null, next: HostState): Partial<HostState> | null {
  const patch: Record<string, unknown> = {};
  let changed = false;
  for (const key of Object.keys(next) as (keyof HostState)[]) {
    if (!prev || JSON.stringify(prev[key]) !== JSON.stringify(next[key])) {
      patch[key] = next[key];
      changed = true;
    }
  }
  return changed ? (patch as Partial<HostState>) : null;
}
