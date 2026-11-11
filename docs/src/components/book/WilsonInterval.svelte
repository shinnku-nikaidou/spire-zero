<script lang="ts">
  // The Wilson 95% interval of a win rate (docs: evaluation/metrics §4), as Rate.Wilson computes it
  // (src/Sts2Ai.Harness/Reports.cs:10-17), against the normal (Wald) interval; presets are example counts, small and
  // none, and FableBot's published result.
  import { scaleLinear } from 'd3';
  import type { Lang } from '../v/names.ts';
  import { fmt } from '../v/i18n.ts';
  import { w, say } from './words.ts';
  import { wilson, wald } from './evalStats.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();

  const PRESETS = [
    { name: '', hits: 23, of: 300 },
    { name: '', hits: 13, of: 300 },
    { name: '', hits: 1, of: 300 },
    { name: '', hits: 0, of: 200 },
    { name: 'FableBot', hits: 71, of: 343 },
  ];
  let hits = $state(23);
  let of = $state(300);
  const n = $derived(Math.max(0, Math.round(of)));
  const k = $derived(Math.min(n, Math.max(0, Math.round(hits))));
  const r = $derived(wilson(k, n));
  const z = $derived(wald(k, n));
  const pct = (x: number) => `${fmt(lang, 100 * x, 1)} %`;
  // The report's own form, Rate.ToString: "7.7 % (23/300, 5.2 %–11.2 %)".
  const asReport = $derived(`${pct(r.value)} (${k}/${n}, ${pct(r.low)}–${pct(r.high)})`);

  const W = 640;
  const H = 150;
  const M = { l: 92, r: 20, t: 18, b: 30 };
  const top = $derived(Math.min(1, Math.max(0.05, Math.ceil(Math.max(r.high, z.high) * 1.25 * 20) / 20)));
  const x = $derived(scaleLinear().domain([Math.min(0, z.low), top]).range([M.l, W - M.r]));
  const ticks = $derived(x.ticks(6));
  const ROWS = [
    { key: 'wilson', y: 52, colour: 'var(--v-s1)', label: w('Wilson', 'Wilson', 'Wilson') },
    { key: 'wald', y: 96, colour: 'var(--v-s2)', label: w('normal approximation', '正态近似', '常態近似') },
  ];
</script>

<div class="vfig">
  <div class="v-tabs">
    {#each PRESETS as p (`${p.hits}/${p.of}`)}
      <button aria-pressed={k === p.hits && n === p.of} onclick={() => ((hits = p.hits), (of = p.of))}>{p.name} {p.hits}/{p.of}</button>
    {/each}
  </div>
  <div class="v-row">
    <label>{say(lang, w('won', '赢了', '贏了'))} <input type="number" min="0" max={n} step="1" bind:value={hits} style="width: 5rem;" /></label>
    <label>{say(lang, w('of runs', '总局数', '總局數'))} <input type="number" min="0" max="100000" step="1" bind:value={of} style="width: 6rem;" /></label>
    <span class="v-muted v-num">{say(lang, w('as a report prints it:', '报告里的写法：', '報告裡的寫法：'))} {asReport}</span>
  </div>
  <svg viewBox="0 0 {W} {H}" width="100%" role="img" aria-label="Wilson interval">
    {#each ticks as t (t)}
      <line x1={x(t)} x2={x(t)} y1={M.t} y2={H - M.b} stroke="var(--v-grid)" />
      <text x={x(t)} y={H - M.b + 16} text-anchor="middle">{fmt(lang, 100 * t, top < 0.2 ? 1 : 0)}%</text>
    {/each}
    {#if z.low < 0}
      <rect x={x(z.low)} y={M.t} width={x(0) - x(z.low)} height={H - M.b - M.t} fill="var(--v-neg)" opacity="0.08" />
      <text x={x(z.low) + 2} y={H - M.b - 4} fill="var(--v-neg)">{say(lang, w('below 0', '低于 0', '低於 0'))}</text>
    {/if}
    <line x1={x(0)} x2={x(0)} y1={M.t} y2={H - M.b} stroke="var(--v-axis)" />
    <line x1={x(r.value)} x2={x(r.value)} y1={M.t} y2={H - M.b} stroke="var(--v-ink-2)" stroke-dasharray="3 3" />
    {#each ROWS as row (row.key)}
      {@const lo = row.key === 'wilson' ? r.low : z.low}
      {@const hi = row.key === 'wilson' ? r.high : z.high}
      <text x={M.l - 8} y={row.y + 4} text-anchor="end" fill="var(--v-ink)" style="font-size: 12px;">{say(lang, row.label)}</text>
      <line x1={x(lo)} x2={x(hi)} y1={row.y} y2={row.y} stroke={row.colour} stroke-width="6" stroke-linecap="round" opacity="0.85" />
      <circle cx={x(r.value)} cy={row.y} r="4" fill="var(--v-ink)" />
      <text x={x(lo)} y={row.y - 9} text-anchor="middle" class="v-num">{pct(lo)}</text>
      <text x={x(hi)} y={row.y - 9} text-anchor="middle" class="v-num">{pct(hi)}</text>
    {/each}
  </svg>
  <p class="v-caption">
    {say(lang, w(
      'The Wilson interval is the one every report and the leaderboard print (z = 1.96). The normal interval p̂ ± 1.96·√(p̂(1−p̂)/n) is shown only to compare: with few wins it reaches below 0, and with none it has no width at all. Presets: example counts of few wins and of none, and FableBot’s 71 of 343 Ironclad runs.',
      '每份报告和排行榜印的都是 Wilson 区间（z = 1.96）。正态区间 p̂ ± 1.96·√(p̂(1−p̂)/n) 只是拿来比较：赢得少时它会跌到 0 以下，一局都没赢时宽度是 0。预设：几个赢得很少和一局没赢的示例计数，以及 FableBot 的 343 局 Ironclad 赢 71 局。',
      '每份報告和排行榜印的都是 Wilson 區間（z = 1.96）。常態區間 p̂ ± 1.96·√(p̂(1−p̂)/n) 只是拿來比較：贏得少時它會跌到 0 以下，一局都沒贏時寬度是 0。預設：幾個贏得很少和一局沒贏的示例計數，以及 FableBot 的 343 局 Ironclad 贏 71 局。',
    ))}
  </p>
</div>
