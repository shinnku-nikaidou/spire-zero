<script lang="ts">
  // Four real decisions of a run v6-3-2 won: each option's afterstate valued by V in the browser, as the gap to the
  // best option (docs: macro-brain/afterstates). With `explore`, also each option's chance to be played by a softmax
  // and by an even draw (docs: macro-brain/selfplay).
  import { onMount } from 'svelte';
  import { scaleLinear } from 'd3';
  import { merged } from '../../lib/v/encode.ts';
  import type { V } from '../../lib/v/net.ts';
  import { loadV } from '../../lib/v/load.ts';
  import { describe } from './describe.ts';
  import { fmt, surface, words } from './i18n.ts';
  import { loadNames, type Lang, type Names } from './names.ts';
  import { DECISIONS, EXAMPLES } from './states.ts';

  let { lang = 'en', explore = false, start = 'rest' }: { lang?: Lang; explore?: boolean; start?: (typeof DECISIONS)[number] } = $props();
  const t = $derived(words(lang));
  let v = $state.raw<V | null>(null);
  let names = $state.raw<Names | null>(null);
  let which = $state<(typeof DECISIONS)[number]>(start);
  let logT = $state(Math.log10(0.006)); // the softmax's temperature, in floors (n2-b0.03's self-play: 0.006)
  let eps = $state(0.15);

  onMount(async () => {
    const [loaded, n] = await Promise.all([loadV(), loadNames(lang)]);
    v = loaded.v;
    names = n;
  });

  const d = $derived(EXAMPLES.decisions[which]);
  const rows = $derived.by(() => {
    if (!v || !names) return [];
    const before = d.run_view;
    const valued = d.options.map((o) => {
      const after = merged(before, o.after);
      return { action: o.action, text: describe(lang, names!, o.action, before, after), value: v!.of(after) };
    });
    const best = Math.max(...valued.map((o) => o.value));
    const T = 10 ** logT;
    const weights = valued.map((o) => Math.exp((o.value - best) / T));
    const total = weights.reduce((a, b) => a + b, 0);
    const bestIndex = valued.findIndex((o) => o.value === best);
    return valued.map((o, i) => ({
      ...o,
      gap: o.value - best,
      softmax: weights[i] / total,
      even: (1 - eps) * (i === bestIndex ? 1 : 0) + eps / valued.length,
      best: i === bestIndex,
      played: o.action === d.chosen,
    }));
  });
  const W = 220;
  const lo = $derived(Math.min(-0.2, ...rows.map((r) => r.gap)));
  const x = $derived(scaleLinear().domain([lo, 0]).range([8, W - 8]).nice());
</script>

<div class="vfig">
  <div class="v-tabs">
    {#each DECISIONS as k (k)}
      <button aria-pressed={which === k} onclick={() => (which = k)}>{surface(lang, k)}</button>
    {/each}
  </div>
  <p class="v-ink2" style="margin-bottom: 0.5rem;">
    {t('floor')} {d.run_view.floor} · HP {d.run_view.hp}/{d.run_view.max_hp} · {t('gold')} {d.run_view.gold} · {t('deck')} {d.run_view.deck.length}
  </p>
  {#if !v || !names}
    <p class="v-muted">{t('loading')}</p>
  {:else}
    {#if explore}
      <div class="v-row" style="margin-bottom: 0.75rem;">
        <label><span class="v-key" style="background: var(--v-s1)"></span> {t('softmax')}, T = {fmt(lang, 10 ** logT, 3)}
          <input type="range" min="-3" max="0" step="0.05" bind:value={logT} /></label>
        <label><span class="v-key" style="background: var(--v-s2)"></span> {t('even')}, ε = {fmt(lang, eps, 2)}
          <input type="range" min="0" max="1" step="0.05" bind:value={eps} /></label>
      </div>
    {/if}
    <div class="v-scroll">
      <table class="opts">
        <thead>
          <tr>
            <th></th>
            <th class="v-num">V</th>
            <th>{explore ? t('chance') : t('belowBest')}</th>
          </tr>
        </thead>
        <tbody>
          {#each rows as r (r.action)}
            <tr>
              <td class="desc">
                {r.text}
                {#if r.played}<span class="tag">{t('chosen')}</span>{/if}
                {#if r.best}<span class="tag">{t('best')}</span>{/if}
              </td>
              <td class="v-num">{fmt(lang, r.value, 2)}</td>
              <td>
                {#if explore}
                  <svg width={W} height="22" role="img" aria-label="{fmt(lang, 100 * r.softmax, 1)}% / {fmt(lang, 100 * r.even, 1)}%">
                    <rect x="0" y="2" height="8" rx="2" width={Math.max(0.5, r.softmax * W)} fill="var(--v-s1)"><title>{fmt(lang, 100 * r.softmax, 1)}%</title></rect>
                    <rect x="0" y="12" height="8" rx="2" width={Math.max(0.5, r.even * W)} fill="var(--v-s2)"><title>{fmt(lang, 100 * r.even, 1)}%</title></rect>
                  </svg>
                {:else}
                  <svg width={W} height="16" role="img" aria-label={fmt(lang, r.gap, 3)}>
                    <line x1={x(0)} x2={x(0)} y1="0" y2="16" stroke="var(--v-axis)" />
                    <line x1={x(r.gap)} x2={x(0)} y1="8" y2="8" stroke="var(--v-grid)" stroke-width="2" />
                    <circle cx={x(r.gap)} cy="8" r="4.5" fill="var(--v-s1)" stroke="var(--v-surface)" stroke-width="2"><title>{fmt(lang, r.gap, 3)}</title></circle>
                  </svg>
                {/if}
              </td>
            </tr>
          {/each}
        </tbody>
        {#if !explore}
          <tfoot>
            <tr>
              <td></td><td></td>
              <td>
                <svg width={W} height="16">
                  {#each x.ticks(4) as tick (tick)}
                    <text x={x(tick)} y="12" text-anchor="middle">{fmt(lang, tick === 0 ? 0 : tick, 2)}</text>
                  {/each}
                </svg>
              </td>
            </tr>
          </tfoot>
        {/if}
      </table>
    </div>
  {/if}
</div>

<style>
  .opts {
    border-collapse: collapse;
    width: max-content;
    margin: 0;
  }
  .opts th,
  .opts td {
    border: none;
    padding: 0.1rem 0.5rem 0.1rem 0;
    vertical-align: middle;
    background: none;
  }
  .opts th {
    color: var(--v-muted);
    font-weight: normal;
    font-size: 0.75rem;
    text-align: left;
  }
  .desc {
    max-width: 17rem;
  }
  .tag {
    font-size: 0.7rem;
    border: 1px solid var(--v-axis);
    border-radius: 0.25rem;
    padding: 0 0.3rem;
    margin-left: 0.3rem;
    color: var(--v-ink-2);
    white-space: nowrap;
  }
</style>
