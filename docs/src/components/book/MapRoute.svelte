<script lang="ts">
  // A map answer's marks (docs: output §3): the map of the example run at the panel's map decision
  // (src/data/book/panel-examples.json, map), the path so far, a pulsing ring on the point the brain picked, and a line
  // along the route the brain plans after it, as MapMark draws them; the map route setting picks how much shows. The
  // setting's names are the mod's (L10n.cs).
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
  const typeAt = (a: string) => {
    const [c, r] = coord(a);
    return M.points.find((p) => p.col === c && p.row === r)!.type;
  };
  const room = (a: string) => (names ? names.name('map', typeAt(a)) : typeAt(a));
  const others = M.options.map((o) => o.action).filter((a) => a !== M.chosen);

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
    <span class="v-muted">{say(lang, w('Map route', '地图路线', '地圖路線'))}</span>
    <div class="v-tabs" style="margin: 0;">
      <button aria-pressed={route === 'off'} onclick={() => (route = 'off')}>{say(lang, w('Off', '关', '關'))}</button>
      <button aria-pressed={route === 'next'} onclick={() => (route = 'next')}>{say(lang, w('Next point', '下一个点', '下一個點'))}</button>
      <button aria-pressed={route === 'whole'} onclick={() => (route = 'whole')}>{say(lang, w('Whole route', '整条路线', '整條路線'))}</button>
    </div>
  </div>
  <svg viewBox="0 0 {W} {H}" role="img" aria-label={say(lang, w('The planned route on the map', '地图上规划的路线', '地圖上規劃的路線'))}>
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
    <text x={px(at.col) + 16} y={py(at.row) + 18} class="val muted">{say(lang, w('you are here', '你在这里', '你在這裡'))}</text>
  </svg>
  <div class="legend">
    {#each ['Monster', 'Elite', 'RestSite', 'Shop', 'Unknown', 'Treasure', 'Boss'] as ty (ty)}
      <span><span class="dot" style="border-color: {COLOUR[ty]}">{GLYPH[ty]}</span>{names ? names.name('map', ty) : ty}</span>
    {/each}
  </div>
  <p class="v-caption">
    {say(lang, w(
      `The example run (seed ${M.source.seed}) on floor ${M.source.floor}, act ${M.source.act + 1} (${M.source.hp} of ${M.source.max_hp} HP), at the map’s row ${at.row}, the decision the panel’s map answer shows. Grey: the path so far. The brain picks the ${room(M.chosen)} at row ${next[1]} over the ${others.map(room).join(', ')} beside it; the purple line is the route it plans after that pick, to the boss.`,
      `贯穿全书那一局（种子 ${M.source.seed}）在第 ${M.source.floor} 层，第 ${M.source.act + 1} 幕（HP ${M.source.hp}/${M.source.max_hp}），位于地图第 ${at.row} 行，也就是面板里地图回答的那个决策。灰线：走过的路。大脑在第 ${next[1]} 行选了${room(M.chosen)}，而不是旁边的${others.map(room).join('、')}；紫线是它在这一步之后规划的路线，一直到 Boss。`,
      `貫穿全書那一局（種子 ${M.source.seed}）在第 ${M.source.floor} 層，第 ${M.source.act + 1} 幕（HP ${M.source.hp}/${M.source.max_hp}），位於地圖第 ${at.row} 行，也就是面板裡地圖回答的那個決策。灰線：走過的路。大腦在第 ${next[1]} 行選了${room(M.chosen)}，而不是旁邊的${others.map(room).join('、')}；紫線是它在這一步之後規劃的路線，一直到頭目。`,
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
