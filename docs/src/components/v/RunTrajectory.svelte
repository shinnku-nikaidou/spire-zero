<script lang="ts">
  // V along a run v6-3-2 won: at each of its macro decisions, V's value of the afterstate of the answer played, and
  // the share of max HP held (docs: macro-brain).
  import { scaleLinear } from 'd3';
  import { fmt, surface, words } from './i18n.ts';
  import type { Lang } from './names.ts';
  import { EXAMPLES, DECISIONS } from './states.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));
  const series = EXAMPLES.series.filter((s) => s.v !== null);
  const W = 640;
  const H1 = 200;
  const H2 = 80;
  const M = { l: 40, r: 56, t: 10, b: 22 };
  const x = scaleLinear().domain([0, series.length - 1]).range([M.l, W - M.r]);
  const y = scaleLinear().domain([30, 50]).range([H1 - M.b, M.t]);
  const y2 = scaleLinear().domain([0, 1]).range([H2 - M.b, M.t]);
  const line = series.map((s, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)},${y(s.v).toFixed(1)}`).join('');
  const hpLine = series.map((s, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)},${y2(s.hp / s.max_hp).toFixed(1)}`).join('');
  // Where each act starts, as the floors the decisions were made on.
  const actStarts = [1, 18, 34].map((f) => series.findIndex((s) => s.floor >= f)).filter((i) => i >= 0);
  const marked = DECISIONS.map((k) => ({ k, i: series.findIndex((s) => s.d === EXAMPLES.decisions[k].decision_id) })).filter((m) => m.i >= 0);
  const end = EXAMPLES.source.floor + 1; // a won run's progress: its last floor, its last boss all taken

  let hover = $state<number | null>(null);
  let svg: SVGSVGElement;
  function move(e: PointerEvent) {
    const r = svg.getBoundingClientRect();
    const px = ((e.clientX - r.left) / r.width) * W;
    hover = Math.max(0, Math.min(series.length - 1, Math.round(x.invert(px))));
  }
  const h = $derived(hover === null ? null : series[hover]);
</script>

<div class="vfig" style="position: relative;">
  <svg bind:this={svg} viewBox="0 0 {W} {H1 + H2}" width="100%" role="img" aria-label={t('value')} onpointermove={move} onpointerleave={() => (hover = null)}>
    {#each [30, 35, 40, 45, 50] as v (v)}
      <line x1={M.l} x2={W - M.r} y1={y(v)} y2={y(v)} stroke="var(--v-grid)" />
      <text x={M.l - 6} y={y(v) + 4} text-anchor="end">{v}</text>
    {/each}
    {#each actStarts as i, a (a)}
      <line x1={x(i)} x2={x(i)} y1={M.t} y2={H1 + H2 - M.b} stroke="var(--v-axis)" />
      <text x={x(i) + 4} y={H1 + H2 - 6}>{t('act')} {a + 1}</text>
    {/each}
    <line x1={M.l} x2={W - M.r} y1={y(end)} y2={y(end)} stroke="var(--v-ink-2)" stroke-width="1" />
    <text x={W - M.r + 4} y={y(end) + 4}>{end}</text>
    <path d={line} fill="none" stroke="var(--v-s1)" stroke-width="2" stroke-linejoin="round" />
    {#each marked as m (m.k)}
      <circle cx={x(m.i)} cy={y(series[m.i].v)} r="5" fill="var(--v-s1)" stroke="var(--v-surface)" stroke-width="2" />
      <text x={x(m.i)} y={y(series[m.i].v) - 9} text-anchor="middle">{surface(lang, m.k)}</text>
    {/each}
    <g transform="translate(0,{H1})">
      {#each [0, 0.5, 1] as v (v)}
        <line x1={M.l} x2={W - M.r} y1={y2(v)} y2={y2(v)} stroke="var(--v-grid)" />
        <text x={M.l - 6} y={y2(v) + 4} text-anchor="end">{v * 100}%</text>
      {/each}
      <path d={hpLine} fill="none" stroke="var(--v-s2)" stroke-width="2" stroke-linejoin="round" />
      <text x={W - M.r + 4} y={y2(series[series.length - 1].hp / series[series.length - 1].max_hp) + 4}>HP</text>
    </g>
    <text x={W - M.r + 4} y={y(series[series.length - 1].v) + 14}>V</text>
    {#if hover !== null && h}
      <line x1={x(hover)} x2={x(hover)} y1={M.t} y2={H1 + H2 - M.b} stroke="var(--v-ink-2)" />
      <circle cx={x(hover)} cy={y(h.v)} r="4" fill="var(--v-s1)" stroke="var(--v-surface)" stroke-width="2" />
    {/if}
  </svg>
  {#if hover !== null && h}
    <div class="v-tip v-num" style:left="{Math.min(70, (x(hover) / W) * 100)}%" style:top="0.5rem">
      <strong>{fmt(lang, h.v, 2)}</strong> · {t('floor')} {h.floor} · HP {h.hp}/{h.max_hp}<br />
      <span class="v-muted">{surface(lang, h.s)} · {h.chosen}</span>
    </div>
  {/if}
</div>
