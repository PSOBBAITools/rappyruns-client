<script lang="ts">
  import { runs as runsApi } from '../lib/ipc.ts';
  import type { HostState, RunRow } from '../lib/ipc-types.ts';
  import { formatRunTime, ghostAhead, partyLabel, questStatusText, renderMsg, runStatusText } from '../lib/format.ts';
  import { act, confirmBox, showNotice, tr, ui } from '../lib/store.svelte.ts';
  import StatusLine from './StatusLine.svelte';

  let { host }: { host: HostState } = $props();

  let selected = $state<string | null>(null);
  let body = $state<HTMLTableSectionElement>();

  // The list is replaced only on host events (ui-shell R2); keep the
  // selection by id across replacements.
  $effect(() => {
    if (selected && !ui.runs.some((r) => r.id === selected)) selected = null;
  });

  const showPinshare = $derived(host.pinshareAllowed && host.settings.pinshareEnabled);
  const quest = $derived(host.quest);

  function open(row: RunRow) {
    if (row.url) void act(() => runsApi.open(row.id));
  }

  function focusRow(index: number) {
    const row = ui.runs[Math.max(0, Math.min(ui.runs.length - 1, index))];
    if (!row) return;
    selected = row.id;
    queueMicrotask(() => body?.querySelector<HTMLElement>(`tr[data-id="${row.id}"]`)?.focus());
  }

  function onKey(e: KeyboardEvent) {
    const i = ui.runs.findIndex((r) => r.id === selected);
    if (e.key === 'ArrowDown') focusRow(i + 1);
    else if (e.key === 'ArrowUp') focusRow(i < 0 ? 0 : i - 1);
    else if (e.key === 'Home') focusRow(0);
    else if (e.key === 'End') focusRow(ui.runs.length - 1);
    else if (e.key === 'Enter' && i >= 0) open(ui.runs[i]);
    else return;
    e.preventDefault();
  }

  async function upload() {
    await showNotice(await act(() => runsApi.uploadVideo(selected)));
  }

  async function clearList() {
    if (await confirmBox(tr('clear-list-confirm'))) await act(() => runsApi.clear());
  }
</script>

<div class="runs">
  <section class="status card" aria-live="polite">
    <StatusLine line={host.game} />
    <StatusLine line={host.server} />
    <StatusLine line={host.token} />
    {#if showPinshare}<StatusLine line={host.pinshare} />{/if}
  </section>

  <div class="quest-row">
    <div class="quest card" class:active={quest.kind === 'active'} title={questStatusText(tr, quest)}>
      {#if quest.kind === 'active'}
        <span class="clock num">{quest.elapsed}</span>
        {#if quest.recording}<span class="rec" aria-label="REC">REC</span>{/if}
        <span class="slug">{quest.slug}{quest.others > 0 ? ` (+${quest.others})` : ''}</span>
        {#if quest.ghost}
          {@const ahead = ghostAhead(quest.ghost.gap)}
          <span class="ghost num">
            vs {quest.ghost.target}
            {#if quest.ghost.gap}
              <span class="gap" class:ahead={ahead === true} class:behind={ahead === false}>{quest.ghost.gap}</span>
            {/if}
          </span>
        {/if}
      {:else}
        <span class="muted">{questStatusText(tr, quest)}</span>
      {/if}
    </div>
    <button class="btn icon" title={tr('clear-list-tooltip')} aria-label={tr('clear-list-tooltip')} onclick={clearList}>
      <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"><path d="M4 4l8 8M12 4l-8 8" /></svg>
    </button>
  </div>

  <div class="table-wrap card">
    <table role="grid" onkeydown={onKey}>
      <colgroup>
        <col class="c-quest" />
        <col class="c-time" />
        <col class="c-party" />
        <col class="c-video" />
        <col class="c-status" />
      </colgroup>
      <thead>
        <tr>
          <th>{tr('col-quest')}</th>
          <th class="right">{tr('col-time')}</th>
          <th>{tr('col-party')}</th>
          <th>{tr('col-video')}</th>
          <th>{tr('col-status')}</th>
        </tr>
      </thead>
      <tbody bind:this={body}>
        {#each ui.runs as row, i (row.id)}
          {@const status = runStatusText(tr, row)}
          {@const video = row.video ? renderMsg(tr, row.video) : ''}
          <tr
            data-id={row.id}
            tabindex={selected === row.id || (selected === null && i === 0) ? 0 : -1}
            aria-selected={selected === row.id}
            class:selected={selected === row.id}
            class:linked={row.url !== null}
            onclick={() => (selected = row.id)}
            ondblclick={() => open(row)}
            onfocus={() => (selected = row.id)}
          >
            <td title={row.quest}>{row.quest}</td>
            <td class="right num">{formatRunTime(row.timeMs)}</td>
            <td class="num">{partyLabel(row)}</td>
            <td title={video}>
              {#if row.video && 'key' in row.video && row.video.key === 'video-uploading'}
                {@const pct = Number(row.video.args?.[0] ?? 0)}
                <span class="progress" style:--pct="{pct}%"><span>{video}</span></span>
              {:else}
                {video}
              {/if}
            </td>
            <td title={status} class:tone-error={row.statusError}>
              <span class="status-cell">
                <span class="status-text">{status}</span>
                {#if row.url}
                  <button
                    class="open"
                    tabindex="-1"
                    title={tr('run-open-page')}
                    aria-label={tr('run-open-page')}
                    onclick={(e) => {
                      e.stopPropagation();
                      open(row);
                    }}
                  >
                    <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"
                      ><path d="M9 3h4v4M13 3L7 9M11 9.5V13H3V5h3.5" /></svg
                    >
                  </button>
                {/if}
              </span>
            </td>
          </tr>
        {/each}
      </tbody>
    </table>
    {#if ui.runs.length === 0}
      <p class="empty muted">{tr('runs-empty')}</p>
    {/if}
  </div>

  <div class="actions">
    <button class="btn primary" onclick={upload}>
      <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"
        ><path d="M8 11V3M4.5 6.5L8 3l3.5 3.5M3 13h10" /></svg
      >
      {tr('upload-button')}
    </button>
    <button class="btn" onclick={() => act(() => runsApi.openRecordingsFolder())}>
      <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"
        ><path d="M2 4.5V12.5h12V6H8L6.5 4.5z" /></svg
      >
      {tr('recordings-folder-button')}
    </button>
    <button class="btn" onclick={() => act(() => runsApi.openMyRuns())}>
      <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"
        ><path d="M9 3h4v4M13 3L7 9M11 9.5V13H3V5h3.5" /></svg
      >
      {tr('my-runs-button')}
    </button>
    <span class="spacer"></span>
    <button class="btn" onclick={() => act(() => runsApi.retry())}>
      <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"
        ><path d="M13 8a5 5 0 1 1-1.5-3.5M13 3v2.5h-2.5" /></svg
      >
      {tr('retry-button')}
    </button>
  </div>
</div>

<style>
  .runs {
    display: flex;
    flex-direction: column;
    gap: 10px;
    height: 100%;
    min-height: 0;
  }
  .card {
    background: var(--surface);
    border: 1px solid var(--line);
    border-radius: var(--radius);
    box-shadow: var(--shadow);
  }
  .status {
    display: grid;
    gap: 2px;
    padding: 8px 12px;
  }
  .quest-row {
    display: flex;
    gap: 8px;
    align-items: stretch;
  }
  .quest {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 6px 12px;
    min-height: 40px;
    overflow: hidden;
    white-space: nowrap;
  }
  .quest.active {
    border-color: var(--accent);
    background: linear-gradient(90deg, var(--accent-soft), var(--surface) 70%);
  }
  .clock {
    font-size: 20px;
    font-weight: 600;
    letter-spacing: 0.01em;
  }
  .rec {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    font-size: 11px;
    font-weight: 700;
    color: var(--rec);
    letter-spacing: 0.06em;
  }
  .rec::before {
    content: '';
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--rec);
    animation: blink 1.6s ease-in-out infinite;
  }
  @keyframes blink {
    50% {
      opacity: 0.3;
    }
  }
  @media (prefers-reduced-motion: reduce) {
    .rec::before {
      animation: none;
    }
  }
  .slug {
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .ghost {
    margin-left: auto;
    color: var(--muted);
  }
  .gap {
    font-weight: 600;
    margin-left: 4px;
  }
  .gap.ahead {
    color: var(--ok);
  }
  .gap.behind {
    color: var(--danger);
  }
  .quest-row .btn.icon {
    height: auto;
    width: 40px;
  }

  .table-wrap {
    flex: 1;
    min-height: 120px;
    overflow: auto;
  }
  table {
    width: 100%;
    border-collapse: collapse;
    table-layout: fixed;
  }
  .c-quest {
    width: 28%;
  }
  .c-time {
    width: 92px;
  }
  .c-party {
    width: 64px;
  }
  .c-video {
    width: 128px;
  }
  th {
    position: sticky;
    top: 0;
    z-index: 1;
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
  .right {
    text-align: right;
  }
  tbody tr {
    cursor: default;
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
  .status-cell {
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .status-text {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .open {
    flex: none;
    display: inline-grid;
    place-items: center;
    width: 22px;
    height: 22px;
    padding: 0;
    border: none;
    border-radius: 4px;
    background: none;
    color: var(--muted);
    cursor: pointer;
    opacity: 0;
  }
  tr:hover .open,
  tr.selected .open {
    opacity: 1;
  }
  .open:hover {
    color: var(--accent);
    background: var(--accent-soft);
  }
  .open svg {
    width: 14px;
    height: 14px;
  }
  .progress {
    position: relative;
    display: block;
    border-radius: 4px;
    background: linear-gradient(90deg, var(--accent-soft) var(--pct), transparent var(--pct));
    padding: 0 4px;
    margin: 0 -4px;
  }
  .progress span {
    position: relative;
  }
  .empty {
    text-align: center;
    padding: 28px 16px;
    margin: 0;
  }

  .actions {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }
  .spacer {
    flex: 1;
  }
</style>
