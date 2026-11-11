<script lang="ts">
  // The SL planner's search of one turn (docs: sl-planner/turn-search), on a made-up turn small enough to show whole:
  // depth first, ending the turn first at every step, states merged by key, the nodes shared over the first actions
  // as SlPlanner's TurnDfs shares them, and the best ends kept for the beam.
  import type { Lang } from '../v/names.ts';
  import { fmt, say, w, words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));

  type Card = { name: Record<Lang, string>; cost: number; damage: number; block: number; weak: number; poison: number };
  const CARDS: Card[] = [
    { name: w('Neutralize', '中和', '中和'), cost: 0, damage: 3, block: 0, weak: 1, poison: 0 },
    { name: w('Strike', '打击', '打擊'), cost: 1, damage: 6, block: 0, weak: 0, poison: 0 },
    { name: w('Defend', '防御', '防禦'), cost: 1, damage: 0, block: 5, weak: 0, poison: 0 },
    { name: w('Poisoned Stab', '带毒刺击', '帶毒刺擊'), cost: 1, damage: 6, block: 0, weak: 0, poison: 3 },
  ];
  const START = { energy: 2, hand: [0, 1, 2, 3], foe: 30, block: 0, weak: 0, poison: 0, hp: 50 };
  const ATTACK = 12;
  type S = typeof START;

  // The key of a state: what the player sees, with no record of the order that led to it.
  const key = (s: S) => `${s.energy}|${s.hand.join(',')}|${s.foe}|${s.block}|${s.weak}|${s.poison}`;
  const play = (s: S, c: number): S => {
    const card = CARDS[c];
    return { ...s, energy: s.energy - card.cost, hand: s.hand.filter((h) => h !== c), foe: Math.max(0, s.foe - card.damage),
      block: s.block + card.block, weak: s.weak + card.weak, poison: s.poison + card.poison };
  };
  // The enemies' turn, played for real: poison ticks, then the attack (Weak takes a quarter off).
  const endTurn = (s: S) => {
    const foe = Math.max(0, s.foe - s.poison);
    const hit = foe > 0 ? Math.max(0, Math.floor(ATTACK * (s.weak ? 0.75 : 1)) - s.block) : 0;
    return { hp: s.hp - hit, foe, poison: Math.max(0, s.poison - 1) };
  };
  // A made-up leaf: the HP kept, less what the enemy still has, plus the poison still to tick.
  const leaf = (e: { hp: number; foe: number; poison: number }) => 0.2 + 0.6 * (e.hp / 70) - 0.004 * Math.max(0, e.foe - (e.poison * (e.poison + 1)) / 2);

  type Row = { depth: number; label: string; kind: 'open' | 'merged' | 'end' | 'dup'; detail: string; value?: number; node: number; first: number };
  let nodes = $state(24);
  let keep = $state(3);

  const search = $derived.by(() => {
    const rows: Row[] = [];
    const seen = new Set<string>();
    const keys = new Set<string>();
    let used = 0;
    let limit = 0;
    let cut = false;
    const firsts: { label: string; limit: number }[] = [];
    function visit(s: S, depth: number, root: boolean, first: number) {
      if (seen.has(key(s))) return false;
      seen.add(key(s));
      const legal = [-1, ...s.hand.filter((c) => CARDS[c].cost <= s.energy)];
      let moved = false;
      legal.forEach((a, i) => {
        if (cut && !root) return;
        if (root) {
          limit = Math.max(used + 1, Math.floor((nodes * (i + 1)) / legal.length));
          firsts.push({ label: a < 0 ? t('endTurn') : say(lang, CARDS[a].name), limit });
          cut = false;
        } else if (moved && used >= limit) {
          cut = true;
          return;
        }
        moved = true;
        used++;
        const f = root ? i : first;
        if (a < 0) {
          const e = endTurn(s);
          const k = `${key(s)}>${e.hp}|${e.foe}`;
          const dup = keys.has(k);
          keys.add(k);
          rows.push({ depth, label: t('endTurn'), kind: dup ? 'dup' : 'end', detail: `HP ${e.hp}, ${t('enemy')} ${e.foe}`, value: leaf(e), node: used, first: f });
          return;
        }
        const next = play(s, a);
        const row: Row = { depth, label: say(lang, CARDS[a].name), kind: 'open', detail: `${t('energy')} ${next.energy}, ${t('enemy')} ${next.foe}${next.block ? `, ${t('block')} ${next.block}` : ''}`, node: used, first: f };
        rows.push(row);
        if (!visit(next, depth + 1, false, f)) row.kind = 'merged';
      });
      return true;
    }
    visit(START, 0, true, 0);
    const ends = rows.filter((r) => r.kind === 'end').sort((a, b) => (b.value ?? 0) - (a.value ?? 0));
    const kept = new Set(ends.slice(0, keep));
    return { rows, kept, used, firsts, ends: ends.length };
  });

  const L = {
    budget: w('nodes for the turn', '这回合的节点数', '這回合最多試幾步'),
    keep: w('states kept', '保留的状态', '留下幾個局面'),
    merged: w('seen before: merged', '已见过：合并', '見過的局面：不再算'),
    dup: w('same end as before', '和之前的结局相同', '和之前的結局相同'),
    kept: w('kept for the next turn', '留到下一回合', '留到下一回合'),
    shares: w('Where each first action must stop (k/m of the nodes)', '每个第一动作的节点上限（节点数的 k/m）', '每個開頭動作最多用到第幾步'),
    used: w('nodes used', '用掉的节点', '試了幾步'),
    ends: w('distinct turn ends', '不同的回合结局', '不同的回合結局'),
    start: w('Start: 2 energy, 50 HP, one enemy with 30 HP attacking for 12.', '开始：2 点能量、50 HP，一个敌人 30 HP，意图攻击 12。', '開始：2 點能量、50 HP，一個敵人 30 HP，意圖攻擊 12。'),
  };
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.5rem;">
    <label>{say(lang, L.budget)} {nodes} <input type="range" min="5" max="40" bind:value={nodes} /></label>
    <label>{say(lang, L.keep)} {keep} <input type="range" min="1" max="6" bind:value={keep} /></label>
  </div>
  <p class="v-ink2" style="margin-bottom: 0.25rem;">{say(lang, L.start)}</p>
  <p class="v-ink2" style="margin-bottom: 0.5rem;">
    {say(lang, L.shares)}: {#each search.firsts as f, i (i)}<span class="share">{f.label} ≤ {f.limit}</span>{/each}
  </p>
  <div class="tree">
    {#each search.rows as r, i (i)}
      <div class="row" class:merged={r.kind === 'merged'} class:dup={r.kind === 'dup'} class:kept={search.kept.has(r)} style="padding-left: {r.depth * 1.25}rem;">
        <span class="num v-muted">{r.node}</span>
        <span class="act" style="border-color: var(--v-s{(r.first % 8) + 1});">{r.label}</span>
        <span class="v-ink2">{r.detail}</span>
        {#if r.value != null}<span class="v-num val">{fmt(lang, r.value, 3)}</span>{/if}
        {#if r.kind === 'merged'}<span class="tag">{say(lang, L.merged)}</span>{/if}
        {#if r.kind === 'dup'}<span class="tag">{say(lang, L.dup)}</span>{/if}
        {#if search.kept.has(r)}<span class="tag keep">{say(lang, L.kept)}</span>{/if}
      </div>
    {/each}
  </div>
  <div class="v-row" style="margin-top: 0.5rem;">
    <div class="v-stat"><span class="v-label">{say(lang, L.used)}</span><span class="v-value v-num">{search.used}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, L.ends)}</span><span class="v-value v-num">{search.ends}</span></div>
  </div>
  <p class="v-caption">{t('illustrative')}</p>
</div>

<style>
  .tree { max-height: 24rem; overflow-y: auto; border: 1px solid var(--v-grid); border-radius: 0.375rem; padding: 0.35rem 0.5rem; }
  .row { display: flex; flex-wrap: wrap; gap: 0 0.5rem; align-items: baseline; font-size: 0.8125rem; line-height: 1.6; }
  .num { width: 1.6rem; text-align: right; font-variant-numeric: tabular-nums; font-size: 0.75rem; }
  .act { border-left: 3px solid; padding-left: 0.35rem; font-weight: 600; }
  .merged .act, .dup .act { font-weight: normal; color: var(--v-muted); }
  .val { color: var(--v-ink); }
  .tag { font-size: 0.7rem; color: var(--v-muted); border: 1px solid var(--v-grid); border-radius: 0.25rem; padding: 0 0.3rem; }
  .tag.keep { color: var(--v-ink); border-color: var(--v-s3); }
  .kept { background: var(--v-wash); }
  .share { margin-right: 0.6rem; white-space: nowrap; }
</style>
