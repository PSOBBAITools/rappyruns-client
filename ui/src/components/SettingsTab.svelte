<script lang="ts">
  import { account, pinshare, settings as settingsApi, updates } from '../lib/ipc.ts';
  import type { HostState, OverlayCorner, Settings, SimpleSettingKey } from '../lib/ipc-types.ts';
  import { overlayCorners } from '../lib/format.ts';
  import { act, confirmBox, openRuleForm, ruleForm, setLanguage, showNotice, tr, ui } from '../lib/store.svelte.ts';
  import StatusLine from './StatusLine.svelte';

  let { host }: { host: HostState } = $props();
  const s = $derived(host.settings);

  // ---- Unsaved text fields: follow the host's value when IT changes
  // (pairing writes the token while the tab is open, ui-shell R19), keep
  // the user's typing otherwise.
  function follow(get: () => string) {
    let seen = get();
    let draft = $state(seen);
    $effect(() => {
      const v = get();
      if (v !== seen) {
        seen = v;
        draft = v;
      }
    });
    return {
      get value() {
        return draft;
      },
      set value(v: string) {
        draft = v;
      },
    };
  }
  const token = follow(() => host.settings.apiToken);
  const serverUrl = follow(() => host.settings.serverUrl);
  const channel = follow(() => host.settings.pinshareChannel);

  let saving = $state(false);

  function setSimple<K extends SimpleSettingKey>(key: K, value: Settings[K]) {
    void act(() => settingsApi.set(key, value));
  }

  function checked(e: Event): boolean {
    return (e.currentTarget as HTMLInputElement).checked;
  }

  async function saveConnection() {
    saving = true;
    try {
      await showNotice(await act(() => settingsApi.saveConnection(token.value, ui.debug ? serverUrl.value : null)));
    } finally {
      saving = false;
    }
  }

  // ui-shell §1.4.3 / R18: confirm only when turning on; the box ends up
  // showing what the server holds.
  async function toggleAutoPublish(e: Event) {
    const box = e.currentTarget as HTMLInputElement;
    const want = box.checked;
    if (want && !(await confirmBox(tr('auto-publish-confirm')))) {
      box.checked = false;
      return;
    }
    const r = await act(() => settingsApi.setAutoPublish(want));
    box.checked = r ? r.enabled : s.autoPublish;
    await showNotice(r?.notice);
  }

  // R17: the registry is the truth; show what was read back.
  async function toggleAutostart(e: Event) {
    const box = e.currentTarget as HTMLInputElement;
    const r = await act(() => settingsApi.setAutostart(box.checked));
    box.checked = r ? r.enabled : s.autostart;
  }

  async function toggleTriggerLog(e: Event) {
    await showNotice(await act(() => settingsApi.setTriggerLog(checked(e))));
  }

  async function togglePinshare(e: Event) {
    await act(() => pinshare.setEnabled(checked(e), channel.value));
  }

  async function applyChannel() {
    await act(() => pinshare.applyChannel(channel.value));
  }

  async function saveNewPinSet() {
    await showNotice(await act(() => pinshare.saveNew()));
  }

  async function overwritePinSet() {
    const r = await act(() => pinshare.overwriteCheck());
    if (!r) return;
    if (!r.ok) return showNotice(r.notice);
    if (await confirmBox(tr('pinshare-save-confirm-overwrite', r.name, r.pins, r.arrows))) {
      await showNotice(await act(() => pinshare.overwrite()));
    }
  }

  async function checkUpdates() {
    await showNotice(await act(() => updates.check()));
  }

  // ---- Section nav with scroll spy ----
  type Group = { id: string; key: string; show: boolean };
  const groups: Group[] = $derived([
    { id: 'language', key: 'group-language', show: true },
    { id: 'connection', key: 'group-connection', show: true },
    { id: 'recording', key: 'group-recording', show: true },
    { id: 'ghost', key: 'group-ghost', show: true },
    { id: 'pinshare', key: 'group-pinshare', show: host.pinshareAllowed },
    { id: 'updates', key: 'group-updates', show: true },
    { id: 'tray', key: 'group-tray', show: true },
    { id: 'advanced', key: 'group-advanced', show: true },
  ]);
  const visible = $derived(groups.filter((g) => g.show));
  let current = $state('language');
  let scroller = $state<HTMLElement>();

  function jump(id: string) {
    current = id;
    scroller?.querySelector(`#group-${id}`)?.scrollIntoView({ block: 'start', behavior: 'smooth' });
  }

  function spy() {
    if (!scroller) return;
    const top = scroller.getBoundingClientRect().top + 24;
    let id = visible[0]?.id ?? 'language';
    for (const g of visible) {
      const el = scroller.querySelector(`#group-${g.id}`);
      if (el && el.getBoundingClientRect().top <= top) id = g.id;
    }
    if (scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 2) id = visible[visible.length - 1].id;
    current = id;
  }
</script>

<div class="settings">
  <nav class="side" aria-label={tr('tab-settings')}>
    {#each visible as g (g.id)}
      <button class:current={current === g.id} aria-current={current === g.id ? 'true' : undefined} onclick={() => jump(g.id)}>{tr(g.key)}</button>
    {/each}
  </nav>

  <div class="groups" bind:this={scroller} onscroll={spy}>
    <section id="group-language" class="group" aria-labelledby="h-language">
      <h2 id="h-language">{tr('group-language')}</h2>
      <div class="radios" role="radiogroup" aria-labelledby="h-language">
        {#each ui.languages as lang (lang.code)}
          <label class="choice">
            <input type="radio" name="language" checked={ui.language === lang.code} onchange={() => setLanguage(lang.code)} />
            <span>{lang.label}</span>
          </label>
        {/each}
      </div>
    </section>

    <section id="group-connection" class="group" aria-labelledby="h-connection">
      <h2 id="h-connection">{tr('group-connection')}</h2>
      <StatusLine line={host.token} />
      <div class="row">
        <button class="btn" disabled={host.pairing} onclick={() => act(() => account.link())}>
          <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"
            ><path d="M7 9.5a2.5 2.5 0 0 0 3.5 0l2.5-2.5a2.5 2.5 0 0 0-3.5-3.5L9 4M9 6.5a2.5 2.5 0 0 0-3.5 0L3 9a2.5 2.5 0 0 0 3.5 3.5L7 12" /></svg
          >
          {tr('link-account-button')}
        </button>
      </div>
      <form
        class="fields"
        onsubmit={(e) => {
          e.preventDefault();
          void saveConnection();
        }}
      >
        {#if ui.debug}
          <label class="field">
            <span>{tr('server-url-label')}</span>
            <input type="text" bind:value={serverUrl.value} spellcheck="false" autocomplete="off" />
          </label>
        {/if}
        <label class="field">
          <span>{tr('api-token-label')}</span>
          <input type="password" bind:value={token.value} spellcheck="false" autocomplete="off" />
        </label>
        <div class="row">
          <button class="btn primary" type="submit" disabled={saving}>{tr('save-button')}</button>
        </div>
      </form>
      <StatusLine line={host.server} />
    </section>

    <section id="group-recording" class="group" aria-labelledby="h-recording">
      <h2 id="h-recording">{tr('group-recording')}</h2>
      <label class="choice">
        <input type="checkbox" checked={s.trackingOnly} onchange={(e) => setSimple('trackingOnly', checked(e))} />
        <span>{tr('tracking-only-label')}</span>
      </label>
      <label class="choice indent" class:disabled={!s.trackingOnly}>
        <input
          type="checkbox"
          checked={s.trackingPrivate}
          disabled={!s.trackingOnly}
          onchange={(e) => setSimple('trackingPrivate', checked(e))}
        />
        <span>{tr('tracking-private-label')}</span>
      </label>
      <label class="choice">
        <input type="checkbox" checked={s.recordAudio} onchange={(e) => setSimple('recordAudio', checked(e))} />
        <span>{tr('record-audio-label')}</span>
      </label>
      <label class="choice">
        <input type="checkbox" checked={s.autoPublish} onchange={toggleAutoPublish} />
        <span>{tr('auto-publish-label')}</span>
      </label>
      <div class="folder">
        <span class="path" title={s.recordDir}>{tr('record-dir-label', s.recordDir)}</span>
        <button class="btn" onclick={() => act(() => settingsApi.chooseRecordDir())}>{tr('change-folder-button')}</button>
      </div>
      <div class="notes muted">
        {#if s.recordMaxTotalGb > 0}<p>{tr('record-storage-note', s.recordMaxTotalGb)}</p>{/if}
        <p>{tr('video-retention-note')}</p>
      </div>
    </section>

    <section id="group-ghost" class="group" aria-labelledby="h-ghost">
      <h2 id="h-ghost">{tr('group-ghost')}</h2>
      <label class="choice">
        <input type="checkbox" checked={s.ghostRace} onchange={(e) => setSimple('ghostRace', checked(e))} />
        <span>{tr('ghost-race-label')}</span>
      </label>
      <label class="choice">
        <input type="checkbox" checked={s.ghostOverlay} onchange={(e) => setSimple('ghostOverlay', checked(e))} />
        <span>{tr('ghost-overlay-label')}</span>
      </label>
      <label class="choice">
        <input type="checkbox" checked={s.ghostMarker} onchange={(e) => setSimple('ghostMarker', checked(e))} />
        <span>{tr('ghost-marker-label')}</span>
      </label>
      <label class="field inline">
        <span>{tr('overlay-corner-label')}</span>
        <select value={s.overlayCorner} onchange={(e) => setSimple('overlayCorner', e.currentTarget.value as OverlayCorner)}>
          {#each overlayCorners as c (c)}
            <option value={c}>{tr(`corner-${c}`)}</option>
          {/each}
        </select>
      </label>
    </section>

    {#if host.pinshareAllowed}
      <section id="group-pinshare" class="group" aria-labelledby="h-pinshare">
        <h2 id="h-pinshare">{tr('group-pinshare')}</h2>
        <label class="choice">
          <input type="checkbox" checked={s.pinshareEnabled} onchange={togglePinshare} />
          <span>{tr('pinshare-enabled-label')}</span>
        </label>
        <form
          class="fields"
          onsubmit={(e) => {
            e.preventDefault();
            void applyChannel();
          }}
        >
          <label class="field">
            <span>{tr('pinshare-channel-label')}</span>
            <span class="inline-input">
              <input type="text" maxlength="64" bind:value={channel.value} spellcheck="false" autocomplete="off" />
              <button class="btn" type="submit">{tr('pinshare-channel-save-button')}</button>
            </span>
          </label>
        </form>
        <p class="notes muted">{tr('pinshare-channel-note')}</p>
        <div class="status-box">
          <StatusLine line={host.pinshare} />
          <StatusLine line={host.pinSet} dot={false} />
        </div>
        <div class="row">
          <button class="btn" onclick={saveNewPinSet}>{tr('pinshare-save-new-button')}</button>
          <button class="btn" onclick={overwritePinSet}>{tr('pinshare-save-overwrite-button')}</button>
        </div>
      </section>
    {/if}

    <section id="group-updates" class="group" aria-labelledby="h-updates">
      <h2 id="h-updates">{tr('group-updates')}</h2>
      <StatusLine line={host.version} dot={false} />
      <label class="choice">
        <input type="checkbox" checked={s.autoUpdate} onchange={(e) => setSimple('autoUpdate', checked(e))} />
        <span>{tr('auto-update-label')}</span>
      </label>
      <div class="row">
        <button class="btn" disabled={host.updating} onclick={checkUpdates}>{tr('check-updates-button')}</button>
      </div>
    </section>

    <section id="group-tray" class="group" aria-labelledby="h-tray">
      <h2 id="h-tray">{tr('group-tray')}</h2>
      <label class="choice">
        <input type="checkbox" checked={s.closeToTray} onchange={(e) => setSimple('closeToTray', checked(e))} />
        <span>{tr('close-to-tray-label')}</span>
      </label>
      <label class="choice">
        <input type="checkbox" checked={s.autostart} onchange={toggleAutostart} />
        <span>{tr('autostart-label')}</span>
      </label>
      <label class="choice">
        <input type="checkbox" checked={s.startMinimized} onchange={(e) => setSimple('startMinimized', checked(e))} />
        <span>{tr('start-minimized-label')}</span>
      </label>
      <label class="choice">
        <input type="checkbox" checked={s.rankToast} onchange={(e) => setSimple('rankToast', checked(e))} />
        <span>{tr('rank-toast-label')}</span>
      </label>
    </section>

    <section id="group-advanced" class="group" aria-labelledby="h-advanced">
      <h2 id="h-advanced">{tr('group-advanced')}</h2>
      <label class="choice">
        <input type="checkbox" checked={s.triggerLog} onchange={toggleTriggerLog} />
        <span>{tr('trigger-log-label')}</span>
      </label>
      {#if host.moderator}
        <div class="row">
          <button class="btn" disabled={ruleForm.loading} onclick={() => openRuleForm()}>{tr('register-rule-button')}</button>
        </div>
      {/if}
    </section>
  </div>
</div>

<style>
  .settings {
    display: grid;
    grid-template-columns: 180px 1fr;
    gap: 16px;
    height: 100%;
    min-height: 0;
  }
  .side {
    display: flex;
    flex-direction: column;
    gap: 2px;
    padding-top: 2px;
  }
  .side button {
    font: inherit;
    text-align: left;
    padding: 6px 10px;
    border: none;
    border-radius: 6px;
    background: none;
    color: var(--muted);
    cursor: pointer;
  }
  .side button:hover {
    background: var(--row-hover);
    color: var(--text);
  }
  .side button.current {
    background: var(--accent-soft);
    color: var(--text);
    font-weight: 600;
  }
  .groups {
    overflow: auto;
    min-height: 0;
    padding-right: 4px;
    display: flex;
    flex-direction: column;
    gap: 12px;
  }
  .group {
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 12px 16px 14px;
    background: var(--surface);
    border: 1px solid var(--line);
    border-radius: var(--radius);
    box-shadow: var(--shadow);
    scroll-margin-top: 2px;
  }
  h2 {
    font-size: 13px;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--muted);
    margin: 0 0 2px;
  }
  :global(:lang(ja)) h2 {
    text-transform: none;
    letter-spacing: 0;
  }
  .radios {
    display: flex;
    gap: 20px;
  }
  .choice {
    display: flex;
    align-items: flex-start;
    gap: 8px;
    cursor: pointer;
  }
  .choice input {
    margin-top: 2px;
  }
  .choice.indent {
    margin-left: 24px;
  }
  .choice.disabled {
    color: var(--faint);
    cursor: default;
  }
  .row {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }
  .fields {
    display: flex;
    flex-direction: column;
    gap: 8px;
    margin: 0;
  }
  .field {
    display: flex;
    flex-direction: column;
    gap: 3px;
    max-width: 560px;
  }
  .field > span:first-child {
    font-size: 12.5px;
    color: var(--muted);
  }
  .field.inline {
    flex-direction: row;
    align-items: center;
    gap: 10px;
  }
  .field.inline > span:first-child {
    font-size: inherit;
    color: inherit;
  }
  .inline-input {
    display: flex;
    gap: 8px;
  }
  .inline-input input {
    flex: 1;
    min-width: 0;
  }
  .folder {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 6px 10px;
    border: 1px dashed var(--line-strong);
    border-radius: 6px;
    background: var(--surface-2);
  }
  .path {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .notes {
    font-size: 12.5px;
    margin: 0;
  }
  .notes p {
    margin: 0;
  }
  .status-box {
    display: grid;
    gap: 2px;
    padding: 6px 10px;
    border-radius: 6px;
    background: var(--surface-2);
  }
  @media (max-width: 640px) {
    .settings {
      grid-template-columns: 1fr;
    }
    .side {
      display: none;
    }
  }
</style>
