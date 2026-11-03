<script lang="ts">
  // Where one report's lost runs ended, floor by floor, and in what room (docs: evaluation/metrics §8), from the run
  // records the report embeds (src/data/book/eval-reports.json, from eval/silent/reports/).
  import { scaleBand, scaleLinear } from 'd3';
  import type { Lang } from '../v/names.ts';
  import { w, say, type Words } from './words.ts';
  import data from '../../data/book/eval-reports.json';

  let { lang = 'en' }: { lang?: Lang } = $props();

  const r = data.report;
  const KINDS: { key: 'Monster' | 'Elite' | 'Boss' | 'outside'; label: Words; colour: string }[] = [
    { key: 'Monster', label: w('monster fight', '普通战斗', '普通戰鬥'), colour: 'var(--v-s1)' },
    { key: 'Elite', label: w('elite', '精英', '菁英'), colour: 'var(--v-s4)' },
    { key: 'Boss', label: w('boss', 'Boss', '頭目'), colour: 'var(--v-s2)' },
    { key: 'outside', label: w('outside combat', '战斗之外', '戰鬥之外'), colour: 'var(--v-muted)' },
  ];
  const rows = r.deaths.byFloor;
  const totals = Object.fromEntries(KINDS.map((k) => [k.key, rows.reduce((s, f) => s + f[k.key], 0)])) as Record<string, number>;
  const lost = Object.values(totals).reduce((a, b) => a + b, 0);
  const floors = Array.from({ length: 48 }, (_, i) => i + 1);

  const W = 660;
  const H = 240;
  const M = { l: 34, r: 56, t: 16, b: 34 };
  const x = scaleBand<number>().domain(floors).range([M.l, W - M.r]).padding(0.15);
  const yMax = Math.max(10, r.won, ...rows.map((f) => f.Monster + f.Elite + f.Boss + f.outside));
  const y = scaleLinear().domain([0, yMax]).nice().range([H - M.b, M.t]);
  const stack = (f: (typeof rows)[number]) => {
    let base = 0;
    return KINDS.map((k) => {
      const seg = { key: k.key, colour: k.colour, y0: base, y1: base + f[k.key] };
      base += f[k.key];
      return seg;
    });
  };
  let hover = $state<number | null>(null);
  const hovered = $derived(hover === null ? null : rows.find((f) => f.floor === hover) ?? null);
</script>

<div class="vfig" style="position: relative;">
  <div class="v-row" style="margin-bottom: 0.5rem;">
    {#each KINDS as k (k.key)}
      <span><span class="v-key" style:background={k.colour}></span> {say(lang, k.label)} {totals[k.key]}</span>
    {/each}
  </div>
  <svg viewBox="0 0 {W} {H}" width="100%" role="img" aria-label="deaths by floor" onpointerleave={() => (hover = null)}>
    {#each y.ticks(5) as v (v)}
      <line x1={M.l} x2={W - M.r} y1={y(v)} y2={y(v)} stroke="var(--v-grid)" />
      <text x={M.l - 6} y={y(v) + 4} text-anchor="end">{v}</text>
    {/each}
    {#each [17, 33, 48] as f, i (f)}
      <line x1={x(f)! + x.bandwidth() / 2} x2={x(f)! + x.bandwidth() / 2} y1={M.t} y2={H - M.b} stroke="var(--v-axis)" stroke-dasharray="2 3" />
      <text x={x(f)! + x.bandwidth() / 2} y={M.t + 2} text-anchor="middle">{say(lang, w(`act ${i + 1} boss`, `第 ${i + 1} 幕 Boss`, `第 ${i + 1} 幕 Boss`))}</text>
    {/each}
    {#each floors as f (f)}
      <rect x={x(f)} y={M.t} width={x.bandwidth()} height={H - M.b - M.t} fill={hover === f ? 'var(--v-wash)' : 'transparent'} role="presentation" onpointerenter={() => (hover = f)} />
      {#if f % 4 === 1 || f === 48}<text x={x(f)! + x.bandwidth() / 2} y={H - M.b + 14} text-anchor="middle">{f}</text>{/if}
    {/each}
    {#each rows as row (row.floor)}
      {#each stack(row) as s (s.key)}
        {#if s.y1 > s.y0}
          <rect x={x(row.floor)} width={x.bandwidth()} y={y(s.y1)} height={y(s.y0) - y(s.y1)} fill={s.colour} pointer-events="none" />
        {/if}
      {/each}
    {/each}
    <rect x={W - M.r + 14} width={x.bandwidth() * 1.6} y={y(r.won)} height={y(0) - y(r.won)} fill="var(--v-s3)" />
    <text x={W - M.r + 14 + x.bandwidth() * 0.8} y={H - M.b + 14} text-anchor="middle">{say(lang, w('won', '通关', '通關'))}</text>
    <text x={W - M.r + 14 + x.bandwidth() * 0.8} y={y(r.won) - 4} text-anchor="middle" class="v-num">{r.won}</text>
    <text x={M.l} y={H - 4}>{say(lang, w('floor the run ended on', '对局结束的楼层', '對局結束的樓層'))}</text>
  </svg>
  {#if hovered}
    <div class="v-tip v-num" style="left: 3rem; top: 3.5rem;">
      {say(lang, w('floor', '第', '第'))} <strong>{hovered.floor}</strong>{say(lang, w('', ' 层', ' 層'))}:
      {#each KINDS.filter((k) => hovered[k.key] > 0) as k, i (k.key)}{i ? ', ' : ''}{say(lang, k.label)} {hovered[k.key]}{/each}
    </div>
  {/if}
  <p class="v-caption">
    {say(lang, w(
      `${lost} lost runs of ${r.runs}, ${r.won} won (an earlier champion, ${r.env}, ${r.seedSet}). Bosses reached ${r.bossesReached.join(' / ')}, beaten ${r.bossesBeaten.join(' / ')} (acts 1 / 2 / 3). A lost run died in a combat when its last combat is on the floor it ended on and that combat ended in a death or has no end; ${r.deaths.noEnd} of these deaths have no end in the record, and learn/gate.py counts those as outside combat.`,
      `${r.runs} 局里输了 ${lost} 局，通关 ${r.won} 局（一个较早的冠军，${r.env}，${r.seedSet}）。Boss 到达 ${r.bossesReached.join(' / ')}，击败 ${r.bossesBeaten.join(' / ')}（第 1 / 2 / 3 幕）。输掉的一局若最后一场战斗就在它结束的楼层，且那场战斗以死亡结束或没有 end，就算死在那场战斗里；其中 ${r.deaths.noEnd} 场死亡在记录里没有 end，learn/gate.py 把它们算成战斗之外。`,
      `${r.runs} 局裡輸了 ${lost} 局，通關 ${r.won} 局（一個較早的冠軍，${r.env}，${r.seedSet}）。Boss 到達 ${r.bossesReached.join(' / ')}，擊敗 ${r.bossesBeaten.join(' / ')}（第 1 / 2 / 3 幕）。輸掉的一局若最後一場戰鬥就在它結束的樓層，且那場戰鬥以死亡結束或沒有 end，就算死在那場戰鬥裡；其中 ${r.deaths.noEnd} 場死亡在紀錄裡沒有 end，learn/gate.py 把它們算成戰鬥之外。`,
    ))}
  </p>
</div>
