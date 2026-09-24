<script lang="ts">
  // The main window (ui-shell §1): Runs / Rooms (moderators) / Settings.
  // The window title, tray and close-to-tray are host-side.
  import { onMount } from 'svelte';
  import { usingMock } from './lib/ipc.ts';
  import { ruleForm, start, tr, ui, type Tab } from './lib/store.svelte.ts';
  import RunsTab from './components/RunsTab.svelte';
  import RoomsTab from './components/RoomsTab.svelte';
  import SettingsTab from './components/SettingsTab.svelte';
  import MessageDialog from './components/MessageDialog.svelte';
  import RuleDialog from './components/RuleDialog.svelte';

  onMount(() => void start());

  const tabs = $derived<{ id: Tab; key: string }[]>([
    { id: 'runs', key: 'tab-runs' },
    ...(ui.host?.moderator ? [{ id: 'rooms' as Tab, key: 'tab-rooms' }] : []),
    { id: 'settings', key: 'tab-settings' },
  ]);

  // Losing the moderator role while on Rooms: fall back to Runs (the tab is
  // matched by name, never by index - ui-shell §1.4.1).
  $effect(() => {
    if (!tabs.some((t) => t.id === ui.tab)) ui.tab = 'runs';
  });

  function onTabKey(e: KeyboardEvent) {
    const i = tabs.findIndex((t) => t.id === ui.tab);
    let next = -1;
    if (e.key === 'ArrowRight') next = (i + 1) % tabs.length;
    else if (e.key === 'ArrowLeft') next = (i - 1 + tabs.length) % tabs.length;
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = tabs.length - 1;
    if (next < 0) return;
    e.preventDefault();
    ui.tab = tabs[next].id;
    (e.currentTarget as HTMLElement).querySelector<HTMLElement>(`#tab-${ui.tab}`)?.focus();
  }
</script>

{#if ui.error}
  <main class="message">{ui.error}</main>
{:else if ui.ready && ui.host}
  <div class="shell">
    <header>
      <div class="tabs" role="tablist" tabindex="-1" onkeydown={onTabKey}>
        {#each tabs as t (t.id)}
          <button
            id="tab-{t.id}"
            role="tab"
            aria-selected={ui.tab === t.id}
            aria-controls="panel"
            tabindex={ui.tab === t.id ? 0 : -1}
            class:active={ui.tab === t.id}
            onclick={() => (ui.tab = t.id)}>{tr(t.key)}</button
          >
        {/each}
      </div>
      {#if usingMock}<span class="mock" title="mock-host.ts">mock host</span>{/if}
    </header>

    <div class="panel" id="panel" role="tabpanel" aria-labelledby="tab-{ui.tab}">
      {#if ui.tab === 'runs'}
        <RunsTab host={ui.host} />
      {:else if ui.tab === 'rooms'}
        <RoomsTab />
      {:else}
        <SettingsTab host={ui.host} />
      {/if}
    </div>
  </div>

  {#if ruleForm.data}
    {#key ruleForm.data}
      <RuleDialog data={ruleForm.data} />
    {/key}
  {/if}
{/if}
<MessageDialog />

<style>
  .shell {
    display: grid;
    grid-template-rows: auto 1fr;
    height: 100vh;
  }
  header {
    display: flex;
    align-items: flex-end;
    gap: 12px;
    padding: 0 16px;
    background: var(--surface);
    border-bottom: 1px solid var(--line);
  }
  .tabs {
    display: flex;
    gap: 4px;
  }
  .tabs button {
    font: inherit;
    font-weight: 500;
    padding: 10px 14px 8px;
    border: none;
    border-bottom: 2px solid transparent;
    background: none;
    color: var(--muted);
    cursor: pointer;
  }
  .tabs button:hover {
    color: var(--text);
  }
  .tabs button.active {
    color: var(--text);
    border-bottom-color: var(--accent);
    font-weight: 600;
  }
  .mock {
    margin: 0 0 9px auto;
    font-size: 11px;
    padding: 1px 8px;
    border-radius: 99px;
    border: 1px dashed var(--busy);
    color: var(--busy);
  }
  .panel {
    min-height: 0;
    overflow: hidden;
    padding: 12px 16px 14px;
  }
  .message {
    padding: 24px;
  }
</style>
