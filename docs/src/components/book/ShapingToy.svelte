<script lang="ts">
  // Why potential-based shaping keeps the best policy (docs: learning/loop §11): two made-up routes from the same start,
  // one that bleeds HP early and wins, one that plays safe and loses. Potential-based terms Φ(s′) − Φ(s), with
  // Φ = c × HP and Φ(end) = 0, telescope to −Φ(start) on both, so their order never changes; a raw penalty per HP lost
  // does not telescope, and past c = 1/30 it ranks the safe loss first. Illustrative values, not from a run.
  import { scaleLinear } from 'd3';
  import type { Lang } from '../v/names.ts';
  import { fmt } from '../v/i18n.ts';
  import { w, say, words, type Words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));

  interface Route {
    key: string;
    name: Words;
    hp: number[];
    reward: number;
    colour: string;
  }
  const ROUTES: Route[] = [
    { key: 'A', name: w('A: bleed in act 1, win', 'A：第 1 幕失血，最后获胜', 'A：第 1 幕失血，最後獲勝'), hp: [70, 38, 30, 45, 52], reward: 1, colour: 'var(--v-s1)' },
    { key: 'B', name: w('B: play safe, lose at the last boss', 'B：稳妥地打，输给最后的 Boss', 'B：穩妥地打，輸給最後的頭目'), hp: [70, 66, 64, 62, 60], reward: 0, colour: 'var(--v-s2)' },
  ];

  let mode = $state<'potential' | 'penalty'>('potential');
  let c = $state(0.02);

  /** Each step's shaping term: from state k to state k + 1, the last step to the run's end. */
  function terms(r: Route): number[] {
    const out: number[] = [];
    for (let k = 0; k < r.hp.length; k++) {
      const next = k + 1 < r.hp.length ? r.hp[k + 1] : null;
      if (mode === 'potential') out.push((next === null ? 0 : c * next) - c * r.hp[k]);
      else out.push(next === null ? 0 : -c * Math.max(0, r.hp[k] - next));
    }
    return out;
  }
  const rows = $derived(
    ROUTES.map((r) => {
      const f = terms(r);
      const shaping = f.reduce((a, b) => a + b, 0);
      const cumulative = f.reduce<number[]>((acc, v) => [...acc, acc[acc.length - 1] + v], [0]);
      cumulative[cumulative.length - 1] += r.reward;
      return { r, f, shaping, total: r.reward + shaping, cumulative };
    }),
  );
  const flipped = $derived(rows[1].total > rows[0].total);
  const signed = (v: number) => (v < -0.005 ? '−' : '') + fmt(lang, Math.abs(v), 2);
  // The end labels, kept 13 px apart.
  const labelY = $derived.by(() => {
    const ys = rows.map((row) => y(row.total) + 4);
    if (Math.abs(ys[0] - ys[1]) < 13) {
      const mid = (ys[0] + ys[1]) / 2, up = ys[0] <= ys[1] ? 0 : 1;
      ys[up] = mid - 6.5;
      ys[1 - up] = mid + 6.5;
    }
    return ys;
  });

  const W = 640;
  const H = 200;
  const M = { l: 44, r: 120, t: 12, b: 28 };
  const STEPS = ROUTES[0].hp.length + 1;
  const x = scaleLinear().domain([0, STEPS - 1]).range([M.l, W - M.r]);
  const lo = $derived(Math.min(-0.5, ...rows.flatMap((row) => row.cumulative)) - 0.1);
  const y = $derived(scaleLinear().domain([lo, 1.2]).range([H - M.b, M.t]));
  const ticks = $derived(y.ticks(5));
  const path = (vs: number[]) => vs.map((v, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join('');
</script>

<div class="vfig">
  <div class="v-tabs" role="group">
    <button aria-pressed={mode === 'potential'} onclick={() => (mode = 'potential')}>{say(lang, w('Potential-based: Φ(s′) − Φ(s)', '基于势能：Φ(s′) − Φ(s)', '基於勢能：Φ(s′) − Φ(s)'))}</button>
    <button aria-pressed={mode === 'penalty'} onclick={() => (mode = 'penalty')}>{say(lang, w('Raw penalty per HP lost', '每损失 1 HP 直接扣分', '每損失 1 HP 直接扣分'))}</button>
  </div>
  <div class="v-row">
    <label>
      {mode === 'potential' ? say(lang, w('Φ(s) = c × HP, c =', 'Φ(s) = c × HP，c =', 'Φ(s) = c × HP，c =')) : say(lang, w('penalty c per HP lost =', '每损失 1 HP 扣 c =', '每損失 1 HP 扣 c ='))}
      <input type="range" min="0" max="0.05" step="0.001" bind:value={c} aria-label="c" />
      <span class="v-num">{fmt(lang, c, 3)}</span>
    </label>
  </div>
  <svg viewBox="0 0 {W} {H}" width="100%" role="img" aria-label="shaping" style="margin-top: 0.5rem;">
    {#each ticks as v (v)}
      <line x1={M.l} x2={W - M.r} y1={y(v)} y2={y(v)} stroke={v === 0 ? 'var(--v-axis)' : 'var(--v-grid)'} />
      <text x={M.l - 6} y={y(v) + 4} text-anchor="end">{v}</text>
    {/each}
    {#each Array.from({ length: STEPS }, (_, i) => i) as i (i)}
      <text x={x(i)} y={H - 10} text-anchor="middle">{i === STEPS - 1 ? say(lang, w('end', '结束', '結束')) : `s${i}`}</text>
    {/each}
    {#each rows as row, k (row.r.key)}
      <path d={path(row.cumulative)} fill="none" stroke={row.r.colour} stroke-width="2" />
      {#each row.cumulative as v, i (i)}
        <circle cx={x(i)} cy={y(v)} r="3" fill={row.r.colour} />
      {/each}
      <text x={W - M.r + 8} y={labelY[k]} style="fill: {row.r.colour};">{row.r.key}: {signed(row.total)}</text>
    {/each}
  </svg>
  <p class="v-muted" style="margin: 0.25rem 0 0.5rem;">{say(lang, w('Cumulative return along each route: the shaping terms, then the outcome (1 for a win, 0 for a loss) at the end.', '每条路线上的累计回报：先是各步的塑形项，最后加上结果（赢为 1，输为 0）。', '每條路線上的累計回報：先是各步的塑形項，最後加上結果（贏為 1，輸為 0）。'))}</p>
  <div class="v-scroll">
    <table>
      <thead>
        <tr>
          <th>{say(lang, w('route', '路线', '路線'))}</th>
          <th>HP s0 … s4</th>
          <th>{say(lang, w('shaping terms, step by step', '各步的塑形项', '各步的塑形項'))}</th>
          <th>{say(lang, w('sum', '合计', '合計'))}</th>
          <th>{say(lang, w('outcome', '结果', '結果'))}</th>
          <th>{say(lang, w('return', '回报', '回報'))}</th>
        </tr>
      </thead>
      <tbody>
        {#each rows as row (row.r.key)}
          <tr>
            <td><span class="v-key" style="background: {row.r.colour};"></span> {say(lang, row.r.name)}</td>
            <td class="v-num">{row.r.hp.join(' → ')}</td>
            <td class="v-num">{row.f.map((v) => (v >= 0 ? '+' : '−') + fmt(lang, Math.abs(v), 2)).join(' ')}</td>
            <td class="v-num">{signed(row.shaping)}</td>
            <td class="v-num">{row.r.reward}</td>
            <td class="v-num"><strong>{signed(row.total)}</strong></td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
  <p style="margin-top: 0.5rem;">
    {#if mode === 'potential'}
      {say(lang, w(
        `The terms telescope: (Φ₁ − Φ₀) + (Φ₂ − Φ₁) + … + (0 − Φ₄) = −Φ₀ = −${fmt(lang, c * 70, 2)} on both routes, whatever c is. A stays ahead of B by exactly the outcomes’ difference, 1.`,
        `各项逐项相消：(Φ₁ − Φ₀) + (Φ₂ − Φ₁) + … + (0 − Φ₄) = −Φ₀，两条路线都是 −${fmt(lang, c * 70, 2)}，不论 c 取多少。A 始终比 B 高，差距正好是结果之差 1。`,
        `各項逐項相消：(Φ₁ − Φ₀) + (Φ₂ − Φ₁) + … + (0 − Φ₄) = −Φ₀，兩條路線都是 −${fmt(lang, c * 70, 2)}，不論 c 取多少。A 始終比 B 高，差距正好是結果之差 1。`,
      ))}
    {:else if flipped}
      <strong>{say(lang, w('The penalty flipped the order:', '扣分把顺序颠倒了：', '扣分把順序顛倒了：'))}</strong>
      {say(lang, w(
        'A loses 40 HP on the way and B only 10, so above c = 1/30 ≈ 0.033 the safe loss scores higher than the win. A learner shaped this way would learn to lose safely.',
        'A 一路上掉了 40 HP，B 只掉 10，所以 c 超过 1/30 ≈ 0.033 后，稳妥的输局得分高过赢局。这样塑形的学习者会学会稳稳地输。',
        'A 一路上掉了 40 HP，B 只掉 10，所以 c 超過 1/30 ≈ 0.033 後，穩妥的輸局得分高過贏局。這樣塑形的學習者會學會穩穩地輸。',
      ))}
    {:else}
      {say(lang, w(
        'The penalties do not cancel: A pays for its 40 HP lost, B for its 10 (healing earns nothing back). A still ranks first here, but past c = 1/30 ≈ 0.033 the order flips.',
        '扣分不会相消：A 为掉的 40 HP 付出代价，B 只为 10 HP（回血不加分）。这里 A 仍排第一，但 c 超过 1/30 ≈ 0.033 顺序就颠倒。',
        '扣分不會相消：A 為掉的 40 HP 付出代價，B 只為 10 HP（回血不加分）。這裡 A 仍排第一，但 c 超過 1/30 ≈ 0.033 順序就顛倒。',
      ))}
    {/if}
  </p>
  <p class="v-caption">
    {t('illustrative')}
    {say(lang, w(
      ' Two routes of five states each from the same start (70 HP); the run’s end has potential 0, the same for every ending, which the telescoping needs.',
      ' 两条路线从同一个起点（70 HP）出发，各有五个状态；一局结束时的势能为 0，每种结局都一样，逐项相消需要这一点。',
      ' 兩條路線從同一個起點（70 HP）出發，各有五個狀態；一局結束時的勢能為 0，每種結局都一樣，逐項相消需要這一點。',
    ))}
  </p>
</div>
