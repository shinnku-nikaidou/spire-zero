<script lang="ts">
  // The SL planner's beam over turns (docs: sl-planner/beam) on a made-up fight: a greedy dive first, then a beam of
  // B states a turn (E when the dive won), the best by the leaf. The same seeded fight for every setting, so the settings
  // can be compared.
  import { scaleLinear } from 'd3';
  import type { Lang } from '../v/names.ts';
  import { children, root, type Node } from './madeUpFight.ts';
  import { fmt, say, w, words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));

  type Dot = { n: Node; mark: 'kept' | 'dropped' | 'end' };
  // One beam of this width; `best` is the best line that ends the fight so far (the dive's, for the full beam).
  function run(width: number, start: Node | null) {
    let best: Node | null = start;
    const dots: Dot[] = [];
    let states = [root];
    let complete = true;
    let nodes = 0;
    for (let turn = 0; states.length && turn < 40; turn++) {
      const next: Node[] = [];
      for (const s of states)
        for (const c of children(s)) {
          nodes++;
          if (c.status !== 'going') {
            dots.push({ n: c, mark: 'end' });
            if (!best || c.value > best.value) best = c;
          } else next.push(c);
        }
      next.sort((a, b) => b.value - a.value);
      states = [];
      for (const c of next) {
        if (states.length < width) {
          states.push(c);
          dots.push({ n: c, mark: 'kept' });
        } else {
          complete = false;
          dots.push({ n: c, mark: 'dropped' });
        }
      }
    }
    return { best, dots, complete, nodes };
  }

  let beam = $state(8);
  let easy = $state(4);
  let diveFirst = $state(true);
  let shown = $state(99);

  const plan = $derived.by(() => {
    const dive = diveFirst ? run(1, null) : null;
    const width = dive?.best?.status === 'won' && easy > 0 ? easy : beam;
    const full = run(width, dive?.best ?? null);
    return { dive, full, best: full.best, proved: full.complete, width };
  });
  const turns = $derived(Math.max(1, ...plan.full.dots.map((d) => d.n.turn)));
  const lineOf = (n: Node | null) => {
    const pts: Node[] = [];
    for (let m = n; m; m = m.parent) pts.unshift(m);
    return pts;
  };

  const CW = 560;
  const CH = 230;
  const x = $derived(scaleLinear().domain([0, turns]).range([42, CW - 12]));
  const y = scaleLinear().domain([0, 0.75]).range([CH - 28, 10]);
  const path = (pts: Node[]) => pts.map((p, i) => `${i ? 'L' : 'M'}${x(p.turn).toFixed(1)},${y(p.value).toFixed(1)}`).join('');

  const L = {
    beam: w('beam B', '束宽 B', '束寬 B'),
    easy: w('easy beam E', '简易束 E', '簡易束 E'),
    dive: w('greedy dive first', '先做贪心下潜', '先做貪婪下潛'),
    shown: w('turns shown', '显示到第几回合', '顯示到第幾回合'),
    kept: w('kept', '保留', '保留'),
    dropped: w('beyond the width', '超出宽度', '超出寬度'),
    end: w('fight over', '战斗结束', '戰鬥結束'),
    diveLine: w('the dive’s line', '下潜的路线', '下潛的路線'),
    bestLine: w('the pick', '选中的线', '選中的線'),
    yAxis: w('leaf value', '叶子估值', '葉節點估值'),
    xAxis: w('turn', '回合', '回合'),
    result: w('Pick', '结果', '結果'),
    wins: w('wins with', '获胜，剩', '獲勝，剩'),
    loses: w('loses', '输', '輸'),
    proved: w('proved', '已证明', '已證明'),
    notProved: w('not proved', '未证明', '未證明'),
    width: w('the full beam’s width', '完整束的宽度', '完整束的寬度'),
  };
  const COLORS = { kept: 'var(--v-s1)', dropped: 'var(--v-muted)', end: 'var(--v-s3)' } as const;
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.5rem;">
    <label>{say(lang, L.beam)} {beam} <input type="range" min="1" max="16" bind:value={beam} /></label>
    <label>{say(lang, L.easy)} {easy} <input type="range" min="0" max="8" bind:value={easy} /></label>
    <label><input type="checkbox" bind:checked={diveFirst} /> {say(lang, L.dive)}</label>
    <label>{say(lang, L.shown)} <input type="range" min="1" max={turns} bind:value={shown} /></label>
  </div>
  <svg viewBox="0 0 {CW} {CH}" style="width: 100%; max-width: {CW}px;" role="img" aria-label={say(lang, L.result)}>
    {#each [0, 0.2, 0.4, 0.6] as v (v)}
      <line x1="42" x2={CW - 12} y1={y(v)} y2={y(v)} stroke="var(--v-grid)" />
      <text x="36" y={y(v) + 4} text-anchor="end">{fmt(lang, v, 1)}</text>
    {/each}
    {#each Array.from({ length: turns + 1 }, (_, i) => i) as i (i)}
      <text x={x(i)} y={CH - 12} text-anchor="middle">{i}</text>
    {/each}
    <text x={CW - 12} y={CH - 32} text-anchor="end">{say(lang, L.xAxis)}</text>
    <text x="44" y="12">{say(lang, L.yAxis)}</text>
    {#each plan.full.dots as d (d.n.id)}
      {#if d.n.turn <= shown}
        <circle cx={x(d.n.turn)} cy={y(d.n.value)} r={d.mark === 'end' ? 4 : 3} fill={d.mark === 'dropped' ? 'none' : COLORS[d.mark]}
          stroke={COLORS[d.mark]} opacity={d.mark === 'dropped' ? 0.6 : 0.85} />
      {/if}
    {/each}
    {#if plan.dive}
      <path d={path(lineOf(plan.dive.best ?? null).filter((n) => n.turn <= shown))} fill="none" stroke="var(--v-s4)" stroke-width="2" stroke-dasharray="5 3" />
    {/if}
    {#if plan.best && shown >= plan.best.turn}
      <path d={path(lineOf(plan.best))} fill="none" stroke="var(--v-ink)" stroke-width="1.5" />
    {/if}
  </svg>
  <div class="v-row" style="font-size: 0.75rem;">
    <span><span class="v-key" style="background: {COLORS.kept}"></span> {say(lang, L.kept)}</span>
    <span><span class="v-key" style="border: 1px solid {COLORS.dropped}"></span> {say(lang, L.dropped)}</span>
    <span><span class="v-key" style="background: {COLORS.end}"></span> {say(lang, L.end)}</span>
    <span><span class="v-line-key" style="background: var(--v-s4)"></span> {say(lang, L.diveLine)}</span>
    <span><span class="v-line-key" style="background: var(--v-ink)"></span> {say(lang, L.bestLine)}</span>
  </div>
  <div class="v-row" style="margin-top: 0.5rem;">
    <div class="v-stat"><span class="v-label">{say(lang, L.result)}</span>
      <span class="v-value">{plan.best?.status === 'won' ? `${say(lang, L.wins)} ${plan.best.h} HP` : say(lang, L.loses)}</span></div>
    <div class="v-stat"><span class="v-label">{t('nodes')}</span><span class="v-value v-num">{(plan.dive?.nodes ?? 0) + (plan.full?.nodes ?? 0)}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, L.width)}</span><span class="v-value v-num">{plan.width}</span></div>
    <div class="v-stat"><span class="v-label">&nbsp;</span><span class="v-value">{say(lang, plan.proved ? L.proved : L.notProved)}</span></div>
  </div>
  <p class="v-caption">{t('illustrative')}</p>
</div>
