<script lang="ts">
  // Why a gate pairs runs by seed (docs: evaluation/gates §2): two committed reports on the same seeds, each seed's
  // floors as one point, and the 95% interval of the mean difference with and without the pairing
  // (src/data/book/eval-pairs.json, from eval/silent/reports/).
  import { scaleLinear } from 'd3';
  import type { Lang } from '../v/names.ts';
  import { fmt } from '../v/i18n.ts';
  import { w, say } from './words.ts';
  import { meanSe, unpairedSe } from './evalStats.ts';
  import data from '../../data/book/eval-pairs.json';

  let { lang = 'en' }: { lang?: Lang } = $props();

  let pick = $state(0);
  const p = $derived(data.pairs[pick]);
  const n = $derived(p.floors.length);
  const d = $derived(p.floors.map(([a, b]) => b - a));
  const paired = $derived(meanSe(d));
  const unSe = $derived(unpairedSe(p.floors.map((f) => f[0]), p.floors.map((f) => f[1])));
  const ratio = $derived((unSe * unSe) / (paired.se * paired.se));
  const corr = $derived.by(() => {
    const a = p.floors.map((f) => f[0]);
    const b = p.floors.map((f) => f[1]);
    const ma = a.reduce((s, x) => s + x, 0) / n;
    const mb = b.reduce((s, x) => s + x, 0) / n;
    let sab = 0, saa = 0, sbb = 0;
    for (let i = 0; i < n; i++) {
      sab += (a[i] - ma) * (b[i] - mb);
      saa += (a[i] - ma) ** 2;
      sbb += (b[i] - mb) ** 2;
    }
    return sab / Math.sqrt(saa * sbb);
  });
  const counts = $derived({ further: d.filter((x) => x > 0).length, same: d.filter((x) => x === 0).length, less: d.filter((x) => x < 0).length });
  const winsD = $derived(p.floors.map((_, i) => (p.bWon.includes(i) ? 1 : 0) - (p.aWon.includes(i) ? 1 : 0)));
  const winsPaired = $derived(meanSe(winsD));
  const winsUn = $derived(unpairedSe(p.floors.map((_, i) => (p.aWon.includes(i) ? 1 : 0)), p.floors.map((_, i) => (p.bWon.includes(i) ? 1 : 0))));
  // One point per distinct (a, b), sized by how many seeds share it.
  const cells = $derived.by(() => {
    const m = new Map<string, { a: number; b: number; n: number; aWon: number; bWon: number }>();
    p.floors.forEach(([a, b], i) => {
      const key = `${a},${b}`;
      const c = m.get(key) ?? { a, b, n: 0, aWon: 0, bWon: 0 };
      c.n++;
      if (p.aWon.includes(i)) c.aWon++;
      if (p.bWon.includes(i)) c.bWon++;
      m.set(key, c);
    });
    return [...m.values()];
  });

  const S = 300;
  const M = { l: 40, t: 12, b: 36 };
  const W = 660;
  const H = S + M.t + M.b;
  const sx = scaleLinear().domain([0, 50]).range([M.l, M.l + S]);
  const sy = scaleLinear().domain([0, 50]).range([M.t + S, M.t]);
  const IX = { l: M.l + S + 40, r: W - 14 };
  const ix = $derived.by(() => {
    const lo = Math.min(-1, paired.mean - 1.96 * unSe - 0.5);
    const hi = Math.max(3, paired.mean + 1.96 * unSe + 0.5);
    return scaleLinear().domain([Math.floor(lo), Math.ceil(hi)]).range([IX.l, IX.r]);
  });
  let hover = $state<number | null>(null);
  const signed = (x: number, digits = 2) => (x >= 0 ? '+' : '−') + fmt(lang, Math.abs(x), digits);
</script>

<div class="vfig" style="position: relative;">
  <div class="v-tabs">
    {#each data.pairs as q, i (i)}
      <button aria-pressed={pick === i} onclick={() => ((pick = i), (hover = null))}>
        {q.b.label} − {q.a.label} ({q.env}, {q.seedSet})
      </button>
    {/each}
  </div>
  <svg viewBox="0 0 {W} {H}" width="100%" role="img" aria-label="paired floors">
    {#each [0, 10, 20, 30, 40, 50] as t (t)}
      <line x1={sx(t)} x2={sx(t)} y1={M.t} y2={M.t + S} stroke="var(--v-grid)" />
      <line x1={M.l} x2={M.l + S} y1={sy(t)} y2={sy(t)} stroke="var(--v-grid)" />
      <text x={sx(t)} y={M.t + S + 14} text-anchor="middle">{t}</text>
      <text x={M.l - 6} y={sy(t) + 4} text-anchor="end">{t}</text>
    {/each}
    {#each [17, 33] as f (f)}
      <line x1={sx(f)} x2={sx(f)} y1={M.t} y2={M.t + S} stroke="var(--v-axis)" stroke-dasharray="2 3" />
      <line x1={M.l} x2={M.l + S} y1={sy(f)} y2={sy(f)} stroke="var(--v-axis)" stroke-dasharray="2 3" />
    {/each}
    <line x1={sx(0)} y1={sy(0)} x2={sx(50)} y2={sy(50)} stroke="var(--v-ink-2)" />
    <text x={M.l} y={H - 4}>{say(lang, w('floor of', '楼层：', '樓層：'))} {p.a.label}</text>
    <text x={M.l - 30} y={M.t + S / 2} transform="rotate(-90 {M.l - 30} {M.t + S / 2})" text-anchor="middle">{say(lang, w('floor of', '楼层：', '樓層：'))} {p.b.label}</text>
    {#each cells as c, i (`${c.a},${c.b}`)}
      <circle
        cx={sx(c.a)}
        cy={sy(c.b)}
        r={2 + 2.2 * Math.sqrt(c.n)}
        fill={c.b > c.a ? 'var(--v-s1)' : c.b < c.a ? 'var(--v-s2)' : 'var(--v-muted)'}
        fill-opacity={hover === i ? 0.95 : 0.55}
        stroke={c.aWon + c.bWon > 0 ? 'var(--v-ink)' : 'none'}
        stroke-width="1.5"
        role="presentation"
        onpointerenter={() => (hover = i)}
        onpointerleave={() => (hover = null)}
      />
    {/each}

    <text x={IX.l} y={M.t + 10} fill="var(--v-ink)" style="font-size: 12px;">{say(lang, w('Mean floor difference, 95% interval', '平均楼层差，95% 区间', '平均樓層差，95% 區間'))}</text>
    {#each ix.ticks(6) as t (t)}
      <line x1={ix(t)} x2={ix(t)} y1={M.t + 24} y2={M.t + 150} stroke={t === 0 ? 'var(--v-axis)' : 'var(--v-grid)'} />
      <text x={ix(t)} y={M.t + 164} text-anchor="middle">{t > 0 ? '+' : ''}{t}</text>
    {/each}
    {#each [{ y: M.t + 60, se: paired.se, label: w('paired by seed', '按种子配对', '按種子配對'), c: 'var(--v-s1)' }, { y: M.t + 112, se: unSe, label: w('as if unpaired', '当作不配对', '當作不配對'), c: 'var(--v-s2)' }] as row (row.y)}
      <text x={IX.l} y={row.y - 12}>{say(lang, row.label)}: ± {fmt(lang, 1.96 * row.se, 2)}</text>
      <line x1={ix(paired.mean - 1.96 * row.se)} x2={ix(paired.mean + 1.96 * row.se)} y1={row.y} y2={row.y} stroke={row.c} stroke-width="6" stroke-linecap="round" />
      <circle cx={ix(paired.mean)} cy={row.y} r="4" fill="var(--v-ink)" />
    {/each}
    <text x={IX.l} y={M.t + 196} class="v-num">{say(lang, w('mean', '平均', '平均'))} {signed(paired.mean)} · r = {fmt(lang, corr, 2)}</text>
    <text x={IX.l} y={M.t + 214} class="v-num">{say(lang, w('further / as far / less far:', '更远／一样／更近：', '更遠／一樣／更近：'))} {counts.further} / {counts.same} / {counts.less}</text>
    <text x={IX.l} y={M.t + 232} class="v-num">{say(lang, w('variance ratio', '方差比', '變異數比'))} {fmt(lang, ratio, 2)}: {say(lang, w('unpaired needs', '不配对要', '不配對要'))} ×{fmt(lang, ratio, 2)} {say(lang, w('runs a side', '倍的局数', '倍的局數'))}</text>
    <text x={IX.l} y={M.t + 258} class="v-num">{say(lang, w('wins:', '通关：', '通關：'))} {p.bWon.length} {say(lang, w('against', '对', '對'))} {p.aWon.length}, {signed(100 * winsPaired.mean, 1)} pp</text>
    <text x={IX.l} y={M.t + 276} class="v-num">SE {say(lang, w('paired', '配对', '配對'))} {fmt(lang, 100 * winsPaired.se, 2)} pp, {say(lang, w('unpaired', '不配对', '不配對'))} {fmt(lang, 100 * winsUn, 2)} pp</text>
  </svg>
  {#if hover !== null}
    {@const c = cells[hover]}
    <div class="v-tip v-num" style="left: 1rem; top: 3rem;">
      <strong>{c.n}</strong> {say(lang, w(c.n === 1 ? 'seed' : 'seeds', '个种子', '個種子'))}: {p.a.label} {say(lang, w('floor', '楼层', '樓層'))} {c.a}, {p.b.label} {say(lang, w('floor', '楼层', '樓層'))} {c.b}
      {#if c.aWon + c.bWon > 0}<br />{say(lang, w('won by', '通关：', '通關：'))} {p.a.label} {c.aWon}, {p.b.label} {c.bWon}{/if}
    </div>
  {/if}
  <p class="v-caption">
    {say(lang, w(
      `Each point is the seeds where the two reached the same pair of floors, larger for more seeds; blue above the diagonal (the second went further), orange below, grey on it; a ring marks a cell with a won run. The dashed lines are the act 1 and act 2 bosses (floors 17 and 33); a run that dies at the act 3 boss or wins ends on 48. The paired interval is the mean of the per-seed differences ± 1.96 SE; the unpaired one treats the two reports as independent samples. Pairing narrows the floor interval; for wins here it changes almost nothing, since wins are rare and almost no seed is won by both.`,
      `每个点是两边到达同一对楼层的那些种子，种子越多点越大；对角线上方蓝色（第二个走得更远），下方橙色，线上灰色；有通关局的格子加一圈外框。虚线是第 1、2 幕 Boss（第 17、33 层）；死在第 3 幕 Boss 或通关的局都停在第 48 层。配对区间是逐种子差值的平均 ± 1.96 SE；不配对区间把两份报告当成独立样本。配对让楼层区间变窄；这里对通关几乎没有帮助，因为通关少，几乎没有两边都通关的种子。`,
      `每個點是兩邊到達同一對樓層的那些種子，種子越多點越大；對角線上方藍色（第二個走得更遠），下方橙色，線上灰色；有通關局的格子加一圈外框。虛線是第 1、2 幕 Boss（第 17、33 層）；死在第 3 幕 Boss 或通關的局都停在第 48 層。配對區間是逐種子差值的平均 ± 1.96 SE；不配對區間把兩份報告當成獨立樣本。配對讓樓層區間變窄；這裡對通關幾乎沒有幫助，因為通關少，幾乎沒有兩邊都通關的種子。`,
    ))}
  </p>
</div>
