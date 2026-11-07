<script lang="ts">
  // The whole-run gate's SPRT on wins, pair by pair (docs: evaluation/gates §3), with Gate.Sprt's rule (evalStats.ts,
  // src/Sts2Ai.Harness/Gate.cs:51-82): only the pairs where exactly one side won move it. The real tabs replay two pairs of
  // committed SL reports (src/data/book/eval-pairs.json), the later net as the challenger; "Simulate" draws made-up pairs.
  import { scaleLinear } from 'd3';
  import type { Lang } from '../v/names.ts';
  import { fmt } from '../v/i18n.ts';
  import { w, say } from './words.ts';
  import { sprt, type Pair } from './evalStats.ts';
  import data from '../../data/book/eval-pairs.json';

  let { lang = 'en' }: { lang?: Lang } = $props();

  const DELTA = 0.1;
  // The pair sets with a won run on either side: the NoSL one has none, and its gate would stay open.
  const real = data.pairs.filter((p) => p.aWon.length + p.bWon.length > 0);
  const realPairs = (p: (typeof data.pairs)[number]): Pair[] =>
    p.floors.map(([a, b], k) => ({ d: b - a, championWon: p.aWon.includes(k), challengerWon: p.bWon.includes(k) }));

  let tab = $state<number>(0);
  const SIM = -1;

  let n = $state(100);
  let split = $state(0.15);
  let share = $state(0.6);
  let notWorse = $state(false);
  let seed = $state(1);

  function rng(s: number) {
    let a = s >>> 0;
    return () => {
      a = (a + 0x6d2b79f5) >>> 0;
      let t = a;
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }
  // A pair splits (exactly one side won) with the chance set, and then the challenger is the one with its share.
  function draw(s: number, count: number): Pair[] {
    const r = rng(s);
    return Array.from({ length: count }, () => {
      if (r() >= split) return { d: 0, championWon: false, challengerWon: false };
      const mine = r() < share;
      return { d: 0, championWon: !mine, challengerWon: mine };
    });
  }

  const pairs = $derived(tab === SIM ? draw(seed, n) : realPairs(real[tab]));
  const result = $derived(sprt(pairs, DELTA, 0.05, 0.05, tab === SIM && notWorse));

  let oc = $state<{ pass: number; fail: number; open: number; mean: number; runs: number } | null>(null);
  function operating() {
    const runs = 1000;
    let pass = 0, fail = 0, open = 0, decided = 0;
    for (let i = 0; i < runs; i++) {
      const r = sprt(draw(10_000 + i, n), DELTA, 0.05, 0.05, notWorse);
      if (r.verdict === 'pass') pass++;
      else if (r.verdict === 'fail') fail++;
      else open++;
      decided += r.decided;
    }
    oc = { pass, fail, open, mean: decided / runs, runs };
  }
  $effect(() => {
    // A changed setting makes the last batch stale.
    void n, split, share, notWorse;
    oc = null;
  });

  const W = 660;
  const M = { l: 46, r: 14 };
  const H1 = { t: 8, h: 70 };
  const H2 = { t: 110, h: 190 };
  const H = H2.t + H2.h + 34;
  const x = $derived(scaleLinear().domain([0.5, pairs.length + 0.5]).range([M.l, W - M.r]));
  const yd = scaleLinear().domain([-1, 1]).range([H1.t + H1.h, H1.t + 14]);
  const yl = scaleLinear().domain([-4.5, 4.5]).range([H2.t + H2.h, H2.t]);
  const clampL = (v: number) => Math.max(-4.5, Math.min(4.5, v));
  const path = $derived(result.path.map((v, i) => `${i ? 'L' : 'M'}${x(i + 1)},${yl(clampL(v))}`).join(''));
  const bw = $derived(Math.max(1, (W - M.l - M.r) / pairs.length - 0.6));
  const ticks = $derived([1, ...Array.from({ length: 6 }, (_, i) => (i + 1) * 50)].filter((t) => t <= pairs.length));

  let hover = $state<number | null>(null);
  function move(e: PointerEvent) {
    const svg = e.currentTarget as SVGSVGElement;
    const box = svg.getBoundingClientRect();
    const px = ((e.clientX - box.left) / box.width) * W;
    const k = Math.round(x.invert(px));
    hover = k >= 1 && k <= pairs.length ? k : null;
  }
  const VERDICT: Record<string, ReturnType<typeof w>> = {
    pass: w('pass', '通过', '通過'),
    fail: w('fail', '不通过', '不通過'),
    open: w('open', '未定', '未定'),
  };
  const WHO = (p: Pair) =>
    p.challengerWon && !p.championWon
      ? w('won by the challenger alone', '只有挑战者赢', '只有挑戰者贏')
      : p.championWon && !p.challengerWon
        ? w('won by the champion alone', '只有冠军赢', '只有冠軍贏')
        : p.challengerWon
          ? w('won by both', '两边都赢', '兩邊都贏')
          : w('won by neither', '两边都没赢', '兩邊都沒贏');
  const signed = (v: number, digits = 2) => (v >= 0 ? '+' : '−') + fmt(lang, Math.abs(v), digits);
</script>

<div class="vfig" style="position: relative;">
  <div class="v-tabs">
    {#each real as p, i (p.b.label)}
      <button aria-pressed={tab === i} onclick={() => ((tab = i), (hover = null))}>{p.b.label} − {p.a.label}</button>
    {/each}
    <button aria-pressed={tab === SIM} onclick={() => ((tab = SIM), (hover = null))}>{say(lang, w('Simulate', '模拟', '模擬'))}</button>
  </div>
  {#if tab === SIM}
    <div class="v-row" style="margin-bottom: 0.5rem;">
      <label>{say(lang, w('pairs', '配对数', '配對數'))} <input type="range" min="50" max="300" step="50" bind:value={n} /> <span class="v-num">{n}</span></label>
      <label>{say(lang, w('pairs won by one side only', '只有一边赢的配对', '只有一邊贏的配對'))} <input type="range" min="0.02" max="0.5" step="0.01" bind:value={split} /> <span class="v-num">{fmt(lang, 100 * split, 0)}%</span></label>
      <label>{say(lang, w('the challenger’s share of them', '其中挑战者的比例', '其中挑戰者的比例'))} <input type="range" min="0.2" max="0.8" step="0.05" bind:value={share} /> <span class="v-num">{fmt(lang, 100 * share, 0)}%</span></label>
      <label><input type="checkbox" bind:checked={notWorse} /> --not-worse</label>
      <button onclick={() => (seed += 1)}>{say(lang, w('Draw again', '重新抽', '重新抽'))}</button>
      <button onclick={operating}>{say(lang, w('Run 1,000 gates', '跑 1,000 次门槛', '跑 1,000 次門檻'))}</button>
    </div>
  {/if}
  <svg viewBox="0 0 {W} {H}" width="100%" role="img" aria-label="SPRT" onpointermove={move} onpointerleave={() => (hover = null)}>
    <text x={M.l} y={H1.t + 10} fill="var(--v-ink)" style="font-size: 12px;">{say(lang, w('pairs won by one side only: the challenger up, the champion down', '只有一边赢的配对：挑战者向上，冠军向下', '只有一邊贏的配對：挑戰者向上，冠軍向下'))}</text>
    <line x1={M.l} x2={W - M.r} y1={yd(0)} y2={yd(0)} stroke="var(--v-axis)" />
    {#each pairs as p, i (i)}
      {#if p.challengerWon !== p.championWon}
        <rect
          x={x(i + 1) - bw / 2}
          width={bw}
          y={p.challengerWon ? yd(1) : yd(0)}
          height={Math.abs(yd(1) - yd(0))}
          fill={i + 1 > result.decided ? 'var(--v-grid)' : p.challengerWon ? 'var(--v-s1)' : 'var(--v-s2)'}
        />
      {/if}
    {/each}

    <text x={M.l} y={H2.t - 8} fill="var(--v-ink)" style="font-size: 12px;">{say(lang, w('log-likelihood ratio after each pair', '每对之后的对数似然比', '每對之後的對數概似比'))}</text>
    <rect x={M.l} y={yl(4.5)} width={W - M.l - M.r} height={yl(result.upper) - yl(4.5)} fill="var(--v-pos)" opacity="0.08" />
    <rect x={M.l} y={yl(result.lower)} width={W - M.l - M.r} height={yl(-4.5) - yl(result.lower)} fill="var(--v-neg)" opacity="0.08" />
    {#each [-4, -2, 0, 2, 4] as t (t)}
      <line x1={M.l} x2={W - M.r} y1={yl(t)} y2={yl(t)} stroke="var(--v-grid)" />
      <text x={M.l - 6} y={yl(t) + 4} text-anchor="end">{t}</text>
    {/each}
    <line x1={M.l} x2={W - M.r} y1={yl(result.upper)} y2={yl(result.upper)} stroke="var(--v-pos)" stroke-dasharray="4 3" />
    <line x1={M.l} x2={W - M.r} y1={yl(result.lower)} y2={yl(result.lower)} stroke="var(--v-neg)" stroke-dasharray="4 3" />
    <text x={W - M.r - 4} y={yl(result.upper) - 5} text-anchor="end">A = ln((1−β)/α) = {fmt(lang, result.upper, 3)}: {say(lang, w('pass', '通过', '通過'))}</text>
    <text x={W - M.r - 4} y={yl(result.lower) + 14} text-anchor="end">B = ln(β/(1−α)) = {fmt(lang, result.lower, 3)}: {say(lang, w('fail', '不通过', '不通過'))}</text>
    <path d={path} stroke="var(--v-ink)" stroke-width="1.8" fill="none" />
    {#if result.verdict !== 'open'}
      <circle cx={x(result.decided)} cy={yl(clampL(result.llr))} r="4.5" fill={result.verdict === 'pass' ? 'var(--v-pos)' : 'var(--v-neg)'} />
      <line x1={x(result.decided)} x2={x(result.decided)} y1={H1.t} y2={H2.t + H2.h} stroke="var(--v-ink-2)" stroke-dasharray="2 3" />
    {/if}
    {#each ticks as t (t)}
      <text x={x(t)} y={H2.t + H2.h + 16} text-anchor="middle">{t}</text>
    {/each}
    <text x={W - M.r} y={H - 2} text-anchor="end">{say(lang, w('pair, in the seed set’s order', '配对，按种子集的顺序', '配對，按種子集的順序'))}</text>
    {#if hover !== null}
      <line x1={x(hover)} x2={x(hover)} y1={H1.t} y2={H2.t + H2.h} stroke="var(--v-ink)" opacity="0.35" />
    {/if}
  </svg>
  {#if hover !== null}
    <div class="v-tip v-num" style="right: 1rem; top: 3.5rem;">
      {say(lang, w('pair', '第', '第'))} <strong>{hover}</strong>{say(lang, w('', ' 对', ' 對'))}: {say(lang, WHO(pairs[hover - 1]))}<br />
      LLR {hover > result.decided ? say(lang, w('(after the verdict: not computed)', '（判定之后：不再计算）', '（判定之後：不再計算）')) : fmt(lang, result.path[hover - 1], 3)}
    </div>
  {/if}
  <div class="v-row" style="margin-top: 0.25rem;">
    <div class="v-stat"><span class="v-label">{say(lang, w('verdict', '判定', '判定'))}</span><span class="v-value">{say(lang, VERDICT[result.verdict])}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, w('after pairs', '用了几对', '用了幾對'))}</span><span class="v-value v-num">{result.decided} / {result.pairs}</span></div>
    <div class="v-stat"><span class="v-label">LLR</span><span class="v-value v-num">{fmt(lang, result.llr, 4)}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, w('won by the challenger alone', '只有挑战者赢', '只有挑戰者贏'))}</span><span class="v-value v-num">{result.challengerOnly}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, w('won by the champion alone', '只有冠军赢', '只有冠軍贏'))}</span><span class="v-value v-num">{result.championOnly}</span></div>
    {#if tab !== SIM}
      <div class="v-stat"><span class="v-label">{say(lang, w('floor difference, listed', '楼层差（仅列出）', '樓層差（僅列出）'))}</span><span class="v-value v-num">{signed(result.floorDiffMean, 3)}</span></div>
    {/if}
  </div>
  {#if tab === SIM && oc}
    <p class="v-caption v-num">
      {oc.runs} {say(lang, w(`simulated gates of ${n} pairs:`, `次模拟门槛，各 ${n} 对：`, `次模擬門檻，各 ${n} 對：`))}
      {say(lang, w('pass', '通过', '通過'))} {fmt(lang, (100 * oc.pass) / oc.runs, 1)}%, {say(lang, w('fail', '不通过', '不通過'))} {fmt(lang, (100 * oc.fail) / oc.runs, 1)}%, {say(lang, w('open', '未定', '未定'))} {fmt(lang, (100 * oc.open) / oc.runs, 1)}%;
      {say(lang, w('pairs used on average', '平均用了', '平均用了'))} {fmt(lang, oc.mean, 0)}{say(lang, w('', ' 对', ' 對'))}.
    </p>
  {/if}
  <p class="v-caption">
    {#if tab === SIM}
      {say(lang, w(
        'Made-up pairs, not runs: a pair is won by one side only with the chance set, and then by the challenger with its share. The gate is the real one: δ = 0.1, α = β = 0.05.',
        '编造的配对，不是真的对局：一对以设定的机率只有一边赢，那时挑战者以它的比例赢。gate 本身是真的：δ = 0.1，α = β = 0.05。',
        '編造的配對，不是真的對局：一對以設定的機率只有一邊贏，那時挑戰者以它的比例贏。gate 本身是真的：δ = 0.1，α = β = 0.05。',
      ))}
    {:else}
      {say(lang, w(
        'Two committed SL reports on the same 300 seeds, the later net as the challenger, replayed with the gate’s rule; no such gate was run. Grey bars are pairs after the verdict, which the gate never looks at.',
        '同一组 300 个种子上的两份已提交的 SL 报告，较新的网络当挑战者，按 gate 的规则重放；并没有真的跑过这样的 gate。灰色的柱是判定之后的配对，gate 不会看。',
        '同一組 300 個種子上的兩份已提交的 SL 報告，較新的網路當挑戰者，按 gate 的規則重放；並沒有真的跑過這樣的 gate。灰色的柱是判定之後的配對，gate 不會看。',
      ))}
    {/if}
  </p>
</div>
