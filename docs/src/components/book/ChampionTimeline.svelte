<script lang="ts">
  // Every committed whole-run report by when it entered git, its mean floor, NoSL and SL apart, with each
  // environment's champion over time from the champion files' history (docs: evaluation/champions §2;
  // src/data/book/eval-reports.json, from eval/silent/reports/ and eval/*/champions/ at each commit).
  import { scaleLinear, scaleUtc } from 'd3';
  import type { Lang } from '../v/names.ts';
  import { fmt } from '../v/i18n.ts';
  import { w, say } from './words.ts';
  import data from '../../data/book/eval-reports.json';

  let { lang = 'en' }: { lang?: Lang } = $props();

  type Report = (typeof data.reports)[number];
  const t = (iso: string) => new Date(iso).getTime();
  const ENVS = ['NoSL', 'SL'] as const;
  const colour = (env: string) => (env === 'SL' ? 'var(--v-s2)' : 'var(--v-s1)');
  const byName = new Map(data.reports.map((r) => [r.report, r]));
  const start = Math.min(...data.reports.map((r) => t(r.added.time))) - 3 * 3600e3;
  const end = Math.max(...data.champions.map((c) => t(c.time))) + 6 * 3600e3;
  // Each environment's champion: from a champion file's version to the next one, at its report's floor. A NoSL file
  // that names an SL report (dc4ee30) has no NoSL floor to draw.
  const steps = ENVS.map((env) => {
    const versions = data.champions.filter((c) => c.env === env);
    return {
      env,
      segments: versions.map((c, i) => {
        const r = byName.get(c.report)!;
        return { from: t(c.time), to: i + 1 < versions.length ? t(versions[i + 1].time) : end, report: r, own: r.env === env, gate: c.gate, commit: c.commit };
      }),
    };
  });
  const champions = new Set(data.champions.filter((c) => byName.get(c.report)!.env === c.env).map((c) => c.report));

  const W = 660;
  const H = 300;
  const M = { l: 40, r: 16, t: 14, b: 40 };
  const x = scaleUtc().domain([start, end]).range([M.l, W - M.r]);
  const y = scaleLinear().domain([5, 36]).range([H - M.b, M.t]);
  const ticks = x.ticks(8);
  const tickLabel = (d: Date) => `${String(d.getUTCMonth() + 1).padStart(2, '0')}-${String(d.getUTCDate()).padStart(2, '0')} ${String(d.getUTCHours()).padStart(2, '0')}:00`;
  let show = $state<Record<string, boolean>>({ NoSL: true, SL: true });
  let hover = $state<Report | null>(null);
</script>

<div class="vfig" style="position: relative;">
  <div class="v-row" style="margin-bottom: 0.5rem;">
    {#each ENVS as env (env)}
      <label><input type="checkbox" bind:checked={show[env]} /> <span class="v-key" style:background={colour(env)}></span> {env}</label>
    {/each}
    <span class="v-muted">
      {say(lang, w('● promotion-1 (200 seeds) ■ promotion-1-300 · hollow: void · ring: a champion’s report · line: the champion', '● promotion-1（200 个种子）■ promotion-1-300 · 空心：作废 · 外圈：当过冠军的报告 · 线：冠军', '● promotion-1（200 個種子）■ promotion-1-300 · 空心：作廢 · 外圈：當過冠軍的報告 · 線：冠軍'))}
    </span>
  </div>
  <svg viewBox="0 0 {W} {H}" width="100%" role="img" aria-label="champion timeline">
    {#each [5, 10, 15, 20, 25, 30, 35] as v (v)}
      <line x1={M.l} x2={W - M.r} y1={y(v)} y2={y(v)} stroke="var(--v-grid)" />
      <text x={M.l - 6} y={y(v) + 4} text-anchor="end">{v}</text>
    {/each}
    {#each ticks as d (d.getTime())}
      <line x1={x(d)} x2={x(d)} y1={M.t} y2={H - M.b} stroke="var(--v-grid)" />
      <text x={x(d)} y={H - M.b + 14} text-anchor="middle">{tickLabel(d)}</text>
    {/each}
    <text x={M.l + 4} y={M.t + 10}>{say(lang, w('mean floor', '平均楼层', '平均樓層'))}</text>
    <text x={W - M.r} y={H - 4} text-anchor="end">{say(lang, w('committed (UTC)', '进 git 的时间（UTC）', '進 git 的時間（UTC）'))}</text>
    {#each steps as s (s.env)}
      {#if show[s.env]}
        {#each s.segments as g, i (g.commit)}
          {#if g.own}
            <line x1={x(g.from)} x2={x(g.to)} y1={y(g.report.floorMean)} y2={y(g.report.floorMean)} stroke={colour(s.env)} stroke-width="2.5" opacity="0.8" />
          {:else if i > 0}
            {@const before = s.segments[i - 1].report.floorMean}
            <line x1={x(g.from)} x2={x(g.from)} y1={y(before) - 6} y2={y(before) + 6} stroke={colour(s.env)} stroke-width="2" />
            <text x={x(g.from) - 4} y={y(before) - 8} text-anchor="end" fill={colour(s.env)}>{say(lang, w('from here the NoSL file names the SL report', '从这里起 NoSL 文件指向 SL 报告', '從這裡起 NoSL 檔案指向 SL 報告'))}</text>
          {/if}
        {/each}
      {/if}
    {/each}
    {#each data.reports as r (r.report)}
      {#if show[r.env]}
        {@const cx = x(t(r.added.time))}
        {@const cy = y(r.floorMean)}
        <g role="presentation" onpointerenter={() => (hover = r)} onpointerleave={() => (hover = null)}>
          {#if champions.has(r.report)}<circle {cx} {cy} r="8.5" fill="none" stroke={colour(r.env)} stroke-width="1.5" />{/if}
          {#if r.seedSet === 'promotion-1-300'}
            <rect x={cx - 5} y={cy - 5} width="10" height="10" fill={r.void ? 'var(--v-surface)' : colour(r.env)} stroke={colour(r.env)} stroke-width="1.5" />
          {:else}
            <circle {cx} {cy} r="5" fill={r.void ? 'var(--v-surface)' : colour(r.env)} stroke={colour(r.env)} stroke-width="1.5" />
          {/if}
        </g>
      {/if}
    {/each}
  </svg>
  {#if hover}
    <div class="v-tip v-num" style="left: 3.5rem; top: 3rem;">
      <strong>{fmt(lang, hover.floorMean, 2)}</strong> · {hover.env} · {hover.seedSet}<br />
      {hover.label}<br />
      {say(lang, w('won', '通关', '通關'))} {hover.won}/{hover.runs} · {say(lang, w('report dated', '报告日期', '報告日期'))} {hover.date} · {say(lang, w('simulator', '模拟器', '模擬器'))} <code>{hover.commit}</code> · {say(lang, w('added in', '加入于', '加入於'))} <code>{hover.added.commit}</code>
      {#if hover.void}<br />{say(lang, w('void:', '作废：', '作廢：'))} {hover.random} {say(lang, w('random decisions', '个随机决策', '個隨機決策'))}{#if hover.failed}, {hover.failed} {say(lang, w('failed runs', '局失败', '局失敗'))}{/if}{#if hover.gameErrors}, {hover.gameErrors} {say(lang, w('game errors', '个游戏错误', '個遊戲錯誤'))}{/if}{/if}
    </div>
  {/if}
  <p class="v-caption">
    {say(lang, w(
      'Each mark is a committed report, placed at the commit that added it (UTC) and at its mean floor. Each line is what an environment’s champion file named, from the commit that wrote it to the next: NoSL from the first champion file on 2026-10-27, SL from 2026-10-29 (UTC). Since dc4ee30 both files name v6-3-2’s SL report; the NoSL line stops there, since no NoSL report of that champion exists. promotion-1-300’s first 200 seeds are promotion-1’s, so its floors compare with the circles only roughly.',
      '每个记号是一份进了 git 的报告，横坐标是加入它的 commit（UTC），纵坐标是它的平均楼层。每条线是某个环境的冠军档案指定的报告，从写入它的 commit 到下一个：NoSL 从 2026-10-27 的第一份冠军档案起，SL 从 2026-10-29（UTC）起。dc4ee30 之后两份档案都指向 v6-3-2 的 SL 报告；NoSL 那条线到那里为止，因为这个冠军还没有 NoSL 报告。promotion-1-300 的前 200 个种子就是 promotion-1 的，所以它的楼层和圆点只能粗略比较。',
      '每個記號是一份進了 git 的報告，橫座標是加入它的 commit（UTC），縱座標是它的平均樓層。每條線是某個環境的冠軍檔案指定的報告，從寫入它的 commit 到下一個：NoSL 從 2026-10-27 的第一份冠軍檔案起，SL 從 2026-10-29（UTC）起。dc4ee30 之後兩份檔案都指向 v6-3-2 的 SL 報告；NoSL 那條線到那裡為止，因為這個冠軍還沒有 NoSL 報告。promotion-1-300 的前 200 個種子就是 promotion-1 的，所以它的樓層和圓點只能粗略比較。',
    ))}
  </p>
</div>
