<script lang="ts">
  // The elite gauntlet's champions and reference lines: progress on the eval seeds 300000–300499 (500 runs each),
  // ± one standard error (eval/silent/gauntlet-leaderboard.md); and the whole SL runs of plan v6's last three nets
  // on promotion-1-300 (eval/silent/leaderboard.md) (docs: macro-brain/selfplay §4).
  import { scaleLinear } from 'd3';
  import { fmt, words } from './i18n.ts';
  import type { Lang } from './names.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));
  const RANDOM: Record<Lang, string> = { en: 'every decision drawn evenly', 'zh-CN': '每个决策都均匀乱抽', 'zh-TW': '每個決策都均勻亂抽' };
  const ROWS = [
    { name: 'random', progress: 21.89, se: 0.35, bosses: [178 / 442, 5 / 109, 0 / 2], won: 0 },
    { name: 'n2-b0.03', progress: 33.71, se: 0.35, bosses: [441 / 499, 81 / 429, 1 / 56], won: 1 },
    { name: 'pure-1-g', progress: 34.47, se: 0.33, bosses: [469 / 497, 97 / 412, 2 / 61], won: 2 },
    { name: 'v6-1', progress: 36.07, se: 0.34, bosses: [475 / 499, 122 / 454, 10 / 103], won: 10 },
    { name: 'v6-2', progress: 40.69, se: 0.38, bosses: [483 / 500, 262 / 479, 68 / 244], won: 68 },
    { name: 'v6-3-1', progress: 42.42, se: 0.43, bosses: [469 / 496, 335 / 458, 154 / 324], won: 154 },
    { name: 'v6-3-2', progress: 43.36, se: 0.38, bosses: [480 / 499, 352 / 471, 173 / 341], won: 173 },
  ];
  const WHOLE = [
    { name: 'v6-2', floor: 31.37, se: 0.48, won: 1 },
    { name: 'v6-3-1', floor: 31.11, se: 0.63, won: 13 },
    { name: 'v6-3-2', floor: 33.56, se: 0.58, won: 23 },
  ];
  const W = 640;
  const M = { l: 190, r: 70 };
  const RH = 26;
  const x = scaleLinear().domain([20, 46]).range([M.l, W - M.r]);
  let hover = $state<number | null>(null);
</script>

<div class="vfig" style="position: relative;">
  <svg viewBox="0 0 {W} {ROWS.length * RH + 30}" width="100%" role="img" aria-label={t('progress')}>
    {#each [20, 25, 30, 35, 40, 45] as tick (tick)}
      <line x1={x(tick)} x2={x(tick)} y1="0" y2={ROWS.length * RH} stroke="var(--v-grid)" />
      <text x={x(tick)} y={ROWS.length * RH + 14} text-anchor="middle">{tick}</text>
    {/each}
    <text x={W - M.r} y={ROWS.length * RH + 28} text-anchor="end">{t('progress')}</text>
    {#each ROWS as r, i (r.name)}
      {@const cy = i * RH + RH / 2}
      <g onpointerenter={() => (hover = i)} onpointerleave={() => (hover = null)} role="presentation">
        <rect x="0" y={i * RH} width={W} height={RH} fill={hover === i ? 'var(--v-wash)' : 'transparent'} />
        <text x={M.l - 10} y={cy + 4} text-anchor="end" style="font-size: 12px;" fill="var(--v-ink)">{r.name === 'random' ? RANDOM[lang] : r.name}</text>
        <line x1={x(r.progress - 1.96 * r.se)} x2={x(r.progress + 1.96 * r.se)} y1={cy} y2={cy} stroke="var(--v-s1)" stroke-width="2" />
        <circle cx={x(r.progress)} cy={cy} r="5" fill="var(--v-s1)" stroke="var(--v-surface)" stroke-width="2" />
        <text x={x(r.progress) + 10} y={cy - 6} class="v-num">{fmt(lang, r.progress, 2)}</text>
      </g>
    {/each}
  </svg>
  {#if hover !== null}
    {@const r = ROWS[hover]}
    <div class="v-tip v-num" style="right: 1rem; top: 0.5rem;">
      <strong>{fmt(lang, r.progress, 2)} ± {fmt(lang, r.se, 2)}</strong> · {r.name === 'random' ? RANDOM[lang] : r.name}<br />
      {t('bossFloor')} 1 / 2 / 3: {r.bosses.map((b) => `${Math.round(100 * b)}%`).join(' / ')} · {t('won')} {r.won}/500
    </div>
  {/if}
  <div class="v-scroll" style="margin-top: 0.75rem;">
    <table>
      <thead>
        <tr><th></th><th>{t('bossFloor')} 1</th><th>{t('bossFloor')} 2</th><th>{t('bossFloor')} 3</th><th>{t('won')} / 500</th></tr>
      </thead>
      <tbody>
        {#each ROWS as r (r.name)}
          <tr>
            <td>{r.name === 'random' ? RANDOM[lang] : r.name}</td>
            {#each r.bosses as b, j (j)}<td class="v-num">{Math.round(100 * b)}%</td>{/each}
            <td class="v-num">{r.won}</td>
          </tr>
        {/each}
      </tbody>
    </table>
    <table>
      <thead><tr><th></th><th>{t('floor')}</th><th>{t('won')} / 300</th></tr></thead>
      <tbody>
        {#each WHOLE as r (r.name)}
          <tr><td>{r.name}</td><td class="v-num">{fmt(lang, r.floor, 2)} ± {fmt(lang, r.se, 2)}</td><td class="v-num">{r.won}</td></tr>
        {/each}
      </tbody>
    </table>
  </div>
</div>
