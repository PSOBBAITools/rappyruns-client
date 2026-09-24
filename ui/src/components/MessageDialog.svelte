<script lang="ts">
  // The message box: OK, or Yes/No for a confirmation. One at a time from
  // dialogs.queue. It is an in-page modal, so the host's 4 Hz updates and
  // the tray keep running underneath (ui-shell §0).
  import { dialogs, tr } from '../lib/store.svelte.ts';

  let el = $state<HTMLDialogElement>();
  const current = $derived(dialogs.queue[0]);

  $effect(() => {
    if (!el) return;
    if (current && !el.open) el.showModal();
    else if (!current && el.open) el.close();
  });

  function answer(yes: boolean) {
    const d = dialogs.queue.shift();
    d?.resolve(yes);
  }
</script>

<dialog
  bind:this={el}
  class:error={current?.error}
  aria-describedby="message-text"
  oncancel={(e) => {
    e.preventDefault();
    answer(false);
  }}
>
  {#if current}
    <div class="body">
      <span class="badge" aria-hidden="true">{current.error ? '!' : current.confirm ? '?' : 'i'}</span>
      <p id="message-text">{current.text}</p>
    </div>
    <div class="buttons">
      {#if current.confirm}
        <button class="btn primary" onclick={() => answer(true)}>{tr('dialog-yes')}</button>
        <!-- svelte-ignore a11y_autofocus -->
        <button class="btn" autofocus onclick={() => answer(false)}>{tr('dialog-no')}</button>
      {:else}
        <!-- svelte-ignore a11y_autofocus -->
        <button class="btn primary" autofocus onclick={() => answer(true)}>{tr('dialog-ok')}</button>
      {/if}
    </div>
  {/if}
</dialog>

<style>
  dialog {
    width: min(480px, calc(100vw - 32px));
    padding: 0;
    border: 1px solid var(--line);
    border-radius: 10px;
    background: var(--surface);
    color: var(--text);
    box-shadow: var(--shadow-lg);
  }
  dialog::backdrop {
    background: var(--backdrop);
  }
  .body {
    display: flex;
    gap: 12px;
    padding: 18px 20px 8px;
  }
  .badge {
    flex: none;
    width: 26px;
    height: 26px;
    border-radius: 50%;
    display: grid;
    place-items: center;
    font-weight: 700;
    font-size: 14px;
    background: var(--accent-soft);
    color: var(--accent);
  }
  .error .badge {
    background: var(--danger-soft);
    color: var(--danger);
  }
  p {
    margin: 2px 0 0;
    white-space: pre-line;
    overflow-wrap: anywhere;
    user-select: text;
  }
  .buttons {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    padding: 12px 20px 16px;
  }
  .buttons .btn {
    min-width: 80px;
    justify-content: center;
  }
</style>
