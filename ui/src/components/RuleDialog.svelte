<script lang="ts">
  // ui-shell §3: the single quest-rule registration form. Validation errors
  // keep it open; Register hands the POST to the host and closes; the result
  // arrives as a message box (the form itself is the review - no extra
  // confirmation).
  import { rules } from '../lib/ipc.ts';
  import { parentLabel, roomLabel, triggerLabel } from '../lib/format.ts';
  import { endChoices, manualLabelKey, validateRuleForm, type EndChoice } from '../lib/rule-form.ts';
  import { act, alertBox, ruleForm, showNotice, tr, type RuleFormData } from '../lib/store.svelte.ts';

  let { data }: { data: RuleFormData } = $props();

  let el = $state<HTMLDialogElement>();
  const choices: EndChoice[] = $derived(endChoices(data.rows, data.preset));

  // The form is re-created per opening ({#key} in App), so the initial value is all it needs.
  // svelte-ignore state_referenced_locally
  let parent = $state(data.detected ?? data.parents[0]?.slug ?? '');
  let name = $state('');
  let description = $state('');
  let endIndex = $state(0);
  let val1 = $state('');
  let val2 = $state('');
  let startWarpIn = $state(false);
  let busy = $state(false);

  const end = $derived(choices[endIndex] ?? null);
  const manual = $derived(end?.kind === 'manual' ? end.manual : null);

  $effect(() => {
    el?.showModal();
  });

  function close() {
    el?.close();
    ruleForm.data = null;
  }

  function choiceLabel(c: EndChoice): string {
    return c.kind === 'row' ? roomLabel(tr, c.row) : tr(manualLabelKey[c.manual]);
  }

  async function submit(e: SubmitEvent) {
    e.preventDefault();
    const v = validateRuleForm({ parent, name, description, end, val1, val2, startWarpIn });
    if ('error' in v) {
      await alertBox(tr(v.error));
      return;
    }
    busy = true;
    close();
    try {
      await showNotice(await act(() => rules.create(v.rule)));
    } finally {
      busy = false;
    }
  }
</script>

<dialog
  bind:this={el}
  aria-labelledby="rule-title"
  oncancel={(e) => {
    e.preventDefault();
    close();
  }}
>
  <form onsubmit={submit} novalidate>
    <h2 id="rule-title">{tr('rule-dialog-title')}</h2>

    <label class="field">
      <span>{tr('rule-quest-label')}</span>
      <select bind:value={parent}>
        {#each data.parents as p (p.slug)}
          <option value={p.slug}>{parentLabel(p)}</option>
        {/each}
      </select>
    </label>

    <label class="field">
      <span>{tr('rule-name-label')}</span>
      <!-- svelte-ignore a11y_autofocus -->
      <input type="text" bind:value={name} autofocus autocomplete="off" />
    </label>

    <label class="field">
      <span>{tr('rule-desc-label')}</span>
      <input type="text" bind:value={description} autocomplete="off" />
    </label>

    <label class="field">
      <span>{tr('rule-end-label-form')}</span>
      <select bind:value={endIndex}>
        {#each choices as c, i (i)}
          <option value={i}>{choiceLabel(c)}</option>
        {/each}
      </select>
    </label>

    <p class="preview" class:manual={manual !== null}>
      {#if end?.kind === 'row'}→ <code>{triggerLabel(end.trigger)}</code>{:else if end}{tr('rule-manual-hint')}{/if}
    </p>

    <div class="values">
      <label class="field inline" class:disabled={manual === null}>
        <span>{tr('rule-val1-label')}</span>
        <input type="text" inputmode="numeric" bind:value={val1} disabled={manual === null} />
      </label>
      <label class="field inline" class:disabled={manual !== 'floor-switch'}>
        <span>{tr('rule-val2-label')}</span>
        <input type="text" inputmode="numeric" bind:value={val2} disabled={manual !== 'floor-switch'} />
      </label>
    </div>

    <label class="field">
      <span>{tr('rule-start-label-form')}</span>
      <select bind:value={startWarpIn}>
        <option value={false}>{tr('rule-start-inherit')}</option>
        <option value={true}>{tr('rule-start-warp-in')}</option>
      </select>
    </label>

    <div class="buttons">
      <button class="btn primary" type="submit" disabled={busy}>{tr('rule-register-ok')}</button>
      <button class="btn" type="button" onclick={close}>{tr('rule-cancel')}</button>
    </div>
  </form>
</dialog>

<style>
  dialog {
    width: min(560px, calc(100vw - 32px));
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
  form {
    display: flex;
    flex-direction: column;
    gap: 10px;
    padding: 18px 20px 16px;
  }
  h2 {
    margin: 0 0 4px;
    font-size: 16px;
  }
  .field {
    display: flex;
    flex-direction: column;
    gap: 3px;
  }
  .field > span {
    font-size: 12.5px;
    color: var(--muted);
  }
  .field.inline {
    flex-direction: row;
    align-items: center;
    gap: 8px;
  }
  .field.inline input {
    width: 110px;
  }
  .field.disabled > span {
    color: var(--faint);
  }
  .values {
    display: flex;
    gap: 20px;
  }
  .preview {
    margin: -4px 0 0;
    padding: 6px 10px;
    border-radius: 6px;
    background: var(--surface-2);
    font-size: 13px;
    min-height: 32px;
  }
  .preview.manual {
    color: var(--muted);
  }
  code {
    font-family: 'Cascadia Mono', Consolas, monospace;
    color: var(--accent);
  }
  .buttons {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    margin-top: 6px;
  }
  .buttons .btn {
    min-width: 88px;
    justify-content: center;
  }
</style>
