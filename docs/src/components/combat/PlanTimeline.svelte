<script lang="ts">
  // One real SL fight, round by round: the planner's plan from its first decision, and the fight following it.
  // Data: src/data/combat-examples.json, sl.fight.
  import { onMount } from 'svelte';
  import examples from '../../data/combat-examples.json';
  import { loadNames, type Lang, type Names } from '../v/names.ts';
  import { actionText, fmt, intentText, say, w, words, type CardRef, type Creature } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));
  let names = $state.raw<Names | null>(null);
  onMount(async () => (names = await loadNames(lang)));

  const fight = examples.sl.fight;
  type Step = (typeof fight.steps)[number];
  const rounds = (() => {
    const out: { round: number; steps: { step: Step; index: number }[] }[] = [];
    fight.steps.forEach((step, index) => {
      if (!out.length || out[out.length - 1].round !== step.obs.round) out.push({ round: step.obs.round, steps: [] });
      out[out.length - 1].steps.push({ step, index });
    });
    return out;
  })();
  let picked = $state(0);
  const shown = $derived(fight.steps[picked]);

  const L = {
    planned: w('planned once, at its first decision', '只在第一个决策规划一次', '只在第一個決策規劃一次'),
    actions: w('actions', '个动作', '個動作'),
    ended: w('End of the fight', '战斗结束', '戰鬥結束'),
    won: w('won', '获胜', '獲勝'),
    poisonLeft: w('poison', '中毒', '中毒'),
    pick: w('Click an action to see the state before it.', '点一个动作，看它之前的局面。', '點一個動作，看它之前的局面。'),
    before: w('Before', '打出之前', '打出之前'),
    wins: w('the plan wins with', '计划获胜，剩', '計畫獲勝，剩'),
    loses: w('the plan loses', '计划会输', '計畫會輸'),
  };

  const cardsOf = (s: Step) => new Map<number, CardRef>(s.obs.hand.map((c) => [c.uid as number, c as CardRef]));
  const enemiesOf = (s: Step) => new Map<number, Creature>(s.obs.enemies.map((e) => [e.uid as number, e as Creature]));
  const amount = (powers: { id: string; amount: number }[] | undefined, id: string) => powers?.find((p) => p.id === id)?.amount ?? 0;
  const pct = (a: number, b: number) => `${Math.max(0, Math.min(100, (100 * a) / b))}%`;
  const firstMs = fight.first.elapsed_ms;
  const planned = fight.first.candidates[0];
</script>

<div class="vfig">
  <p style="margin-bottom: 0.5rem;">
    <strong>{names ? names.full('encounter', fight.encounter) : fight.encounter}</strong>
    <span class="v-ink2"> · {say(lang, w('floor', '楼层', '樓層'))} {fight.floor} · HP {fight.start.hp}/{fight.start.max_hp} → {fight.end.hp}/{fight.end.max_hp}</span>
  </p>
  <p class="v-ink2" style="margin-bottom: 0.75rem;">
    {say(lang, L.planned)}: {fight.first.plan.length + 1} {say(lang, L.actions)}, {rounds.length} {t('turns')},
    {fmt(lang, firstMs / 1000, 1)} s; {planned.win ? `${say(lang, L.wins)} ${planned.hp} HP` : say(lang, L.loses)}.
  </p>
  {#if !names}
    <p class="v-muted">{t('loading')}</p>
  {:else}
    <div class="rounds">
      {#each rounds as r (r.round)}
        {@const s0 = r.steps[0].step}
        {@const foe = s0.obs.enemies[0]}
        <div class="round">
          <div class="head">
            <span class="rn">{t('round')} {r.round}</span>
            <span class="bar" title="HP {s0.obs.player.hp}/{s0.obs.player.max_hp}">
              <span class="fill me" style="width: {pct(s0.obs.player.hp, s0.obs.player.max_hp)}"></span>
              <span class="lab">{s0.obs.player.hp}/{s0.obs.player.max_hp}</span>
            </span>
            <span class="bar" title="{names.name('monster', foe.id)} {foe.hp}/{foe.max_hp}">
              <span class="fill foe" style="width: {pct(foe.hp, foe.max_hp)}"></span>
              <span class="fill psn" style="left: {pct(Math.max(0, foe.hp - amount(foe.powers, 'POISON_POWER')), foe.max_hp)}; width: {pct(Math.min(foe.hp, amount(foe.powers, 'POISON_POWER')), foe.max_hp)}"></span>
              <span class="lab">{names.name('monster', foe.id)} {foe.hp}{amount(foe.powers, 'POISON_POWER') ? ` · ${say(lang, L.poisonLeft)} ${amount(foe.powers, 'POISON_POWER')}` : ''}</span>
            </span>
            <span class="v-muted intent">{(foe.intents ?? []).map((i) => intentText(lang, i)).join(', ')}</span>
          </div>
          <div class="chips">
            {#each r.steps as { step, index } (index)}
              <button class="chip" class:searched={step.searched} aria-pressed={picked === index} onclick={() => (picked = index)}
                title={step.searched ? `${t('searched')} ${fmt(lang, step.elapsed_ms / 1000, 1)} s` : t('followed')}>
                {actionText(lang, names, step.chosen, cardsOf(step), enemiesOf(step), false)}
              </button>
            {/each}
          </div>
        </div>
      {/each}
      <div class="round">
        <div class="head">
          <span class="rn">{say(lang, L.ended)}</span>
          <span class="bar"><span class="fill me" style="width: {pct(fight.end.hp, fight.end.max_hp)}"></span><span class="lab">{fight.end.hp}/{fight.end.max_hp}</span></span>
          <span class="v-ink2">{say(lang, L.won)}</span>
        </div>
      </div>
    </div>
    <div class="detail">
      <p class="v-muted" style="margin-bottom: 0.25rem;">{say(lang, L.pick)}</p>
      <p><strong>{say(lang, L.before)}:</strong> {actionText(lang, names, shown.chosen, cardsOf(shown), enemiesOf(shown))}
        <span class="v-muted">({shown.searched ? `${t('searched')}, ${fmt(lang, shown.elapsed_ms / 1000, 1)} s` : t('followed')})</span></p>
      <p>{t('round')} {shown.obs.round} · HP {shown.obs.player.hp}/{shown.obs.player.max_hp} · {t('block')} {shown.obs.player.block ?? 0} · {t('energy')} {shown.obs.player.energy}
        {#each shown.obs.player.powers ?? [] as p (p.id)} · {names.full('power', p.id)} {p.amount}{/each}</p>
      <p>{t('hand')}: {shown.obs.hand.map((c) => names!.card(c.id, (c as CardRef).up ?? 0)).join(', ')}</p>
      {#each shown.obs.enemies as e (e.uid)}
        <p>{names.full('monster', e.id)}: {e.hp}/{e.max_hp}{e.block ? ` · ${t('block')} ${e.block}` : ''}
          · {t('intent')} {(e.intents ?? []).map((i) => intentText(lang, i)).join(', ')}
          {#each e.powers ?? [] as p (p.id)} · {names.full('power', p.id)} {p.amount}{/each}</p>
      {/each}
    </div>
  {/if}
</div>

<style>
  .rounds { display: flex; flex-direction: column; gap: 0.6rem; }
  .head { display: flex; flex-wrap: wrap; align-items: center; gap: 0.35rem 0.75rem; }
  .rn { min-width: 4.5rem; font-weight: 600; }
  .bar { position: relative; display: inline-block; width: 11rem; height: 1.1rem; background: var(--v-grid); border-radius: 3px; overflow: hidden; }
  .fill { position: absolute; top: 0; bottom: 0; left: 0; }
  .fill.me { background: var(--v-s3); opacity: 0.55; }
  .fill.foe { background: var(--v-s8); opacity: 0.4; }
  .fill.psn { background: var(--v-s6); opacity: 0.75; }
  .lab { position: relative; padding-left: 0.35rem; font-size: 0.75rem; color: var(--v-ink); white-space: nowrap; }
  .intent { font-size: 0.75rem; }
  .chips { display: flex; flex-wrap: wrap; gap: 0.25rem; margin: 0.3rem 0 0 0; padding-left: 0; }
  .chip { font-size: 0.75rem; padding: 0.1rem 0.45rem; }
  .chip.searched { border-color: var(--v-s2); }
  .detail { margin-top: 0.9rem; padding-top: 0.6rem; border-top: 1px solid var(--v-grid); }
  .detail p { margin: 0 0 0.2rem 0; }
</style>
