<script lang="ts">
  // The planned search of the SL planner (docs: sl-planner/beam §7) on the same made-up fight as the beam's figure:
  // passes of width 1, 2, 4, ..., each to the fight's end, each keeping the best states by the leaf. A turn searched in
  // an earlier pass costs nothing again. The pass stops on a proof, on a win at the minimum width, or when the fight's
  // nodes are spent.
  import { scaleLinear } from 'd3';
  import type { Lang } from '../v/names.ts';
  import { CHILDREN, children, root, type Node } from './madeUpFight.ts';
  import { fmt, say, w, words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));

  type Dot = { n: Node; mark: 'kept' | 'dropped' | 'end' };
  type Pass = { width: number; nodes: number; total: number; dots: Dot[]; best: Node | null; complete: boolean; cut: boolean };
  type Stop = 'proved' | 'won' | 'spent';

  let budget = $state(600);
  let least = $state(4);

  function search(budget: number, least: number) {
    const searched = new Set<string>();
    const passes: Pass[] = [];
    let best: Node | null = null;
    let total = 0;
    for (let width = 1; ; width *= 2) {
      const dots: Dot[] = [];
      let states = [root];
      let complete = true;
      let cut = false;
      let nodes = 0;
      for (let turn = 0; states.length && turn < 40 && !cut; turn++) {
        const next: Node[] = [];
        for (const s of states) {
          if (!searched.has(s.id)) {
            if (total + nodes + CHILDREN > budget) {
              cut = true;
              break;
            }
            searched.add(s.id);
            nodes += CHILDREN;
          }
          for (const c of children(s)) {
            if (c.status !== 'going') {
              dots.push({ n: c, mark: 'end' });
              if (!best || c.value > best.value) best = c;
            } else next.push(c);
          }
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
      total += nodes;
      passes.push({ width, nodes, total, dots, best, complete: complete && !cut, cut });
      const proved = complete && !cut;
      const stop: Stop | null = proved ? 'proved' : cut ? 'spent' : best?.status === 'won' && width >= least ? 'won' : null;
      if (stop) return { passes, best, stop };
    }
  }

  const found = $derived(search(budget, least));
  let chosen = $state(-1);
  const pass = $derived(found.passes[chosen < 0 || chosen >= found.passes.length ? found.passes.length - 1 : chosen]);

  const turns = $derived(Math.max(1, ...found.passes.flatMap((p) => p.dots.map((d) => d.n.turn))));
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
    budget: w('the fight’s nodes', '这场战斗的节点数', '這場戰鬥的節點數'),
    least: w('minimum width', '最小宽度', '最小寬度'),
    pass: w('pass', '轮', '輪'),
    width: w('width', '宽度', '寬度'),
    newNodes: w('new nodes', '新节点', '新節點'),
    total: w('in all', '累计', '累計'),
    bestSoFar: w('best line', '最佳线', '最佳線'),
    kept: w('kept', '保留', '保留'),
    dropped: w('beyond the width', '超出宽度', '超出寬度'),
    end: w('fight over', '战斗结束', '戰鬥結束'),
    line: w('the best line after this pass', '这一轮后的最佳线', '這一輪後的最佳線'),
    yAxis: w('leaf value', '叶子估值', '葉節點估值'),
    xAxis: w('turn', '回合', '回合'),
    wins: w('wins with', '获胜，剩', '獲勝，剩'),
    loses: w('loses', '输', '輸'),
    none: w('none yet', '还没有', '還沒有'),
    cut: w('(nodes spent)', '（节点用完）', '（節點用完）'),
    stop: w('Stops', '停止', '停止'),
    proved: w('proved: nothing was left out', '已证明：没有遗漏', '已證明：沒有遺漏'),
    won: w('won at the minimum width', '在最小宽度上获胜', '在最小寬度上獲勝'),
    spent: w('the fight’s nodes are spent', '这场战斗的节点已用完', '這場戰鬥的節點已用完'),
    planned: w('Planned design, not running code: the fight is made up, and the numbers are no measurement.', '规划中的设计，不是运行中的程序：战斗是虚构的，数字不是测量结果。', '規劃中的設計，不是執行中的程式：戰鬥是虛構的，數字不是測量結果。'),
  };
  const COLORS = { kept: 'var(--v-s1)', dropped: 'var(--v-muted)', end: 'var(--v-s3)' } as const;
  const lineText = (n: Node | null) => (!n ? say(lang, L.none) : n.status === 'won' ? `${say(lang, L.wins)} ${n.h} HP` : say(lang, L.loses));
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.5rem;">
    <label>{say(lang, L.budget)} {budget} <input type="range" min="60" max="3000" step="60" bind:value={budget} /></label>
    <label>{say(lang, L.least)} {least} <input type="range" min="1" max="8" bind:value={least} /></label>
  </div>
  <svg viewBox="0 0 {CW} {CH}" style="width: 100%; max-width: {CW}px;" role="img" aria-label={say(lang, L.line)}>
    {#each [0, 0.2, 0.4, 0.6] as v (v)}
      <line x1="42" x2={CW - 12} y1={y(v)} y2={y(v)} stroke="var(--v-grid)" />
      <text x="36" y={y(v) + 4} text-anchor="end">{fmt(lang, v, 1)}</text>
    {/each}
    {#each Array.from({ length: turns + 1 }, (_, i) => i) as i (i)}
      <text x={x(i)} y={CH - 12} text-anchor="middle">{i}</text>
    {/each}
    <text x={CW - 12} y={CH - 32} text-anchor="end">{say(lang, L.xAxis)}</text>
    <text x="44" y="12">{say(lang, L.yAxis)}</text>
    {#each pass.dots as d (d.n.id)}
      <circle cx={x(d.n.turn)} cy={y(d.n.value)} r={d.mark === 'end' ? 4 : 3} fill={d.mark === 'dropped' ? 'none' : COLORS[d.mark]}
        stroke={COLORS[d.mark]} opacity={d.mark === 'dropped' ? 0.6 : 0.85} />
    {/each}
    {#if pass.best}
      <path d={path(lineOf(pass.best))} fill="none" stroke="var(--v-ink)" stroke-width="1.5" />
    {/if}
  </svg>
  <div class="v-row" style="font-size: 0.75rem;">
    <span><span class="v-key" style="background: {COLORS.kept}"></span> {say(lang, L.kept)}</span>
    <span><span class="v-key" style="border: 1px solid {COLORS.dropped}"></span> {say(lang, L.dropped)}</span>
    <span><span class="v-key" style="background: {COLORS.end}"></span> {say(lang, L.end)}</span>
    <span><span class="v-line-key" style="background: var(--v-ink)"></span> {say(lang, L.line)}</span>
  </div>
  <table style="font-size: 0.8rem; margin-top: 0.5rem;">
    <thead>
      <tr><th>{say(lang, L.pass)}</th><th>{say(lang, L.width)}</th><th>{say(lang, L.newNodes)}</th><th>{say(lang, L.total)}</th><th>{say(lang, L.bestSoFar)}</th></tr>
    </thead>
    <tbody>
      {#each found.passes as p, i (p.width)}
        <tr style={p === pass ? 'font-weight: 600;' : ''}>
          <td><button type="button" onclick={() => (chosen = i)}>{i + 1}</button></td>
          <td class="v-num">{p.width}</td>
          <td class="v-num">{p.nodes}</td>
          <td class="v-num">{p.total}</td>
          <td>{lineText(p.best)} {p.cut ? say(lang, L.cut) : ''}</td>
        </tr>
      {/each}
    </tbody>
  </table>
  <div class="v-row" style="margin-top: 0.5rem;">
    <div class="v-stat"><span class="v-label">{say(lang, L.stop)}</span><span class="v-value">{say(lang, L[found.stop])}</span></div>
    <div class="v-stat"><span class="v-label">{t('nodes')}</span><span class="v-value v-num">{found.passes[found.passes.length - 1].total}</span></div>
  </div>
  <p class="v-caption">{say(lang, L.planned)}</p>
</div>
