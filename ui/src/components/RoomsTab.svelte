<script lang="ts">
  import type { RoomRow } from '../lib/ipc-types.ts';
  import { roomCondition, triggerLabel } from '../lib/format.ts';
  import { openRuleForm, tr, ui } from '../lib/store.svelte.ts';

  let selected = $state<string | null>(null);
  let body = $state<HTMLTableSectionElement>();

  // The host re-sends rooms only when rooms-list-signature changes (R2).
  $effect(() => {
    if (selected && !ui.rooms.some((r) => r.id === selected)) selected = null;
  });

  function register(row: RoomRow) {
    if (row.trigger) void openRuleForm(row);
  }

  function focusRow(index: number) {
    const row = ui.rooms[Math.max(0, Math.min(ui.rooms.length - 1, index))];
    if (!row) return;
    selected = row.id;
    queueMicrotask(() => body?.querySelector<HTMLElement>(`tr[data-id="${row.id}"]`)?.focus());
  }

  function onKey(e: KeyboardEvent) {
    const i = ui.rooms.findIndex((r) => r.id === selected);
    if (e.key === 'ArrowDown') focusRow(i + 1);
    else if (e.key === 'ArrowUp') focusRow(i < 0 ? 0 : i - 1);
    else if (e.key === 'Home') focusRow(0);
    else if (e.key === 'End') focusRow(ui.rooms.length - 1);
    else if (e.key === 'Enter' && i >= 0) register(ui.rooms[i]);
    else return;
    e.preventDefault();
  }
</script>

<div class="rooms">
  <p class="hint muted">{tr('rooms-hint')}</p>
  <div class="table-wrap">
    <table role="grid" onkeydown={onKey}>
      <colgroup>
        <col />
        <col />
        <col class="c-trigger" />
      </colgroup>
      <thead>
        <tr>
          <th>{tr('col-area')}</th>
          <th>{tr('col-condition')}</th>
          <th>{tr('col-trigger')}</th>
        </tr>
      </thead>
      <tbody bind:this={body}>
        {#each ui.rooms as row, i (row.id)}
          <tr
            data-id={row.id}
            tabindex={selected === row.id || (selected === null && i === 0) ? 0 : -1}
            aria-selected={selected === row.id}
            class:selected={selected === row.id}
            class:clear={row.kind === 'clear'}
            onclick={() => (selected = row.id)}
            ondblclick={() => register(row)}
            onfocus={() => (selected = row.id)}
          >
            <td>{row.area}</td>
            <td class="cond">{roomCondition(tr, row)}</td>
            <td class="mono">{row.trigger ? triggerLabel(row.trigger) : ''}</td>
          </tr>
        {/each}
      </tbody>
    </table>
    {#if ui.rooms.length === 0}
      <p class="empty muted">{tr('rooms-empty')}</p>
    {/if}
  </div>
</div>

<style>
  .rooms {
    display: flex;
    flex-direction: column;
    gap: 10px;
    height: 100%;
    min-height: 0;
  }
  .hint {
    margin: 0;
  }
  .table-wrap {
    flex: 1;
    min-height: 0;
    overflow: auto;
    background: var(--surface);
    border: 1px solid var(--line);
    border-radius: var(--radius);
    box-shadow: var(--shadow);
  }
  table {
    width: 100%;
    border-collapse: collapse;
    table-layout: fixed;
  }
  .c-trigger {
    width: 34%;
  }
  th {
    position: sticky;
    top: 0;
    background: var(--surface-2);
    color: var(--muted);
    font-weight: 600;
    font-size: 12px;
    text-align: left;
    padding: 6px 10px;
    border-bottom: 1px solid var(--line);
  }
  td {
    padding: 5px 10px;
    border-bottom: 1px solid var(--line);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  tbody tr:hover {
    background: var(--row-hover);
  }
  tbody tr.selected {
    background: var(--row-selected);
  }
  tbody tr:focus-visible {
    outline: 2px solid var(--focus);
    outline-offset: -2px;
  }
  tr.clear .cond {
    font-weight: 600;
  }
  .mono {
    font-family: 'Cascadia Mono', Consolas, monospace;
    font-size: 12.5px;
    color: var(--muted);
  }
  .empty {
    text-align: center;
    padding: 28px 16px;
    margin: 0;
  }
</style>
