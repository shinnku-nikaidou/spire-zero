<script lang="ts">
  // Combats per second against worker processes (docs: simulator/combat §7): `bench`, 3,000 combats per process,
  // 2026-10-26, on a machine with 8 physical cores.
  import { scaleLinear } from 'd3';
  import type { Lang } from '../v/names.ts';
  import { w, say } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();

  const DATA = [
    { p: 1, c: 275 },
    { p: 4, c: 1060 },
    { p: 8, c: 1860 },
    { p: 12, c: 1879 },
    { p: 16, c: 2019 },
  ];
  const W = 640;
  const H = 230;
  const M = { l: 52, r: 20, t: 14, b: 36 };
  const x = scaleLinear().domain([0, 16]).range([M.l, W - M.r]);
  const y = scaleLinear().domain([0, 2400]).range([H - M.b, M.t]);
  const path = DATA.map((d, i) => `${i ? 'L' : 'M'}${x(d.p)},${y(d.c)}`).join('');
  let hover = $state<number | null>(null);
</script>

<div class="vfig" style="position: relative;">
  <svg viewBox="0 0 {W} {H}" width="100%" role="img" aria-label="scaling">
    {#each [0, 600, 1200, 1800, 2400] as v (v)}
      <line x1={M.l} x2={W - M.r} y1={y(v)} y2={y(v)} stroke="var(--v-grid)" />
      <text x={M.l - 6} y={y(v) + 4} text-anchor="end">{v}</text>
    {/each}
    {#each [1, 4, 8, 12, 16] as v (v)}
      <text x={x(v)} y={H - M.b + 16} text-anchor="middle">{v}</text>
    {/each}
    <text x={M.l} y={H - 4}>{say(lang, w('worker processes', '工作进程数', '工作行程數'))}</text>
    <text x={M.l + 4} y={M.t + 10}>{say(lang, w('combats / s', '每秒战斗数', '每秒戰鬥數'))}</text>
    <line x1={x(8)} x2={x(8)} y1={M.t} y2={H - M.b} stroke="var(--v-axis)" stroke-dasharray="3 3" />
    <text x={x(8) + 4} y={y(2300)}>{say(lang, w('8 physical cores', '8 个物理核心', '8 個實體核心'))}</text>
    <path d={`M${x(1)},${y(275)}L${x(8)},${y(275 * 8)}`} stroke="var(--v-muted)" stroke-dasharray="4 4" fill="none" />
    <text x={x(1.3)} y={y(1700)} fill="var(--v-muted)">{say(lang, w('dashed: one process × N', '虚线：单进程 × N', '虛線：單行程 × N'))}</text>
    <path d={path} stroke="var(--v-s1)" stroke-width="2" fill="none" />
    {#each DATA as d, i (d.p)}
      <circle cx={x(d.p)} cy={y(d.c)} r={hover === i ? 6 : 4} fill="var(--v-s1)" stroke="var(--v-surface)" stroke-width="2" role="presentation" onpointerenter={() => (hover = i)} onpointerleave={() => (hover = null)} />
    {/each}
  </svg>
  {#if hover !== null}
    <div class="v-tip v-num" style:left="{(x(DATA[hover].p) / W) * 100 - 6}%" style:top="1rem">
      <strong>{DATA[hover].c}</strong> {say(lang, w('combats/s with', '每秒战斗，进程数', '每秒戰鬥，行程數'))} {DATA[hover].p}
    </div>
  {/if}
  <p class="v-caption">
    {say(lang, w(
      'bench, 3,000 combats per process, act 1 weak and regular encounters, the play policy, 2026-10-26. Throughput grows almost linearly up to the 8 physical cores; hyperthreading adds about 8% beyond.',
      'bench，每个进程 3,000 场战斗，第 1 幕弱怪与普通遭遇，play 策略，2026-10-26。吞吐量到 8 个物理核心为止几乎线性增长；超线程之后只多约 8%。',
      'bench，每個行程 3,000 場戰鬥，第 1 幕弱怪與普通遭遇，play 策略，2026-10-26。吞吐量到 8 個實體核心為止幾乎線性成長；超執行緒之後只多約 8%。',
    ))}
  </p>
</div>
