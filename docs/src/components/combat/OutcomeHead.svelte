<script lang="ts">
  // The combat net's head on the test states its file carries (weights/silent/c2.json): the chance of a death and of
  // each bucket of the HP share still to be lost, and the leaf value at LeafValue's prices, as CombatNet.Value
  // computes it (docs: combat-brain/leaf §4–§5).
  import { onMount } from 'svelte';
  import { scaleLinear } from 'd3';
  import examples from '../../data/combat-examples.json';
  import { loadNames, type Lang, type Names } from '../v/names.ts';
  import { fmt, intentText, say, w, words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));
  let names = $state.raw<Names | null>(null);
  onMount(async () => (names = await loadNames(lang)));

  const { tests, buckets, loss_min: lossMin } = examples.c2;
  const W = { winBase: 0.2, perHp: 0.6, perPotion: 0.02 };
  const width = (1 - lossMin) / buckets;
  const mid = (k: number) => lossMin + (k - 0.5) * width; // bucket k >= 1
  // The heaviest states first: the two boss fights, then the rest in file order.
  const order = tests.map((_, i) => i).sort((a, b) => tests[b].outcome[0] - tests[a].outcome[0]);
  let which = $state(order[0]);
  const s = $derived(tests[which]);

  const r = $derived.by(() => {
    const p = s.outcome;
    const share = s.player.hp / Math.max(1, s.player.max_hp);
    let value = 0;
    let won = 0;
    let lost = 0;
    for (let k = 1; k < p.length; k++) {
      value += p[k] * (W.winBase + W.perHp * (share - mid(k)));
      won += p[k];
      lost += p[k] * mid(k);
    }
    const potions = (1 - p[0]) * W.perPotion * (s.potions_held - s.potions_used);
    const lossIfWon = won > 0 ? lost / won : 0;
    // A win cannot lose more than the HP held now; the head is not told so.
    let beyond = 0;
    for (let k = 1; k < p.length; k++) if (lossMin + (k - 1) * width >= share) beyond += p[k];
    return { death: p[0], won, share, lossIfWon, beyond: won > 0 ? beyond / won : 0, endHp: (share - lossIfWon) * s.player.max_hp, value: value + potions };
  });

  const CW = 520;
  const CH = 170;
  const x = scaleLinear().domain([lossMin - 0.2, 1]).range([40, CW - 10]);
  const yMax = $derived(Math.max(0.02, ...s.outcome.slice(1)));
  const y = $derived(scaleLinear().domain([0, yMax]).range([CH - 26, 10]).nice());
  const ticks = [-0.25, 0, 0.25, 0.5, 0.75, 1];

  const L = {
    lossAxis: w('share of max HP still to be lost, if the fight is won', '若赢下战斗，从现在起还会失去的最大 HP 比例', '若贏下戰鬥，從現在起還會失去的最大 HP 比例'),
    death: w('P(death)', 'P(死亡)', 'P(死亡)'),
    lossIfWon: w('expected share lost, if won', '若获胜，预期失去的比例', '若獲勝，預期失去的比例'),
    endHp: w('expected HP at the end, if won', '若获胜，预期结束时的 HP', '若獲勝，預期結束時的 HP'),
    value: w('leaf value (LeafValue’s prices)', '叶子估值（LeafValue 的价格）', '葉節點估值（LeafValue 的價格）'),
    heals: w('heals', '回血', '回血'),
    held: w('HP held now', '现在的 HP', '現在的 HP'),
    beyond: w('of the wins: losing more than the HP held', '获胜中：失去的比现有 HP 还多', '獲勝中：失去的比現有 HP 還多'),
  };
</script>

<div class="vfig">
  {#if !names}
    <p class="v-muted">{t('loading')}</p>
  {:else}
    <div class="v-tabs">
      {#each order as i (i)}
        <button aria-pressed={which === i} onclick={() => (which = i)}>{names.name('encounter', tests[i].encounter ?? '')} · {t('round')} {tests[i].round}</button>
      {/each}
    </div>
    <p class="v-ink2" style="margin-bottom: 0.25rem;">
      HP {s.player.hp}/{s.player.max_hp} · {t('energy')} {s.player.energy} · {t('hand')}: {s.hand.map((c) => names!.name('card', c)).join(', ')}
    </p>
    {#each s.enemies as e, k (k)}
      <p class="v-ink2" style="margin-bottom: 0.25rem;">
        {names.full('monster', e.id)} {e.hp}/{e.max_hp} · {t('intent')} {(e.intents ?? []).map((i) => intentText(lang, i)).join(', ')}
        {#each e.powers ?? [] as p (p.id)} · {names.name('power', p.id)} {p.amount}{/each}
      </p>
    {/each}
    <svg viewBox="0 0 {CW} {CH}" style="width: 100%; max-width: {CW}px; margin-top: 0.5rem;" role="img" aria-label="{say(lang, L.death)} {fmt(lang, r.death, 3)}">
      <line x1="40" x2={CW - 10} y1={CH - 26} y2={CH - 26} stroke="var(--v-axis)" />
      {#each y.ticks(4) as v (v)}
        <line x1="40" x2={CW - 10} y1={y(v)} y2={y(v)} stroke="var(--v-grid)" />
        <text x="34" y={y(v) + 4} text-anchor="end">{fmt(lang, v, 3)}</text>
      {/each}
      <!-- the death class, drawn at its own place left of the buckets, clipped to the axis -->
      <rect x={x(lossMin - 0.19)} width={x(lossMin - 0.11) - x(lossMin - 0.19)} y={y(Math.min(yMax, s.outcome[0]))} height={CH - 26 - y(Math.min(yMax, s.outcome[0]))} fill="var(--v-neg)">
        <title>{say(lang, L.death)} {fmt(lang, s.outcome[0], 3)}</title>
      </rect>
      <text x={x(lossMin - 0.15)} y={CH - 12} text-anchor="middle">{t('death')}</text>
      {#if s.outcome[0] > yMax}
        <text x={x(lossMin - 0.15)} y={y(yMax) - 2} text-anchor="middle">{fmt(lang, s.outcome[0], 2)}</text>
      {/if}
      <line x1={x(r.share)} x2={x(r.share)} y1="8" y2={CH - 26} stroke="var(--v-ink)" stroke-dasharray="3 3" />
      <text x={x(r.share) + 4} y="34">{say(lang, L.held)}</text>
      {#each s.outcome.slice(1) as p, k (k)}
        <rect x={x(lossMin + k * width) + 0.5} width={Math.max(0.5, x(lossMin + (k + 1) * width) - x(lossMin + k * width) - 1)} y={y(p)} height={CH - 26 - y(p)} fill="var(--v-s1)">
          <title>{fmt(lang, lossMin + k * width, 3)}–{fmt(lang, lossMin + (k + 1) * width, 3)}: {fmt(lang, p, 4)}</title>
        </rect>
      {/each}
      {#each ticks as v (v)}
        <text x={x(v)} y={CH - 12} text-anchor="middle">{fmt(lang, v, 2)}</text>
      {/each}
      <text x={x(-0.125)} y="20" text-anchor="middle">← {say(lang, L.heals)}</text>
    </svg>
    <p class="v-caption" style="margin-top: 0;">{say(lang, L.lossAxis)}</p>
    <div class="v-row">
      <div class="v-stat"><span class="v-label">{say(lang, L.death)}</span><span class="v-value v-num">{fmt(lang, r.death, 3)}</span></div>
      <div class="v-stat"><span class="v-label">{say(lang, L.lossIfWon)}</span><span class="v-value v-num">{fmt(lang, r.lossIfWon, 3)}</span></div>
      <div class="v-stat"><span class="v-label">{say(lang, L.endHp)}</span><span class="v-value v-num">{fmt(lang, r.endHp, 1)}</span></div>
      <div class="v-stat"><span class="v-label">{say(lang, L.beyond)}</span><span class="v-value v-num">{fmt(lang, r.beyond, 3)}</span></div>
      <div class="v-stat"><span class="v-label">{say(lang, L.value)}</span><span class="v-value v-num">{fmt(lang, r.value, 4)}</span></div>
    </div>
  {/if}
</div>
