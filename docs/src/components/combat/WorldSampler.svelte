<script lang="ts">
  // Determinized worlds of one real NoSL decision: the draw pile the player sees as a set, put in an
  // order that depends only on what is seen, then shuffled with each world's seed. Drawn here in the page with its own
  // generator, not the game's; the run's own 8 worlds are those of the next section's table.
  import { onMount } from 'svelte';
  import examples from '../../data/combat-examples.json';
  import { loadNames, type Lang, type Names } from '../v/names.ts';
  import { fmt, say, w, words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));
  let names = $state.raw<Names | null>(null);
  onMount(async () => (names = await loadNames(lang)));

  const { obs, draw } = examples.nosl;
  const pile = [...draw].sort(); // sorted by card key first: an order that depends only on what the player sees
  let seed = $state(1);
  let worlds = $state(8);
  let n = $state(5);

  function mulberry(a: number) {
    return () => {
      a |= 0;
      a = (a + 0x6d2b79f5) | 0;
      let r = Math.imul(a ^ (a >>> 15), 1 | a);
      r = (r + Math.imul(r ^ (r >>> 7), 61 | r)) ^ r;
      return ((r ^ (r >>> 14)) >>> 0) / 4294967296;
    };
  }
  // World k's draw order: Fisher–Yates with a seed mixed from the search seed and k.
  const shuffled = (k: number) => {
    const r = mulberry(seed * 1000003 + k * 7919);
    const p = [...pile];
    for (let i = p.length - 1; i > 0; i--) {
      const j = Math.floor(r() * (i + 1));
      [p[i], p[j]] = [p[j], p[i]];
    }
    return p;
  };
  const orders = $derived(Array.from({ length: worlds }, (_, k) => shuffled(k)));
  const ids = [...new Set(pile)];
  const choose = (a: number, b: number) => {
    if (b < 0 || b > a) return 0;
    let r = 1;
    for (let i = 1; i <= b; i++) r = (r * (a - b + i)) / i;
    return r;
  };
  // The chance that at least one copy is among the next n draws: exact, and as these worlds say.
  const rows = $derived(ids.map((id) => {
    const c = pile.filter((p) => p === id).length;
    const exact = 1 - choose(pile.length - c, n) / choose(pile.length, n);
    const seen = orders.filter((o) => o.slice(0, n).includes(id)).length / orders.length;
    return { id, c, exact, seen };
  }));

  const L = {
    pileSeen: w('What the player sees of the draw pile: 11 cards, order unknown', '玩家看到的抽牌堆：11 张牌，不知道顺序', '玩家看到的抽牌堆：11 張牌，不知道順序'),
    seed: w('search seed', '搜索种子', '搜尋種子'),
    again: w('New seed', '换一个种子', '換一個種子'),
    next: w('next draws', '接下来抽到的牌', '接下來抽到的牌'),
    chance: w('Chance to be among the next draws', '出现在接下来几张里的概率', '出現在接下來幾張裡的機率'),
    exact: w('exact', '精确值', '精確值'),
    inWorlds: w('in these worlds', '这些世界里', '這些世界裡'),
    copies: w('copies', '张', '張'),
  };
</script>

<div class="vfig">
  {#if !names}
    <p class="v-muted">{t('loading')}</p>
  {:else}
    <p class="v-ink2" style="margin-bottom: 0.25rem;">{t('hand')}: {obs.hand.map((c) => names!.name('card', c.id)).join(', ')}</p>
    <p class="v-ink2" style="margin-bottom: 0.5rem;">{say(lang, L.pileSeen)}: {ids.map((id) => `${names!.name('card', id)} ×${pile.filter((p) => p === id).length}`).join(', ')}</p>
    <div class="v-row" style="margin-bottom: 0.5rem;">
      <label>{say(lang, L.seed)} {seed} <button onclick={() => (seed += 1)}>{say(lang, L.again)}</button></label>
      <label>{t('worlds')} {worlds} <input type="range" min="1" max="64" bind:value={worlds} /></label>
      <label>{say(lang, L.next)} {n} <input type="range" min="1" max="10" bind:value={n} /></label>
    </div>
    <div class="v-scroll worlds">
      {#each orders.slice(0, 8) as o, k (k)}
        <div class="wrow">
          <span class="wk" style="border-color: var(--v-s{k + 1});">{t('world')} {k + 1}</span>
          {#each o as id, i (i)}
            <span class="card" class:soon={i < n}>{names.name('card', id)}</span>
          {/each}
        </div>
      {/each}
      {#if worlds > 8}<p class="v-muted" style="margin: 0.25rem 0 0 0;">… {worlds - 8}</p>{/if}
    </div>
    <p class="sub">{say(lang, L.chance)} ({n})</p>
    <div class="v-scroll">
      <table class="ch">
        <thead><tr><th></th><th>{say(lang, L.exact)}</th><th>{say(lang, L.inWorlds)}</th></tr></thead>
        <tbody>
          {#each rows as r (r.id)}
            <tr>
              <td>{names.name('card', r.id)} ×{r.c}</td>
              <td class="v-num">{fmt(lang, r.exact, 2)}</td>
              <td>
                <svg width="160" height="14" role="img" aria-label="{fmt(lang, r.seen, 2)}">
                  <rect x="0" y="3" width={160 * r.seen} height="8" rx="2" fill="var(--v-s1)" />
                  <line x1={160 * r.exact} x2={160 * r.exact} y1="0" y2="14" stroke="var(--v-ink)" stroke-width="1.5" />
                </svg>
                <span class="v-num">{fmt(lang, r.seen, 2)}</span>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</div>

<style>
  .worlds { display: flex; flex-direction: column; gap: 0.2rem; }
  .wrow { display: flex; gap: 0.25rem; align-items: center; white-space: nowrap; font-size: 0.75rem; }
  .wk { border-left: 3px solid; padding-left: 0.3rem; min-width: 4.2rem; color: var(--v-ink-2); }
  .card { border: 1px solid var(--v-grid); border-radius: 0.25rem; padding: 0 0.3rem; color: var(--v-muted); }
  .card.soon { color: var(--v-ink); border-color: var(--v-s1); background: var(--v-wash); }
  .sub { color: var(--v-ink-2); font-size: 0.8125rem; margin: 0.9rem 0 0.3rem 0; font-weight: 600; }
  .ch { border-collapse: collapse; width: max-content; margin: 0; }
  .ch th, .ch td { border: none; padding: 0.05rem 0.75rem 0.05rem 0; background: none; font-size: 0.8125rem; vertical-align: middle; }
  .ch th { color: var(--v-muted); font-weight: normal; font-size: 0.75rem; text-align: left; }
</style>
