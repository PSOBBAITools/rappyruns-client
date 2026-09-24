<script lang="ts">
  import { onMount } from 'svelte';
  import { app, inHost, type AppSnapshot } from './lib/ipc';
  import { translator } from './lib/i18n';

  // P0 shell: proves the host <-> UI round trip (hello, language switch)
  // and the i18n table. The real screens arrive in P3 (spec ui-shell.md).
  let snapshot = $state<AppSnapshot | null>(null);
  let error = $state<string | null>(null);
  let tab = $state<'runs' | 'settings'>('runs');

  const tr = $derived(snapshot ? translator(snapshot.strings) : () => '');

  onMount(async () => {
    if (!inHost) {
      error = 'Open this page from RappyRunsClient.exe.';
      return;
    }
    try {
      snapshot = await app.hello();
      document.documentElement.lang = snapshot.language;
    } catch (e) {
      error = String(e);
    }
  });

  async function setLanguage(code: string) {
    snapshot = await app.setLanguage(code);
    document.documentElement.lang = snapshot.language;
  }
</script>

{#if error}
  <main class="message">{error}</main>
{:else if snapshot}
  <div class="shell">
    <nav class="tabs" aria-label="Sections">
      <button class:active={tab === 'runs'} onclick={() => (tab = 'runs')}>{tr('tab-runs')}</button>
      <button class:active={tab === 'settings'} onclick={() => (tab = 'settings')}>{tr('tab-settings')}</button>
    </nav>

    <main>
      {#if tab === 'runs'}
        <p class="empty">Rappy Runs Client</p>
      {:else}
        <section class="group">
          <div class="row">
            {#each snapshot.languages as lang (lang.code)}
              <label>
                <input
                  type="radio"
                  name="language"
                  checked={snapshot.language === lang.code}
                  onchange={() => setLanguage(lang.code)}
                />
                {lang.label}
              </label>
            {/each}
          </div>
        </section>
        <section class="group">
          <h2>{tr('group-updates')}</h2>
          <p>{tr('version-status', snapshot.version, null)}</p>
        </section>
      {/if}
    </main>
  </div>
{/if}

<style>
  .shell {
    display: grid;
    grid-template-rows: auto 1fr;
    height: 100vh;
  }
  .tabs {
    display: flex;
    gap: 2px;
    padding: 8px 12px 0;
    border-bottom: 1px solid var(--line);
    background: var(--surface);
  }
  .tabs button {
    font: inherit;
    padding: 8px 16px;
    border: 1px solid transparent;
    border-bottom: none;
    border-radius: 6px 6px 0 0;
    background: none;
    color: var(--muted);
    cursor: pointer;
  }
  .tabs button.active {
    color: var(--text);
    background: var(--bg);
    border-color: var(--line);
    margin-bottom: -1px;
  }
  main {
    overflow: auto;
    padding: 16px 20px;
  }
  .message {
    padding: 24px;
  }
  .empty {
    color: var(--muted);
  }
  .group {
    margin-bottom: 20px;
  }
  .group h2 {
    font-size: 1rem;
    margin: 0 0 6px;
  }
  .row {
    display: flex;
    gap: 16px;
  }
</style>
