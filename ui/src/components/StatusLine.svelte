<script lang="ts">
  import type { Line } from '../lib/ipc-types.ts';
  import { renderMsg } from '../lib/format.ts';
  import { tr } from '../lib/store.svelte.ts';

  let { line, dot = true }: { line: Line; dot?: boolean } = $props();
  const tone = $derived(line.tone ?? 'neutral');
  const text = $derived(renderMsg(tr, line.msg));
</script>

<div class="line" class:tone-error={tone === 'error'}>
  {#if dot}<span class="dot {tone}" aria-hidden="true"></span>{/if}
  <span class="text">{text}</span>
</div>

<style>
  .line {
    display: flex;
    align-items: baseline;
    gap: 8px;
    min-width: 0;
  }
  .dot {
    transform: translateY(-1px);
  }
  .text {
    overflow-wrap: anywhere;
  }
</style>
