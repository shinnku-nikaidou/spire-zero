<script lang="ts">
  // The book's example run floor by floor (src/data/book/example-run.json): each floor's room, the HP
  // the player came in with (a fight's start, else at the floor's first decision outside combat), and how many
  // decisions the floor took, outside combat and in it (with card prompts). Hovering a floor names it.
  import { scaleBand, scaleLinear } from 'd3';
  import data from '../../data/book/example-run.json';
  import { loadNames, type Lang, type Names } from '../v/names.ts';
  import { w, say } from './words.ts';
  import { COLOUR } from '../v/map.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();

  interface Floor {
    floor: number;
    act: number;
    room: string;
    macro: number;
    other: number;
    hp: number | null;
    end_hp?: number;
    max_hp: number | null;
    encounter?: string;
  }
  const FLOORS = data.floors as Floor[];
  // The floors where an act starts, less a half: the dashed lines between the acts.
  const ACT_STARTS = FLOORS.filter((f, i) => i > 0 && f.act !== FLOORS[i - 1].act).map((f) => f.floor - 0.5);

  let names = $state<Names | null>(null);
  $effect(() => {
    loadNames(lang).then((n) => (names = n));
  });

  // Map colours as the other map figures draw them (../v/map.ts); an event room takes the ? point's colour.
  const ROOMS: Record<string, { color: string; label: ReturnType<typeof w> }> = {
    Ancient: { color: COLOUR.Ancient, label: w('Ancient', '先古之民', '遠古之民') },
    Monster: { color: COLOUR.Monster, label: w('Monster', '普通战斗', '普通戰鬥') },
    Elite: { color: COLOUR.Elite, label: w('Elite', '精英', '菁英') },
    Boss: { color: COLOUR.Boss, label: w('Boss', 'Boss', '頭目') },
    Event: { color: COLOUR.Unknown, label: w('Event', '事件', '事件') },
    RestSite: { color: COLOUR.RestSite, label: w('Rest site', '休息处', '休息處') },
    Shop: { color: COLOUR.Shop, label: w('Shop', '商店', '商店') },
    Treasure: { color: COLOUR.Treasure, label: w('Treasure', '宝箱', '財寶') },
  };

  const W = 680;
  const M = { l: 40, r: 10, t: 10, b: 24 };
  const H_HP = 110;
  const H_ROOM = 18;
  const H_DEC = 90;
  const x = scaleBand<number>().domain(FLOORS.map((f) => f.floor)).range([M.l, W - M.r]).padding(0.12);
  const yHp = scaleLinear().domain([0, 70]).range([M.t + H_HP, M.t]);
  const top2 = M.t + H_HP + 8;
  const top3 = top2 + H_ROOM + 10;
  const maxDec = Math.max(...FLOORS.map((f) => f.macro + f.other));
  const yDec = scaleLinear().domain([0, maxDec]).range([top3 + H_DEC, top3]);
  const H = top3 + H_DEC + M.b;
  const hpLine = FLOORS.filter((f) => f.hp !== null)
    .map((f, i) => `${i ? 'L' : 'M'}${(x(f.floor)! + x.bandwidth() / 2).toFixed(1)},${yHp(f.hp!).toFixed(1)}`)
    .join('');

  let hover = $state<number | null>(null);
  const cur = $derived(hover === null ? null : FLOORS[hover]);

  function describe(f: Floor): string {
    const room = say(lang, ROOMS[f.room].label);
    const fight = f.encounter && names ? ` · ${names.full('encounter', f.encounter)}` : '';
    const hp = f.hp === null ? '' : f.end_hp !== undefined ? ` · HP ${f.hp} → ${f.end_hp} / ${f.max_hp}` : ` · HP ${f.hp} / ${f.max_hp}`;
    const dec = say(lang, w(` · ${f.macro} decisions outside combat, ${f.other} in combat`,
      ` · 战斗外 ${f.macro} 个决策，战斗里 ${f.other} 个`, ` · 戰鬥外 ${f.macro} 個決策，戰鬥裡 ${f.other} 個`));
    return `${say(lang, w('Floor', '第', '第'))} ${f.floor}${say(lang, w('', ' 层', ' 層'))}: ${room}${fight}${hp}${dec}`;
  }
</script>

<div class="vfig">
  <svg viewBox="0 0 {W} {H}" role="img" aria-label={say(lang, w('The example run floor by floor', '示例对局的逐层概况', '範例對局的逐層概況'))}
    onpointerleave={() => (hover = null)}>
    {#each [0, 35, 70] as v (v)}
      <line x1={M.l} x2={W - M.r} y1={yHp(v)} y2={yHp(v)} stroke="var(--v-grid)" />
      <text x={M.l - 6} y={yHp(v) + 4} text-anchor="end" class="tick">{v}</text>
    {/each}
    <text x={M.l + 4} y={M.t + 12} class="tick">HP</text>
    <path d={hpLine} fill="none" stroke="var(--v-neg)" stroke-width="2" />
    {#each ACT_STARTS as b (b)}
      <line x1={x(Math.ceil(b))! - 2} x2={x(Math.ceil(b))! - 2} y1={M.t} y2={top3 + H_DEC} stroke="var(--v-axis)" stroke-dasharray="3 3" />
    {/each}
    {#each FLOORS as f, i (f.floor)}
      <rect x={x(f.floor)} y={top2} width={x.bandwidth()} height={H_ROOM} fill={ROOMS[f.room].color} opacity={hover === null || hover === i ? 1 : 0.4} />
      <rect x={x(f.floor)} y={yDec(f.other)} width={x.bandwidth()} height={yDec(0) - yDec(f.other)} fill="var(--v-axis)" />
      <rect x={x(f.floor)} y={yDec(f.other + f.macro)} width={x.bandwidth()} height={yDec(0) - yDec(f.macro)} fill="var(--v-ink-2)" />
      <rect x={x(f.floor)! - 1} y={M.t} width={x.bandwidth() + 2} height={top3 + H_DEC - M.t} fill="transparent"
        role="presentation" onpointerenter={() => (hover = i)} />
    {/each}
    {#each [1, 10, 20, 30, 40, 48] as fl (fl)}
      <text x={x(fl)! + x.bandwidth() / 2} y={H - 8} text-anchor="middle" class="tick">{fl}</text>
    {/each}
    <text x={M.l - 6} y={yDec(maxDec) + 4} text-anchor="end" class="tick">{maxDec}</text>
  </svg>
  <div class="legend">
    {#each Object.entries(ROOMS) as [, r] (r.color)}
      <span><i style="background: {r.color}"></i>{say(lang, r.label)}</span>
    {/each}
    <span><i style="background: var(--v-ink-2)"></i>{say(lang, w('decisions outside combat', '战斗外的决策', '戰鬥外的決策'))}</span>
    <span><i style="background: var(--v-axis)"></i>{say(lang, w('decisions in combat', '战斗里的决策', '戰鬥裡的決策'))}</span>
  </div>
  <p class="v-ink2 now">{cur ? describe(cur) : say(lang, w('Point at a floor.', '指向某一层看详情。', '指向某一層看詳情。'))}</p>
  <p class="v-caption">
    {say(lang, w(
      `The book’s example run (seed ${data.source.seed}, won on floor ${data.source.floor}): what room each floor is, the HP on entering it, and which floors all ${data.source.decisions} decisions fall on. Dashed lines are the act changes.`,
      `本书的示例对局（种子 ${data.source.seed}，在第 ${data.source.floor} 层获胜）：每一层是什么房间、进房间时的生命，以及全部 ${data.source.decisions} 个决策落在哪几层。虚线是换幕。`,
      `本書的範例對局（種子 ${data.source.seed}，在第 ${data.source.floor} 層獲勝）：每一層是什麼房間、進房間時的生命，以及全部 ${data.source.decisions} 個決策落在哪幾層。虛線是換幕。`,
    ))}
  </p>
</div>

<style>
  svg {
    width: 100%;
    height: auto;
    display: block;
  }
  .tick {
    font-size: 10px;
    fill: var(--v-muted);
  }
  .legend {
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem 0.9rem;
    font-size: 0.75rem;
    margin-top: 0.4rem;
  }
  .legend i {
    display: inline-block;
    width: 0.7rem;
    height: 0.7rem;
    margin-right: 0.3rem;
    vertical-align: -0.05rem;
  }
  .now {
    min-height: 2.6em;
    margin: 0.4rem 0 0;
  }
</style>
