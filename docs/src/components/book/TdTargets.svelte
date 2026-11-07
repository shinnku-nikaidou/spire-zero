<script lang="ts">
  // TD(λ) targets on the book's example run (docs: learning/loop §4): its decisions outside combat with the champion's V
  // of the run view at each (src/data/book/learning-td.json), and each decision's λ-return (./td.ts) for the λ the reader
  // picks. V's value is the chance to win; the run was won, so its outcome is 1.
  import { scaleLinear } from 'd3';
  import type { Lang } from '../v/names.ts';
  import { fmt, surface } from '../v/i18n.ts';
  import { w, say } from './words.ts';
  import { lambdaReturns } from './td.ts';
  import data from '../../data/book/learning-td.json';

  let { lang = 'en' }: { lang?: Lang } = $props();

  const series = data.series;
  const values = series.map((s) => s.v);
  const G = data.outcome;
  const N = series.length;
  let lambda = $state(0.9);
  const targets = $derived(lambdaReturns(values, G, lambda));
  const gap = $derived(targets.reduce((a, t, k) => a + (t - values[k]), 0) / N);
  const gapEarly = $derived(targets.slice(0, 60).reduce((a, t, k) => a + (t - values[k]), 0) / 60);
  const horizon = $derived(lambda >= 0.999 ? null : 1 / (1 - lambda));

  const W = 640;
  const H = 240;
  const M = { l: 40, r: 40, t: 12, b: 26 };
  const x = scaleLinear().domain([0, N - 1]).range([M.l, W - M.r]);
  const y = scaleLinear().domain([0, 1]).range([H - M.b, M.t]);
  const vLine = values.map((v, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join('');
  const tLine = $derived(targets.map((t, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)},${y(t).toFixed(1)}`).join(''));
  const actStarts = series.map((s, i) => (i > 0 && s.act !== series[i - 1].act ? i : -1)).filter((i) => i >= 0);
  const PRESETS = [0, 0.5, 0.9, 0.95, 1];

  let hover = $state<number | null>(null);
  let svg: SVGSVGElement;
  function move(e: PointerEvent) {
    const r = svg.getBoundingClientRect();
    hover = Math.max(0, Math.min(N - 1, Math.round(x.invert(((e.clientX - r.left) / r.width) * W))));
  }
</script>

<div class="vfig" style="position: relative;">
  <div class="v-row">
    <label>
      λ
      <input type="range" min="0" max="1" step="0.01" bind:value={lambda} aria-label="lambda" />
      <span class="v-num">{fmt(lang, lambda, 2)}</span>
    </label>
    <div class="v-tabs" style="margin: 0;">
      {#each PRESETS as p (p)}
        <button aria-pressed={Math.abs(lambda - p) < 1e-9} onclick={() => (lambda = p)}>{p === 1 ? say(lang, w('1 (MC)', '1（MC）', '1（MC）')) : p === 0 ? say(lang, w('0 (TD(0))', '0（TD(0)）', '0（TD(0)）')) : String(p)}</button>
      {/each}
    </div>
  </div>
  <svg bind:this={svg} viewBox="0 0 {W} {H}" width="100%" role="img" aria-label="TD(λ)" onpointermove={move} onpointerleave={() => (hover = null)} style="margin-top: 0.5rem;">
    {#each [0, 0.25, 0.5, 0.75, 1] as v (v)}
      <line x1={M.l} x2={W - M.r} y1={y(v)} y2={y(v)} stroke="var(--v-grid)" />
      <text x={M.l - 6} y={y(v) + 4} text-anchor="end">{fmt(lang, v, 2)}</text>
    {/each}
    {#each actStarts as i, a (a)}
      <line x1={x(i)} x2={x(i)} y1={M.t} y2={H - M.b} stroke="var(--v-axis)" />
      <text x={x(i) + 4} y={H - 8}>{say(lang, w('act', '第', '第'))} {a + 2}{say(lang, w('', ' 幕', ' 幕'))}</text>
    {/each}
    <text x={M.l} y={H - 8}>{say(lang, w('decision', '决策', '決策'))} 0 → {N - 1}</text>
    <line x1={M.l} x2={W - M.r} y1={y(G)} y2={y(G)} stroke="var(--v-ink-2)" stroke-dasharray="4 3" />
    <text x={W - M.r + 4} y={y(G) + 4}>G {G}</text>
    <path d={vLine} fill="none" stroke="var(--v-s1)" stroke-width="2" stroke-linejoin="round" />
    <path d={tLine} fill="none" stroke="var(--v-s2)" stroke-width="1.5" stroke-linejoin="round" />
    {#each targets as t, i (i)}
      <circle cx={x(i)} cy={y(t)} r="1.8" fill="var(--v-s2)" />
    {/each}
    <text x={W - M.r + 4} y={y(values[N - 1]) + 16}>V</text>
    {#if hover !== null}
      <line x1={x(hover)} x2={x(hover)} y1={M.t} y2={H - M.b} stroke="var(--v-ink-2)" />
      <circle cx={x(hover)} cy={y(values[hover])} r="4" fill="var(--v-s1)" stroke="var(--v-surface)" stroke-width="2" />
      <circle cx={x(hover)} cy={y(targets[hover])} r="4" fill="var(--v-s2)" stroke="var(--v-surface)" stroke-width="2" />
    {/if}
  </svg>
  {#if hover !== null}
    {@const s = series[hover]}
    <div class="v-tip v-num" style:left="{Math.min(66, (x(hover) / W) * 100)}%" style:top="3rem">
      <strong>{say(lang, w('decision', '决策', '決策'))} {hover}</strong> · {surface(lang, s.s)} · {say(lang, w('floor', '楼层', '樓層'))} {s.floor} · HP {s.hp}/{s.max_hp}<br />
      V {fmt(lang, values[hover], 3)} · {say(lang, w('target', '目标', '目標'))} {fmt(lang, targets[hover], 3)}
    </div>
  {/if}
  <div class="v-row" style="margin-top: 0.5rem;">
    <span><span class="v-line-key" style="background: var(--v-s1);"></span> {say(lang, w('V’s value at each decision', '每个决策处 V 的值', '每個決策處 V 的值'))}</span>
    <span><span class="v-line-key" style="background: var(--v-s2);"></span> {say(lang, w('its TD(λ) target', '它的 TD(λ) 目标', '它的 TD(λ) 目標'))}</span>
    <span><span class="v-line-key" style="background: var(--v-ink-2);"></span> {say(lang, w('the outcome G (the MC target)', '结果 G（MC 目标）', '結果 G（MC 目標）'))}</span>
  </div>
  <div class="v-row" style="margin-top: 0.5rem;">
    <div class="v-stat"><span class="v-label">{say(lang, w('looks ahead about', '大约往后看', '大約往後看'))}</span><span class="v-value v-num">{horizon === null ? '∞' : fmt(lang, horizon, 1)}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, w('first decision’s target', '第一个决策的目标', '第一個決策的目標'))}</span><span class="v-value v-num">{fmt(lang, targets[0], 3)}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, w('target − V, first 60 decisions', '目标 − V，前 60 个决策', '目標 − V，前 60 個決策'))}</span><span class="v-value v-num">{fmt(lang, gapEarly, 3)}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, w(`target − V, all ${N}`, `目标 − V，全部 ${N} 个`, `目標 − V，全部 ${N} 個`))}</span><span class="v-value v-num">{fmt(lang, gap, 3)}</span></div>
  </div>
  <p class="v-caption">
    {say(lang, w(
      `The whole SL run on seed ${data.source.seed}, won on floor ${data.floor}, at its ${N} decisions outside combat that have a run view. V is the champion’s, and its value is the chance to win as V reads the run view at each decision; the run was won, so its outcome G is 1. Each target is G^λ_k = (1 − λ) V_{k+1} + λ G^λ_{k+1}, with G^λ for the last decision = G. Not used in training: V’s labels are Monte Carlo.`,
      `种子 ${data.source.seed} 上的一整局 SL，在第 ${data.floor} 层获胜，取它在战斗之外、带对局视图的 ${N} 个决策。V 是冠军的，它的值是 V 读每个决策处的对局视图给出的获胜机率；这一局赢了，所以结果 G 是 1。每个目标是 G^λ_k = (1 − λ) V_{k+1} + λ G^λ_{k+1}，最后一个决策的 G^λ = G。训练中没有用到：V 的标签是蒙特卡洛的。`,
      `種子 ${data.source.seed} 上的一整局 SL，在第 ${data.floor} 層獲勝，取它在戰鬥之外、帶對局視圖的 ${N} 個決策。V 是冠軍的，它的值是 V 讀每個決策處的對局視圖給出的獲勝機率；這一局贏了，所以結果 G 是 1。每個目標是 G^λ_k = (1 − λ) V_{k+1} + λ G^λ_{k+1}，最後一個決策的 G^λ = G。訓練中沒有用到：V 的標籤是蒙地卡羅的。`,
    ))}
  </p>
</div>
