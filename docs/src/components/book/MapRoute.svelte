<script lang="ts">
  // A map answer's marks (docs: output §3): the act-3 map of the example run's floor 43, the path so far, a pulsing ring on
  // the point the brain picked, and a line along each path of the route after it, as MapMark draws them; the map
  // route setting picks how much shows. Each option is labelled with V's value of its predicted afterstate.
  import data from '../../data/book/panel-examples.json';
  import { COLOUR, GLYPH } from '../v/map.ts';
  import { loadNames, type Lang, type Names } from '../v/names.ts';
  import { w, say } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  let names = $state<Names | null>(null);
  $effect(() => {
    loadNames(lang).then((n) => (names = n));
  });

  const M = data.map;
  let route = $state<'off' | 'next' | 'whole'>('whole');

  const ROWS = Math.max(...M.points.map((p) => p.row));
  const DX = 46;
  const DY = 30;
  const W = 16 + 6 * DX + 120;
  const H = 24 + ROWS * DY + 20;
  const px = (col: number) => 24 + col * DX;
  const py = (row: number) => 20 + (ROWS - row) * DY;
  const key = (c: number, r: number) => `${c},${r}`;
  const coord = (a: string) => a.slice(4).split(',').map(Number) as [number, number];

  const visited = new Set(M.visited.map((v) => key(v.col, v.row)));
  const walked = M.visited.slice(1).map((v, i) => [M.visited[i], v]);
  const at = M.visited[M.visited.length - 1];
  const stops = [[at.col, at.row], ...[M.chosen, ...M.route].map(coord)];
  const next = coord(M.chosen);
  const value = Object.fromEntries(M.options.map((o) => [o.action.slice(4), o.value]));

  let t = $state(0);
  $effect(() => {
    let frame = requestAnimationFrame(function tick(ms) {
      t = ms;
      frame = requestAnimationFrame(tick);
    });
    return () => cancelAnimationFrame(frame);
  });
  const pulse = $derived(1 + 0.1 * Math.sin((t / 1000) * 2 * Math.PI));
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.6rem;">
    <span class="v-muted">Map route</span>
    <div class="v-tabs" style="margin: 0;">
      <button aria-pressed={route === 'off'} onclick={() => (route = 'off')}>Off</button>
      <button aria-pressed={route === 'next'} onclick={() => (route = 'next')}>Next point</button>
      <button aria-pressed={route === 'whole'} onclick={() => (route = 'whole')}>Whole route</button>
    </div>
  </div>
  <svg viewBox="0 0 {W} {H}" role="img" aria-label={say(lang, w('The route on the act 3 map', '第三幕地图上的路线', '第三幕地圖上的路線'))}>
    {#each M.points as p (key(p.col, p.row))}
      {#each p.next as n (key(n.col, n.row))}
        <line x1={px(p.col)} y1={py(p.row)} x2={px(n.col)} y2={py(n.row)} stroke="var(--v-grid)" stroke-width="1.5" />
      {/each}
    {/each}
    {#each walked as [a, b], i (i)}
      <line x1={px(a.col)} y1={py(a.row)} x2={px(b.col)} y2={py(b.row)} stroke="var(--v-ink-2)" stroke-width="2.5" />
    {/each}
    {#if route === 'whole'}
      {#each stops.slice(1) as s, i (i)}
        <line x1={px(stops[i][0])} y1={py(stops[i][1])} x2={px(s[0])} y2={py(s[1])} stroke="#bf59ff" stroke-opacity="0.8" stroke-width="5" stroke-linecap="round" />
      {/each}
    {/if}
    {#each M.points as p (key(p.col, p.row))}
      {@const seen = visited.has(key(p.col, p.row))}
      <g transform="translate({px(p.col)},{py(p.row)})">
        <circle r="10" fill="var(--v-surface)" stroke={seen ? 'var(--v-ink-2)' : COLOUR[p.type] ?? 'var(--v-muted)'} stroke-width={seen ? 1 : 2} />
        <text text-anchor="middle" y="3.5" class="glyph" fill={seen ? 'var(--v-muted)' : 'var(--v-ink)'}>{GLYPH[p.type] ?? '·'}</text>
      </g>
    {/each}
    {#if route !== 'off'}
      <circle cx={px(next[0])} cy={py(next[1])} r={17 * pulse} fill="none" stroke="#bf59ff" stroke-width="4" />
    {/if}
    {#each Object.entries(value) as [c, v] (c)}
      {@const [col, row] = c.split(',').map(Number)}
      <text x={px(col) + 22} y={py(row) + 4} class="val">V {v.toFixed(2)}</text>
    {/each}
    <text x={px(at.col) + 16} y={py(at.row) + 18} class="val muted">{say(lang, w('you are here', '你在这里', '你在這裡'))}</text>
  </svg>
  <div class="legend">
    {#each ['Monster', 'Elite', 'RestSite', 'Shop', 'Unknown', 'Treasure', 'Boss'] as ty (ty)}
      <span><span class="dot" style="border-color: {COLOUR[ty]}">{GLYPH[ty]}</span>{names ? names.name('map', ty) : ty}</span>
    {/each}
  </div>
  <p class="v-caption">
    {say(lang, w(
      `The example run (seed ${M.source.seed}) on floor ${M.source.floor}, act 3 (${M.source.hp} of ${M.source.max_hp} HP), at the map’s row 9. Grey: the path so far. The brain picks the monster at row 10 over the rest site beside it; the purple line is the route the run then took, through an elite and a rest site to the boss. V’s values are its expected floor on each option’s afterstate, as predicted from the screen (sim afterstates); the panel does not show them.`,
      `贯穿全书那一局（种子 ${M.source.seed}）在第 ${M.source.floor} 层，第三幕（HP ${M.source.hp}/${M.source.max_hp}），位于地图第 9 行。灰线：走过的路。大脑在第 10 行选了普通战斗，而不是旁边的休息处；紫线是这一局后来实际走的路线，经过一场精英战和一个休息处到 Boss。V 的值是每个选项的后继状态的预期楼层，由画面预测（sim afterstates）；面板不显示这些值。`,
      `貫穿全書那一局（種子 ${M.source.seed}）在第 ${M.source.floor} 層，第三幕（HP ${M.source.hp}/${M.source.max_hp}），位於地圖第 9 行。灰線：走過的路。大腦在第 10 行選了普通戰鬥，而不是旁邊的休息處；紫線是這一局後來實際走的路線，經過一場菁英戰和一個休息處到頭目。V 的值是每個選項的後繼狀態的預期樓層，由畫面預測（sim afterstates）；面板不顯示這些值。`,
    ))}
  </p>
</div>

<style>
  svg {
    width: 100%;
    max-width: 460px;
    height: auto;
    display: block;
    margin: 0 auto;
  }
  .glyph {
    font-size: 10px;
    font-weight: 600;
  }
  .val {
    font-size: 10px;
    fill: var(--v-ink);
  }
  .val.muted {
    fill: var(--v-muted);
  }
  .legend {
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem 0.9rem;
    font-size: 0.75rem;
    margin-top: 0.4rem;
    justify-content: center;
  }
  .dot {
    display: inline-block;
    width: 1.1rem;
    height: 1.1rem;
    line-height: 0.95rem;
    border: 2px solid;
    border-radius: 50%;
    text-align: center;
    font-size: 0.6rem;
    font-weight: 600;
    margin-right: 0.3rem;
    box-sizing: border-box;
  }
</style>
