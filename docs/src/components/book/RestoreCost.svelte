<script lang="ts">
  // What a restore costs by how deep it replays (docs: simulator/combat §2), from the measured models: a NoSL combat
  // world and the SL world on the node's clone.
  import { scaleLinear } from 'd3';
  import type { Lang } from '../v/names.ts';
  import { fmt } from '../v/i18n.ts';
  import { w, say } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();

  const NOSL = { base: 0.28, per: 0.073 };
  const SL = { base: 0.5, per: 0.1 };
  const MAX = 200;
  let n = $state(16);
  const cost = (m: { base: number; per: number }, k: number) => m.base + m.per * k;

  const W = 640;
  const H = 210;
  const M = { l: 44, r: 16, t: 12, b: 34 };
  const x = scaleLinear().domain([0, MAX]).range([M.l, W - M.r]);
  const y = scaleLinear().domain([0, cost(SL, MAX)]).range([H - M.b, M.t]);
  const line = (m: { base: number; per: number }) => `M${x(0)},${y(cost(m, 0))}L${x(MAX)},${y(cost(m, MAX))}`;
  const ROOT = 15.8; // the measured mean root depth
</script>

<div class="vfig">
  <svg viewBox="0 0 {W} {H}" width="100%" role="img" aria-label="restore cost">
    {#each [0, 5, 10, 15, 20] as v (v)}
      <line x1={M.l} x2={W - M.r} y1={y(v)} y2={y(v)} stroke="var(--v-grid)" />
      <text x={M.l - 6} y={y(v) + 4} text-anchor="end">{v}</text>
    {/each}
    {#each [0, 50, 100, 150, 200] as v (v)}
      <text x={x(v)} y={H - M.b + 16} text-anchor="middle">{v}</text>
    {/each}
    <text x={M.l} y={H - 4}>{say(lang, w('steps replayed', '重播的步数', '重播的步數'))}</text>
    <text x={M.l + 4} y={M.t + 10}>ms</text>
    <line x1={x(ROOT)} x2={x(ROOT)} y1={M.t} y2={H - M.b} stroke="var(--v-axis)" stroke-dasharray="3 3" />
    <text x={x(ROOT) + 4} y={M.t + 24}>{say(lang, w('mean search depth', '平均搜索深度', '平均搜尋深度'))}</text>
    <path d={line(NOSL)} stroke="var(--v-s1)" stroke-width="2" fill="none" />
    <path d={line(SL)} stroke="var(--v-s2)" stroke-width="2" fill="none" />
    <line x1={x(n)} x2={x(n)} y1={M.t} y2={H - M.b} stroke="var(--v-ink-2)" />
    <circle cx={x(n)} cy={y(cost(NOSL, n))} r="4" fill="var(--v-s1)" />
    <circle cx={x(n)} cy={y(cost(SL, n))} r="4" fill="var(--v-s2)" />
  </svg>
  <div class="v-row">
    <label>
      {say(lang, w('Restore after', '还原到第', '還原到第'))}
      <input type="range" min="0" max={MAX} step="1" bind:value={n} />
      <span class="v-num">{n}</span> {say(lang, w('steps', '步之后', '步之後'))}
    </label>
  </div>
  <div class="v-row" style="margin-top: 0.5rem;">
    <div class="v-stat"><span class="v-label"><span class="v-line-key" style="background: var(--v-s1);"></span> {say(lang, w('NoSL world: new combat + replay', 'NoSL 世界：新开战斗＋重播', 'NoSL 世界：新開戰鬥＋重播'))}</span><span class="v-value v-num">{fmt(lang, cost(NOSL, n), 2)} ms</span></div>
    <div class="v-stat"><span class="v-label"><span class="v-line-key" style="background: var(--v-s2);"></span> {say(lang, w('SL world: load the clone + replay', 'SL 世界：载入克隆＋重播', 'SL 世界：載入複本＋重播'))}</span><span class="v-value v-num">{fmt(lang, cost(SL, n), 2)} ms</span></div>
  </div>
  <p class="v-caption">
    {say(lang, w(
      'Measured models: a NoSL combat world restores in 0.28 ms + 0.073 ms per replayed action; the SL world loads the node’s checkpoint in about 0.5 ms and replays about 0.1 ms per step since the node’s entry, so late in a long fight it takes 10–20 ms.',
      '实测模型：NoSL 战斗世界还原要 0.28 ms + 每个重播动作 0.073 ms；SL 世界载入节点的检查点约 0.5 ms，再按节点开始以来的每一步约 0.1 ms 重播，所以长战斗的后段要 10–20 ms。',
      '實測模型：NoSL 戰鬥世界還原要 0.28 ms + 每個重播動作 0.073 ms；SL 世界載入節點的檢查點約 0.5 ms，再按節點開始以來的每一步約 0.1 ms 重播，所以長戰鬥的後段要 10–20 ms。',
    ))}
  </p>
</div>
