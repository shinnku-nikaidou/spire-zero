<script lang="ts">
  // A real gate replayed pair by pair (docs: evaluation/gates §3): four NoSL floor gates, each from the two
  // committed reports (src/data/book/eval-gates.json), with Gate.Sprt's rule (evalStats.ts, src/Sts2Ai.Harness/Gate.cs:62-107);
  // the replay reaches the verdict, pair and LLR the champion file stored. "Simulate" draws made-up pairs instead.
  import { scaleLinear } from 'd3';
  import type { Lang } from '../v/names.ts';
  import { fmt } from '../v/i18n.ts';
  import { w, say } from './words.ts';
  import { sprt, MIN_PAIRS, type Pair } from './evalStats.ts';
  import data from '../../data/book/eval-gates.json';

  let { lang = 'en' }: { lang?: Lang } = $props();

  const realPairs = (g: (typeof data.gates)[number]): Pair[] =>
    g.d.map((d, k) => ({ d, championWon: g.championWon.includes(k), challengerWon: g.challengerWon.includes(k) }));

  let tab = $state<number>(data.gates.length - 1);
  const SIM = -1;

  // The simulation's defaults come from the last real gate: its share of tied pairs and the SD of the others.
  const last = data.gates[data.gates.length - 1];
  const nonTied = last.d.filter((x) => x !== 0);
  const tieDefault = Math.round((100 * (last.d.length - nonTied.length)) / last.d.length) / 100;
  const sdDefault = Math.round(Math.sqrt(nonTied.reduce((s, x) => s + (x - nonTied.reduce((a, b) => a + b, 0) / nonTied.length) ** 2, 0) / (nonTied.length - 1)));
  let mu = $state(1.5);
  let ties = $state(tieDefault);
  let sd = $state(sdDefault);
  let notWorse = $state(false);
  let seed = $state(1);
  const N_SIM = 200;

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
  function draw(s: number, n: number): Pair[] {
    const r = rng(s);
    const spread = ties < 1 ? mu / (1 - ties) : 0;
    return Array.from({ length: n }, () => {
      if (r() < ties) return { d: 0, championWon: false, challengerWon: false };
      const z = Math.sqrt(-2 * Math.log(1 - r())) * Math.cos(2 * Math.PI * r());
      return { d: Math.round(spread + sd * z), championWon: false, challengerWon: false };
    });
  }

  const pairs = $derived(tab === SIM ? draw(seed, N_SIM) : realPairs(data.gates[tab]));
  const result = $derived(sprt(pairs, 'floor', 1.5, 0.05, 0.05, tab === SIM && notWorse));
  const gate = $derived(tab === SIM ? null : data.gates[tab]);

  let oc = $state<{ pass: number; fail: number; open: number; mean: number; runs: number } | null>(null);
  function operating() {
    const runs = 1000;
    let pass = 0, fail = 0, open = 0, decided = 0;
    for (let i = 0; i < runs; i++) {
      const r = sprt(draw(10_000 + i, N_SIM), 'floor', 1.5, 0.05, 0.05, notWorse);
      if (r.verdict === 'pass') pass++;
      else if (r.verdict === 'fail') fail++;
      else open++;
      decided += r.decided;
    }
    oc = { pass, fail, open, mean: decided / runs, runs };
  }
  $effect(() => {
    // A changed setting makes the last batch stale.
    void mu, ties, sd, notWorse;
    oc = null;
  });

  const W = 660;
  const M = { l: 46, r: 14 };
  const H1 = { t: 8, h: 110 };
  const H2 = { t: 150, h: 190 };
  const H = H2.t + H2.h + 34;
  const x = $derived(scaleLinear().domain([0.5, pairs.length + 0.5]).range([M.l, W - M.r]));
  const dMax = $derived(Math.max(10, ...pairs.map((p) => Math.abs(p.d))));
  const yd = $derived(scaleLinear().domain([-dMax, dMax]).range([H1.t + H1.h, H1.t]));
  const yl = scaleLinear().domain([-4.5, 4.5]).range([H2.t + H2.h, H2.t]);
  const clampL = (v: number) => Math.max(-4.5, Math.min(4.5, v));
  const path = $derived.by(() => {
    let s = '';
    result.path.forEach((v, i) => {
      if (v === null) return;
      s += `${s ? 'L' : 'M'}${x(i + 1)},${yl(clampL(v))}`;
    });
    return s;
  });
  const firstLlr = $derived(result.path.findIndex((v) => v !== null) + 1);
  const bw = $derived(Math.max(1, (W - M.l - M.r) / pairs.length - 0.6));

  let hover = $state<number | null>(null);
  function move(e: PointerEvent) {
    const svg = e.currentTarget as SVGSVGElement;
    const box = svg.getBoundingClientRect();
    const px = ((e.clientX - box.left) / box.width) * W;
    const k = Math.round(x.invert(px));
    hover = k >= 1 && k <= pairs.length ? k : null;
  }
  const running = (k: number) => pairs.slice(0, k).reduce((s, p) => s + p.d, 0) / k;
  const VERDICT: Record<string, ReturnType<typeof w>> = {
    pass: w('pass', '通过', '通過'),
    fail: w('fail', '不通过', '不通過'),
    open: w('open', '未定', '未定'),
  };
  const signed = (v: number, digits = 2) => (v >= 0 ? '+' : '−') + fmt(lang, Math.abs(v), digits);
</script>

<div class="vfig" style="position: relative;">
  <div class="v-tabs">
    {#each data.gates as g, i (g.commit)}
      <button aria-pressed={tab === i} onclick={() => ((tab = i), (hover = null))}>{say(lang, w(`Gate ${i + 1}`, `门槛 ${i + 1}`, `門檻 ${i + 1}`))}</button>
    {/each}
    <button aria-pressed={tab === SIM} onclick={() => ((tab = SIM), (hover = null))}>{say(lang, w('Simulate', '模拟', '模擬'))}</button>
  </div>
  {#if tab === SIM}
    <div class="v-row" style="margin-bottom: 0.5rem;">
      <label>{say(lang, w('true mean difference', '真实平均差', '真實平均差'))} <input type="range" min="-3" max="5" step="0.25" bind:value={mu} /> <span class="v-num">{signed(mu)}</span></label>
      <label>{say(lang, w('tied pairs', '平手的配对', '平手的配對'))} <input type="range" min="0" max="0.8" step="0.05" bind:value={ties} /> <span class="v-num">{fmt(lang, 100 * ties, 0)}%</span></label>
      <label>{say(lang, w('SD of the others', '其余的标准差', '其餘的標準差'))} <input type="range" min="2" max="16" step="1" bind:value={sd} /> <span class="v-num">{sd}</span></label>
      <label><input type="checkbox" bind:checked={notWorse} /> --not-worse</label>
      <button onclick={() => (seed += 1)}>{say(lang, w('Draw again', '重新抽', '重新抽'))}</button>
      <button onclick={operating}>{say(lang, w('Run 1,000 gates', '跑 1,000 次门槛', '跑 1,000 次門檻'))}</button>
    </div>
  {/if}
  <svg viewBox="0 0 {W} {H}" width="100%" role="img" aria-label="SPRT" onpointermove={move} onpointerleave={() => (hover = null)}>
    <text x={M.l} y={H1.t + 10} fill="var(--v-ink)" style="font-size: 12px;">{say(lang, w('floor difference per pair (challenger − champion)', '每对的楼层差（挑战者 − 冠军）', '每對的樓層差（挑戰者 − 冠軍）'))}</text>
    <line x1={M.l} x2={W - M.r} y1={yd(0)} y2={yd(0)} stroke="var(--v-axis)" />
    {#each [-dMax, dMax] as t (t)}
      <text x={M.l - 6} y={yd(t) + 4} text-anchor="end">{t > 0 ? '+' : ''}{t}</text>
    {/each}
    {#each pairs as p, i (i)}
      {#if p.d !== 0}
        <rect
          x={x(i + 1) - bw / 2}
          width={bw}
          y={p.d > 0 ? yd(p.d) : yd(0)}
          height={Math.abs(yd(p.d) - yd(0))}
          fill={i + 1 > result.decided ? 'var(--v-grid)' : p.d > 0 ? 'var(--v-s1)' : 'var(--v-s2)'}
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
    {#if firstLlr > 1}
      <text x={W - M.r} y={H2.t - 8} text-anchor="end">{say(lang, w(`no LLR until ${MIN_PAIRS} pairs differ (pair ${firstLlr})`, `${MIN_PAIRS} 对不同之前没有 LLR（第 ${firstLlr} 对）`, `${MIN_PAIRS} 對不同之前沒有 LLR（第 ${firstLlr} 對）`))}</text>
    {/if}
    <path d={path} stroke="var(--v-ink)" stroke-width="1.8" fill="none" />
    {#if result.verdict !== 'open'}
      <circle cx={x(result.decided)} cy={yl(clampL(result.llr))} r="4.5" fill={result.verdict === 'pass' ? 'var(--v-pos)' : 'var(--v-neg)'} />
      <line x1={x(result.decided)} x2={x(result.decided)} y1={H1.t} y2={H2.t + H2.h} stroke="var(--v-ink-2)" stroke-dasharray="2 3" />
    {/if}
    {#each [1, 50, 100, 150, 200].filter((t) => t <= pairs.length) as t (t)}
      <text x={x(t)} y={H2.t + H2.h + 16} text-anchor="middle">{t}</text>
    {/each}
    <text x={W - M.r} y={H - 2} text-anchor="end">{say(lang, w('pair, in the seed set’s order', '配对，按种子集的顺序', '配對，按種子集的順序'))}</text>
    {#if hover !== null}
      <line x1={x(hover)} x2={x(hover)} y1={H1.t} y2={H2.t + H2.h} stroke="var(--v-ink)" opacity="0.35" />
    {/if}
  </svg>
  {#if hover !== null}
    {@const v = result.path[hover - 1]}
    <div class="v-tip v-num" style="right: 1rem; top: 3.5rem;">
      {say(lang, w('pair', '第', '第'))} <strong>{hover}</strong>{say(lang, w('', ' 对', ' 對'))}: {signed(pairs[hover - 1].d, 0)}<br />
      {say(lang, w('mean so far', '到这里的平均', '到這裡的平均'))} {signed(running(hover))}<br />
      LLR {hover > result.decided ? say(lang, w('(after the verdict: not computed)', '（判定之后：不再计算）', '（判定之後：不再計算）')) : v === null ? '–' : fmt(lang, v, 3)}
    </div>
  {/if}
  <div class="v-row" style="margin-top: 0.25rem;">
    <div class="v-stat"><span class="v-label">{say(lang, w('verdict', '判定', '判定'))}</span><span class="v-value">{say(lang, VERDICT[result.verdict])}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, w('after pairs', '用了几对', '用了幾對'))}</span><span class="v-value v-num">{result.decided} / {result.pairs}</span></div>
    <div class="v-stat"><span class="v-label">LLR</span><span class="v-value v-num">{fmt(lang, result.llr, 4)}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, w('mean difference', '平均差', '平均差'))}</span><span class="v-value v-num">{signed(result.floorDiffMean, 3)}</span></div>
  </div>
  {#if gate}
    <p class="v-caption v-num">
      {say(lang, w('Stored in the champion file:', '冠军文件存的：', '冠軍檔案存的：'))}
      {gate.stored.verdict} {say(lang, w('after', '用了', '用了'))} {gate.stored.decided} {say(lang, w('of', '对，共', '對，共'))} {gate.stored.pairs}{say(lang, w(' pairs', ' 对', ' 對'))}, LLR {fmt(lang, gate.stored.llr, 4)},
      {say(lang, w('floor difference', '楼层差', '樓層差'))} {signed(gate.stored.floor_diff_mean, 3)}.
      {say(lang, w('Mean floor: champion', '平均楼层：冠军', '平均樓層：冠軍'))} {fmt(lang, gate.champion.floor, 2)}, {say(lang, w('challenger', '挑战者', '挑戰者'))} {fmt(lang, gate.challenger.floor, 2)}.
    </p>
  {:else if oc}
    <p class="v-caption v-num">
      {oc.runs} {say(lang, w('simulated gates of 200 pairs:', '次模拟门槛，各 200 对：', '次模擬門檻，各 200 對：'))}
      {say(lang, w('pass', '通过', '通過'))} {fmt(lang, (100 * oc.pass) / oc.runs, 1)}%, {say(lang, w('fail', '不通过', '不通過'))} {fmt(lang, (100 * oc.fail) / oc.runs, 1)}%, {say(lang, w('open', '未定', '未定'))} {fmt(lang, (100 * oc.open) / oc.runs, 1)}%;
      {say(lang, w('pairs used on average', '平均用了', '平均用了'))} {fmt(lang, oc.mean, 0)}{say(lang, w('', ' 对', ' 對'))}.
    </p>
  {/if}
  <p class="v-caption">
    {#if tab === SIM}
      {say(lang, w(
        `Made-up pairs, not runs: each pair ties with the chance set, and otherwise differs by a normal draw rounded to whole floors, scaled so the mean difference is the one set. The defaults are the last real gate’s share of ties (${fmt(lang, 100 * tieDefault, 0)}%) and SD of its other pairs (${sdDefault}). The gate is the real one: δ = 1.5 floors, α = β = 0.05, at most 200 pairs.`,
        `编造的配对，不是真的对局：每一对以设定的机率平手，否则差值是一个四舍五入到整层的正态抽样，缩放成设定的平均差。默认值取自最后一个真实 gate：平手比例（${fmt(lang, 100 * tieDefault, 0)}%）和其余配对的标准差（${sdDefault}）。gate 本身是真的：δ = 1.5 层，α = β = 0.05，最多 200 对。`,
        `編造的配對，不是真的對局：每一對以設定的機率平手，否則差值是一個四捨五入到整層的常態抽樣，縮放成設定的平均差。預設值取自最後一個真實 gate：平手比例（${fmt(lang, 100 * tieDefault, 0)}%）和其餘配對的標準差（${sdDefault}）。gate 本身是真的：δ = 1.5 層，α = β = 0.05，最多 200 對。`,
      ))}
    {:else}
      {say(lang, w(
        'The challenger’s runs are its promoted report’s, which --promote wrote from the gate’s own records; the champion’s are the report its champion file named then, in that report’s order. Grey bars are pairs after the verdict, which the gate never looked at. Every one of these gates had no won run on either side, so a win gate would have stayed open.',
        '挑战者的对局取自它升级后的报告，那份报告是 --promote 用这次 gate 的同一批对局记录写的；冠军的对局取自当时冠军档案指定的报告，按那份报告的顺序。灰色的柱是判定之后的配对，gate 根本没看。这几个 gate 两边都没有通关局，所以若按通关比，gate 会一直未定。',
        '挑戰者的對局取自它升級後的報告，那份報告是 --promote 用這次 gate 的同一批對局紀錄寫的；冠軍的對局取自當時冠軍檔案指定的報告，按那份報告的順序。灰色的柱是判定之後的配對，gate 根本沒看。這幾個 gate 兩邊都沒有通關局，所以若按通關比，gate 會一直未定。',
      ))}
    {/if}
  </p>
</div>
