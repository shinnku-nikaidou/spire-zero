<script lang="ts">
  // One sample's hazard labels and loss (learn/v/net.py's hazard_loss): from the decision's floor s, each floor the
  // run lived through is a 0, the floor it died on a 1, and the floors after its end, or after a stop, no label. The
  // hazards are V's own for the picked state (docs: macro-brain/value §2).
  import { onMount } from 'svelte';
  import type { V } from '../../lib/v/net.ts';
  import { loadV } from '../../lib/v/load.ts';
  import Picker from './Picker.svelte';
  import { fmt, words } from './i18n.ts';
  import type { Lang } from './names.ts';
  import { states, type Picked } from './states.ts';

  let { lang = 'en', start = 'test-4' }: { lang?: Lang; start?: string } = $props();
  const t = $derived(words(lang));
  let v = $state.raw<V | null>(null);
  let choices = $state.raw<Picked[]>([]);
  let picked = $state(start);
  let kind = $state<'died' | 'won' | 'censored'>('died');
  let endAt = $state(33);

  onMount(async () => {
    const loaded = await loadV();
    v = loaded.v;
    choices = states(lang, loaded.meta);
  });

  const LAST = 52;
  const view = $derived(choices.find((c) => c.id === picked)?.view ?? null);
  const s = $derived(view ? view.floor : 0);
  const e = $derived(Math.max(s, Math.min(endAt, LAST)));
  const cells = $derived.by(() => {
    if (!v || !view) return [];
    const h = v.trace(view).hazards;
    return Array.from({ length: LAST + 1 }, (_, g) => {
      const label = g < s ? null : kind === 'won' ? 0 : g < e ? 0 : g === e && kind === 'died' ? 1 : null;
      const loss = label === null ? 0 : label === 1 ? -Math.log(h[g]) : -Math.log(1 - h[g]);
      return { g, label, loss, h: h[g] };
    });
  });
  const total = $derived(cells.reduce((a, c) => a + c.loss, 0));
  const maxLoss = $derived(Math.max(0.5, ...cells.map((c) => c.loss)));
  const CW = 11;
</script>

<div class="vfig">
  {#if !v || !view}
    <p class="v-muted">{t('loading')}</p>
  {:else}
    <div class="v-row" style="margin-bottom: 0.5rem;">
      <Picker states={choices} value={picked} label={t('state')} onpick={(id) => (picked = id)} />
    </div>
    <div class="v-row" style="margin-bottom: 0.75rem;">
      <div class="v-tabs" style="margin: 0;">
        <button aria-pressed={kind === 'died'} onclick={() => (kind = 'died')}>{t('died')}</button>
        <button aria-pressed={kind === 'won'} onclick={() => (kind = 'won')}>{t('won')}</button>
        <button aria-pressed={kind === 'censored'} onclick={() => (kind = 'censored')}>{t('censored')}</button>
      </div>
      {#if kind !== 'won'}
        <label><span class="v-ink2">e = {e}</span><input type="range" min={s} max={LAST} bind:value={endAt} /></label>
      {/if}
    </div>
    <div class="v-scroll">
      <svg width={(LAST + 1) * CW + 40} height="96" role="img" aria-label={t('lossTotal')}>
        {#each cells as c (c.g)}
          <rect x={c.g * CW} y="0" width={CW - 2} height="16" rx="2"
            fill={c.label === null ? 'var(--v-grid)' : c.label === 1 ? 'var(--v-s2)' : 'var(--v-s1)'}>
            <title>{t('floor')} {c.g}: {c.label === null ? t('noLabel') : c.label === 1 ? t('diedHere') : t('survived')} · h = {fmt(lang, c.h, 3)} · {t('lossTerm')} {fmt(lang, c.loss, 3)}</title>
          </rect>
          {#if c.loss > 0}
            <rect x={c.g * CW} y={22 + 50 * (1 - c.loss / maxLoss)} width={CW - 2} height={(50 * c.loss) / maxLoss} rx="1.5" fill="var(--v-ink-2)" />
          {/if}
          {#if c.g % 8 === 0 || c.g === s}
            <text x={c.g * CW + (CW - 2) / 2} y="90" text-anchor="middle">{c.g === s ? 's' : c.g}</text>
          {/if}
        {/each}
        <line x1="0" x2={(LAST + 1) * CW} y1="72" y2="72" stroke="var(--v-axis)" />
      </svg>
    </div>
    <div class="v-row v-ink2" style="font-size: 0.75rem;">
      <span><span class="v-key" style="background: var(--v-s1)"></span> {t('survived')} (0)</span>
      <span><span class="v-key" style="background: var(--v-s2)"></span> {t('diedHere')} (1)</span>
      <span><span class="v-key" style="background: var(--v-grid)"></span> {t('noLabel')}</span>
      <span><span class="v-key" style="background: var(--v-ink-2)"></span> {t('lossTerm')}</span>
    </div>
    <p style="margin-top: 0.5rem;">{t('lossTotal')}: <strong class="v-num">{fmt(lang, total, 3)}</strong></p>
  {/if}
</div>
