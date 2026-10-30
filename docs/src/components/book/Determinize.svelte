<script lang="ts">
  // Determinization on a real start of a round (src/data/book/round-start.json, from c2's test states): the player
  // sees the draw pile as a multiset; each world is one order of it, drawn the way Determinize draws one (sort by card
  // key, then a Fisher–Yates shuffle), here with the page's own generator (docs: environments/worlds §2).
  import data from '../../data/book/round-start.json';
  import { loadNames, type Lang, type Names } from '../v/names.ts';
  import { w, say, words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));

  interface Card {
    id: string;
    up?: number;
    uid?: number;
    enchant?: { id: string; amount?: number };
  }
  const obs = data.obs as unknown as { hand: Card[]; draw: Card[]; enemies: { id: string; hp: number; max_hp: number }[]; player: { hp: number; max_hp: number }; round: number };
  const key = (c: Card) => `${c.id}+${c.up ?? 0}${c.enchant ? '@' + c.enchant.id : ''}`;
  const sorted = [...obs.draw].sort((a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0));
  const groups = [...new Map(sorted.map((c) => [key(c), { card: c, n: sorted.filter((d) => key(d) === key(c)).length }])).values()];
  const TOP = 5;

  let names = $state<Names | null>(null);
  $effect(() => {
    loadNames(lang).then((v) => (names = v));
  });
  const name = (c: Card) => (names ? names.card(c.id, c.up ?? 0) : c.id + (c.up ? '+' : '')) + (c.enchant ? ` · ${names ? names.name('enchant', c.enchant.id) : c.enchant.id}` : '');
  const short = (c: Card) => (names ? names.name('card', c.id) : c.id) + (c.up ? '+' : '');

  // mulberry32: a small seeded generator, so a page shows the same worlds for the same seed.
  function rng(seed: number) {
    let a = seed >>> 0;
    return (n: number) => {
      a = (a + 0x6d2b79f5) >>> 0;
      let x = a;
      x = Math.imul(x ^ (x >>> 15), x | 1);
      x ^= x + Math.imul(x ^ (x >>> 7), x | 61);
      return Math.floor((((x ^ (x >>> 14)) >>> 0) / 4294967296) * n);
    };
  }
  function world(seed: number): Card[] {
    const next = rng(seed);
    const cards = [...sorted];
    for (let i = cards.length - 1; i > 0; i--) {
      const j = next(i + 1);
      [cards[i], cards[j]] = [cards[j], cards[i]];
    }
    return cards;
  }

  let k = $state(8);
  let seed = $state(1);
  const worlds = $derived(Array.from({ length: k }, (_, i) => world(seed * 1000 + i)));

  // The chance that at least one of m copies is among the top TOP of N cards: 1 − C(N−m, TOP) / C(N, TOP).
  function choose(n: number, r: number) {
    if (r < 0 || r > n) return 0;
    let v = 1;
    for (let i = 1; i <= r; i++) v = (v * (n - r + i)) / i;
    return v;
  }
  const N = sorted.length;
  const exact = (m: number) => 1 - choose(N - m, TOP) / choose(N, TOP);
  const share = (g: (typeof groups)[number]) => worlds.filter((ws) => ws.slice(0, TOP).some((c) => key(c) === key(g.card))).length / k;
  const pct = (x: number) => `${Math.round(x * 100)}%`;
  const who = $derived(names ? names.name('character', 'SILENT') : 'SILENT');
  const foe = $derived(names ? names.full('monster', obs.enemies[0].id) : obs.enemies[0].id);
</script>

<div class="vfig">
  <div class="v-row" style="align-items: flex-start;">
    <div style="flex: 1 1 16rem;">
      <strong>{t('hand')}</strong>
      <div class="v-muted" style="margin-bottom: 0.5rem;">{obs.hand.map((c) => `${short(c)} #${c.uid}`).join(' · ')}</div>
      <strong>{t('drawPile')}</strong>
      <span class="v-muted">— {say(lang, w('what the player sees: which cards, in no order', '玩家看到的：有哪些牌，没有顺序', '玩家看到的：有哪些牌，沒有順序'))}</span>
      <ul style="margin: 0.25rem 0 0; padding-left: 1.1rem;">
        {#each groups as g (key(g.card))}<li>{g.n} × {name(g.card)}</li>{/each}
      </ul>
    </div>
    <div style="flex: 1 1 16rem;">
      <div class="v-row" style="margin-bottom: 0.5rem;">
        <span>K =</span>
        {#each [1, 4, 8, 32] as v (v)}<button aria-pressed={k === v} onclick={() => (k = v)}>{v}</button>{/each}
        <button onclick={() => seed++}>{t('again')}</button>
      </div>
      <div class="v-scroll" style="max-height: 14rem; overflow-y: auto;">
        {#each worlds as ws, i (i)}
          <div class="v-num" style="font-size: 0.8125rem; white-space: nowrap;">
            <span class="v-muted">{t('world')} {i + 1}:</span> {ws.slice(0, TOP).map(short).join(' · ')}
          </div>
        {/each}
      </div>
    </div>
  </div>

  <table style="width: 100%; margin-top: 0.75rem; border-collapse: collapse;">
    <thead>
      <tr>
        <th style="text-align: left;">{say(lang, w('Card', '牌', '牌'))}</th>
        <th style="text-align: right;">{say(lang, w('true chance it is in the top five', '在最上面五张里的真实概率', '在最上面五張裡的真實機率'))}</th>
        <th style="text-align: right;">{say(lang, w(`share of the ${k} worlds`, `${k} 个世界中的比例`, `${k} 個世界中的比例`))}</th>
      </tr>
    </thead>
    <tbody class="v-num">
      {#each groups as g (key(g.card))}
        <tr>
          <td>{name(g.card)}</td>
          <td style="text-align: right;">{pct(exact(g.n))}</td>
          <td style="text-align: right;">{pct(share(g))}</td>
        </tr>
      {/each}
    </tbody>
  </table>
  <p class="v-caption">
    {say(
      lang,
      w(
        `Round ${obs.round} of a real combat from NoSL self-play: ${who} at ${obs.player.hp}/${obs.player.max_hp} HP against ${foe} at ${obs.enemies[0].hp}/${obs.enemies[0].max_hp}, one of the held-out round starts in the combat net c2’s file. Each world sorts the pile by card key and shuffles it, as Determinize does; this page shuffles with its own generator, where a search’s worlds use the game’s Rng and the search’s seed. SL needs no worlds here: its one world, the node’s clone, holds the real order.`,
        `NoSL 自我对弈里一场真实战斗的第 ${obs.round} 回合：${who} ${obs.player.hp}/${obs.player.max_hp} HP，对上 ${obs.enemies[0].hp}/${obs.enemies[0].max_hp} 的 ${foe}，是战斗网络 c2 文件里留出的回合起点之一。每个世界先按卡牌键排序再洗牌，和 Determinize 一样；本页用自己的生成器洗牌，搜索的世界则用游戏的 Rng 和搜索的种子。SL 在这里不需要世界：它唯一的世界，也就是节点的克隆，握有真实顺序。`,
        `NoSL 自我對弈裡一場真實戰鬥的第 ${obs.round} 回合：${who} ${obs.player.hp}/${obs.player.max_hp} HP，對上 ${obs.enemies[0].hp}/${obs.enemies[0].max_hp} 的 ${foe}，是戰鬥網路 c2 檔案裡留出的回合起點之一。每個世界先按卡牌鍵排序再洗牌，和 Determinize 一樣；本頁用自己的產生器洗牌，搜尋的世界則用遊戲的 Rng 和搜尋的種子。SL 在這裡不需要世界：它唯一的世界，也就是節點的複本，握有真實順序。`,
      ),
    )}
  </p>
</div>

<style>
  th,
  td {
    padding: 0.25rem 0.4rem;
    border-top: 1px solid var(--v-grid);
  }
  th {
    color: var(--v-ink-2);
    font-weight: 500;
  }
</style>
