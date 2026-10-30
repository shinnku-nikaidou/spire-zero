<script lang="ts">
  // The hand-made leaf, LeafValue v0, computed in the page as src/Sts2Ai.Brain/Combat/LeafValue.cs computes it
  // (docs: combat-brain/leaf §2): the enemies killed one after another by Smith's rule, the HP projected, scored as a win.
  import { scaleLinear } from 'd3';
  import type { Lang } from '../v/names.ts';
  import { fmt, say, w, words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));

  // LeafWeights' defaults.
  const W = { winBase: 0.2, perHp: 0.6, perPotion: 0.02, dps: 10, unblocked: 0.5, minIncoming: 3, poisonShare: 0.8 };
  const win = (s: number) => (s >= 0 ? W.winBase + W.perHp * s : W.winBase * Math.exp((W.perHp * s) / W.winBase));

  type Foe = { on: boolean; hp: number; block: number; damage: number; hits: number; poison: number };
  const PRESETS = {
    one: { hp: 60, block: 0, strength: 0, potions: 1, foes: [{ on: true, hp: 120, block: 0, damage: 16, hits: 1, poison: 0 }, { on: false, hp: 20, block: 0, damage: 6, hits: 1, poison: 0 }, { on: false, hp: 20, block: 0, damage: 6, hits: 1, poison: 0 }] },
    three: { hp: 40, block: 5, strength: 0, potions: 0, foes: [{ on: true, hp: 30, block: 0, damage: 8, hits: 1, poison: 0 }, { on: true, hp: 25, block: 0, damage: 3, hits: 2, poison: 0 }, { on: true, hp: 45, block: 6, damage: 0, hits: 1, poison: 6 }] },
  };
  const maxHp = 70;
  let hp = $state(60);
  let block = $state(0);
  let strength = $state(0);
  let potions = $state(1);
  let foes = $state<Foe[]>(structuredClone(PRESETS.one.foes));
  function preset(k: keyof typeof PRESETS) {
    const p = PRESETS[k];
    hp = p.hp;
    block = p.block;
    strength = p.strength;
    potions = p.potions;
    foes = structuredClone(p.foes);
  }

  const result = $derived.by(() => {
    const rows = foes
      .map((f, i) => {
        const poisonDamage = Math.min(f.hp, (W.poisonShare * f.poison * (f.poison + 1)) / 2);
        return { i, on: f.on && f.hp > 0, eff: Math.max(0, f.hp + f.block - poisonDamage), rate: Math.max(W.minIncoming, f.damage * f.hits), poisonDamage };
      })
      .filter((r) => r.on);
    const dps = Math.max(1, W.dps + 2 * strength);
    // Smith's rule: the highest damage rate per HP first.
    rows.sort((a, b) => b.rate / Math.max(1, b.eff) - a.rate / Math.max(1, a.eff));
    let turns = 0;
    let taken = 0;
    const order = rows.map((r) => {
      turns += r.eff / dps;
      taken += r.rate * turns;
      return { ...r, until: turns, from: r.rate * turns };
    });
    const hpLeft = hp - W.unblocked * Math.max(0, taken - block);
    const share = hpLeft / maxHp;
    return { order, dps, taken, hpLeft, share, value: win(share) + W.perPotion * potions };
  });

  const CW = 300;
  const CH = 130;
  const x = scaleLinear().domain([-1, 1]).range([34, CW - 8]);
  const y = scaleLinear().domain([0, 0.8]).range([CH - 22, 8]);
  const curve = Array.from({ length: 101 }, (_, k) => -1 + k * 0.02)
    .map((s, k) => `${k ? 'L' : 'M'}${x(s).toFixed(1)},${y(win(s)).toFixed(1)}`)
    .join('');

  const L = {
    player: w('Player', '玩家', '玩家'),
    oneFoe: w('One big enemy', '一个大怪', '一個大怪'),
    threeFoes: w('Three enemies', '三个敌人', '三個敵人'),
    eff: w('effective HP', '有效 HP', '有效 HP'),
    rate: w('damage a turn', '每回合伤害', '每回合傷害'),
    until: w('dead after (turns)', '几回合后死', '幾回合後死'),
    from: w('damage it deals', '它造成的伤害', '它造成的傷害'),
    order: w('Kill order (Smith’s rule: highest damage a turn per effective HP first)', '击杀顺序（Smith 法则：每点有效 HP 的每回合伤害最高者先杀）', '擊殺順序（Smith 法則：每點有效 HP 的每回合傷害最高者先殺）'),
    dps: w('the player’s damage a turn', '玩家每回合伤害', '玩家每回合傷害'),
    projected: w('projected HP at the end', '预计战斗结束时的 HP', '預計戰鬥結束時的 HP'),
    leaf: w('leaf value', '叶子估值', '葉節點估值'),
    share: w('projected HP share', '预计 HP 比例', '預計 HP 比例'),
    damage: w('attack', '攻击', '攻擊'),
  };
</script>

<div class="vfig">
  <div class="v-tabs">
    <button onclick={() => preset('one')}>{say(lang, L.oneFoe)}</button>
    <button onclick={() => preset('three')}>{say(lang, L.threeFoes)}</button>
  </div>
  <div class="grid">
    <div>
      <p class="sub">{say(lang, L.player)}</p>
      <label>HP {hp}/{maxHp} <input type="range" min="1" max={maxHp} bind:value={hp} /></label>
      <label>{t('block')} {block} <input type="range" min="0" max="40" bind:value={block} /></label>
      <label>{t('strength')} {strength} <input type="range" min="0" max="6" bind:value={strength} /></label>
      <label>{t('potions')} {potions} <input type="range" min="0" max="3" bind:value={potions} /></label>
    </div>
    {#each foes as f, i (i)}
      <div class:off={!f.on}>
        <p class="sub"><label><input type="checkbox" bind:checked={f.on} /> {t('enemy')} {i + 1}</label></p>
        <label>HP {f.hp} <input type="range" min="1" max="200" bind:value={f.hp} disabled={!f.on} /></label>
        <label>{t('block')} {f.block} <input type="range" min="0" max="30" bind:value={f.block} disabled={!f.on} /></label>
        <label>{say(lang, L.damage)} {f.damage}×{f.hits} <input type="range" min="0" max="40" bind:value={f.damage} disabled={!f.on} />
          <input type="range" min="1" max="4" bind:value={f.hits} disabled={!f.on} class="short" /></label>
        <label>{t('poison')} {f.poison} <input type="range" min="0" max="30" bind:value={f.poison} disabled={!f.on} /></label>
      </div>
    {/each}
  </div>

  <p class="sub" style="margin-top: 0.75rem;">{say(lang, L.order)}</p>
  <div class="v-scroll">
    <table class="order">
      <thead><tr><th></th><th>{say(lang, L.eff)}</th><th>{say(lang, L.rate)}</th><th>{say(lang, L.until)}</th><th>{say(lang, L.from)}</th></tr></thead>
      <tbody>
        {#each result.order as r (r.i)}
          <tr><td>{t('enemy')} {r.i + 1}</td><td class="v-num">{fmt(lang, r.eff, 1)}</td><td class="v-num">{fmt(lang, r.rate, 0)}</td>
            <td class="v-num">{fmt(lang, r.until, 2)}</td><td class="v-num">{fmt(lang, r.from, 1)}</td></tr>
        {/each}
      </tbody>
    </table>
  </div>

  <div class="v-row" style="margin-top: 0.75rem; align-items: flex-start;">
    <div class="v-stat"><span class="v-label">{say(lang, L.dps)}</span><span class="v-value v-num">{result.dps}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, L.projected)}</span><span class="v-value v-num">{fmt(lang, result.hpLeft, 1)}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, L.leaf)}</span><span class="v-value v-num">{fmt(lang, result.value, 4)}</span></div>
    <svg width={CW} height={CH} role="img" aria-label="{say(lang, L.leaf)} {fmt(lang, result.value, 4)}">
      <line x1={x(0)} x2={x(0)} y1="4" y2={CH - 22} stroke="var(--v-axis)" />
      <line x1="34" x2={CW - 8} y1={CH - 22} y2={CH - 22} stroke="var(--v-axis)" />
      {#each [-1, -0.5, 0, 0.5, 1] as s (s)}<text x={x(s)} y={CH - 8} text-anchor="middle">{fmt(lang, s, 1)}</text>{/each}
      {#each [0, 0.2, 0.4, 0.6, 0.8] as v (v)}<text x="28" y={y(v) + 4} text-anchor="end">{fmt(lang, v, 1)}</text>{/each}
      <path d={curve} fill="none" stroke="var(--v-s1)" stroke-width="2" />
      <circle cx={x(Math.max(-1, Math.min(1, result.share)))} cy={y(win(Math.max(-1, Math.min(1, result.share))))} r="5" fill="var(--v-s2)" stroke="var(--v-surface)" stroke-width="2" />
      <text x={CW - 8} y="14" text-anchor="end">{say(lang, L.share)}</text>
    </svg>
  </div>
</div>

<style>
  .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr)); gap: 0.5rem 1rem; }
  .grid label { display: flex; justify-content: space-between; gap: 0.4rem; font-size: 0.8125rem; }
  .grid input[type='range'] { width: 6.5rem; }
  .grid input.short { width: 2.5rem; }
  .off { opacity: 0.5; }
  .sub { color: var(--v-ink-2); font-size: 0.8125rem; margin: 0 0 0.25rem 0; font-weight: 600; }
  .order { border-collapse: collapse; width: max-content; margin: 0; }
  .order th, .order td { border: none; padding: 0.05rem 0.75rem 0.05rem 0; background: none; text-align: right; }
  .order th { color: var(--v-muted); font-weight: normal; font-size: 0.75rem; }
  .order td:first-child, .order th:first-child { text-align: left; }
</style>
