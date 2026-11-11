<script lang="ts">
  // One real NoSL decision: each first action's best line in each of the 8 worlds, their mean and 95% interval as
  // PimcBrain.Summarize makes them, two actions compared world by world (common random numbers), and what a player
  // who knew the world would get (docs: pimc/search §2–§4). Data: src/data/combat-examples.json, nosl.
  import { onMount } from 'svelte';
  import { scaleLinear } from 'd3';
  import examples from '../../data/combat-examples.json';
  import { loadNames, type Lang, type Names } from '../v/names.ts';
  import { actionText, fmt, intentText, say, w, words, type CardRef, type Creature } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));
  let names = $state.raw<Names | null>(null);
  onMount(async () => (names = await loadNames(lang)));

  const { decision, obs } = examples.nosl;
  const cands = decision.candidates;
  const K = cands[0].values.length;
  const cards = new Map<number, CardRef>(obs.hand.map((c) => [c.uid as number, c as CardRef]));
  const foes = new Map<number, Creature>(obs.enemies.map((e) => [e.uid as number, e as Creature]));
  const label = (a: string, full = false) => (names ? actionText(lang, names, a, cards, foes, full) : a);

  const meanOf = (xs: number[]) => xs.reduce((s, v) => s + v, 0) / xs.length;
  // As PimcBrain.Summarize: 1.96 standard errors, from the sample variance.
  const ciOf = (xs: number[]) => {
    const m = meanOf(xs);
    return 1.96 * Math.sqrt(xs.reduce((s, v) => s + (v - m) ** 2, 0) / (xs.length - 1) / xs.length);
  };

  let a = $state(cands.findIndex((c) => c.action === 'play:1:1'));
  let b = $state(cands.findIndex((c) => c.action === 'play:6:1'));
  let clairvoyant = $state(false);

  const bestInWorld = Array.from({ length: K }, (_, k) => cands.reduce((m, c, i) => (c.values[k] > cands[m].values[k] ? i : m), 0));
  const knowing = meanOf(Array.from({ length: K }, (_, k) => cands[bestInWorld[k]].values[k]));
  // The axis spans the actions near the best; one far below (ending the turn at once) is drawn at its left edge.
  const near = cands.filter((c) => c.value >= cands[0].value - 0.06).flatMap((c) => c.values);
  const CW = 300;
  const x = scaleLinear().domain([Math.min(...near), Math.max(...near)]).range([14, CW - 8]).nice().clamp(true);
  const below = (v: number) => v < x.domain()[0];
  const diffs = $derived(cands[a].values.map((v, k) => v - cands[b].values[k]));
  const dx = $derived.by(() => {
    const m = Math.max(0.01, ...diffs.map(Math.abs));
    return scaleLinear().domain([-m, m]).range([8, CW - 8]).nice();
  });
  const unpaired = $derived(Math.sqrt(cands[a].ci ** 2 + cands[b].ci ** 2));
  const color = (k: number) => `var(--v-s${k + 1})`;

  const L = {
    setting: w('Round 1 of an elite fight; 8 worlds, 300 moves tried in each', '一场精英战的第 1 回合；8 个世界，各试 300 步', '一場菁英戰的第 1 回合；8 個世界，各試 300 步'),
    compare: w('Compare two first moves world by world', '逐个世界比较两个第一步', '逐個世界比較兩個第一步'),
    diff: w('difference in each world', '每个世界里的差', '每個世界裡的差'),
    paired: w('paired 95% interval of the difference', '差的成对 95% 区间', '差的成對 95% 區間'),
    unpaired: w('if the worlds were not shared', '如果两边不共用世界', '如果兩邊不共用世界'),
    know: w('Show each world’s best first move', '标出每个世界里最好的第一步', '標出每個世界裡最好的第一步'),
    knowing: w('mean over the worlds of each world’s best', '每个世界各取最好，再平均', '每個世界各取最好，再平均'),
    pimc: w('the pick: the move with the best mean', '选中的：平均最高的那一步', '選中的：平均最高的那一步'),
    gap: w('what knowing the world would add', '知道是哪个世界能多得的值', '知道是哪個世界能多得的值'),
    nodes: w('moves tried below it in each world', '每个世界在它底下试了几步', '每個世界在它底下試了幾步'),
  };
</script>

<div class="vfig">
  {#if !names}
    <p class="v-muted">{t('loading')}</p>
  {:else}
    <p class="v-ink2" style="margin-bottom: 0.25rem;">
      {names.full('encounter', obs.encounter ?? '')} · {say(lang, L.setting)} · HP {obs.player.hp}/{obs.player.max_hp} · {t('energy')} {obs.player.energy}
    </p>
    {#each obs.enemies as e (e.uid)}
      <p class="v-ink2" style="margin-bottom: 0.5rem;">{names.full('monster', e.id)} {e.hp}/{e.max_hp} · {t('intent')} {(e.intents ?? []).map((i) => intentText(lang, i)).join(', ')}
        {#each e.powers ?? [] as p (p.id)} · {names.name('power', p.id)} {p.amount}{/each}</p>
    {/each}
    <div class="v-row" style="font-size: 0.75rem; margin-bottom: 0.25rem;">
      {#each Array.from({ length: K }, (_, k) => k) as k (k)}<span><span class="v-key" style="background: {color(k)}"></span> {t('world')} {k + 1}</span>{/each}
    </div>
    <div class="v-scroll">
      <table class="wt">
        <thead><tr><th>{t('firstAction')}</th><th class="v-num">{t('mean')}</th><th class="v-num">±</th><th></th></tr></thead>
        <tbody>
          {#each cands as c, i (c.action)}
            <tr class:pick={c.action === decision.chosen}>
              <td title={label(c.action, true)}>{label(c.action)}{#if c.action === decision.chosen}<span class="tag">{t('played')}</span>{/if}</td>
              <td class="v-num">{fmt(lang, c.value, 4)}</td>
              <td class="v-num v-muted">{fmt(lang, c.ci, 4)}</td>
              <td>
                <svg width={CW} height="18" role="img" aria-label="{label(c.action)} {fmt(lang, c.value, 4)}">
                  <line x1={x(c.value - c.ci)} x2={x(c.value + c.ci)} y1="9" y2="9" stroke="var(--v-axis)" stroke-width="5" stroke-linecap="round" />
                  {#each c.values as v, k (k)}
                    <circle cx={x(v)} cy="9" r={clairvoyant && bestInWorld[k] === i ? 5 : 3.2} fill={color(k)} stroke={clairvoyant && bestInWorld[k] === i ? 'var(--v-ink)' : 'var(--v-surface)'} stroke-width="1.2">
                      <title>{t('world')} {k + 1}: {fmt(lang, v, 4)} ({c.nodes?.[k] ?? '–'} {t('nodes')})</title>
                    </circle>
                  {/each}
                  <line x1={x(c.value)} x2={x(c.value)} y1="2" y2="16" stroke="var(--v-ink)" stroke-width="1.5" />
                  {#if below(c.value)}<text x="0" y="13">‹</text>{/if}
                </svg>
              </td>
            </tr>
          {/each}
        </tbody>
        <tfoot>
          <tr><td></td><td></td><td></td><td>
            <svg width={CW} height="16">{#each x.ticks(4) as v (v)}<text x={x(v)} y="12" text-anchor="middle">{fmt(lang, v, 2)}</text>{/each}</svg>
          </td></tr>
        </tfoot>
      </table>
    </div>

    <label style="margin-top: 0.5rem;"><input type="checkbox" bind:checked={clairvoyant} /> {say(lang, L.know)}</label>
    {#if clairvoyant}
      <div class="v-row" style="margin-top: 0.4rem;">
        <div class="v-stat"><span class="v-label">{say(lang, L.knowing)}</span><span class="v-value v-num">{fmt(lang, knowing, 4)}</span></div>
        <div class="v-stat"><span class="v-label">{say(lang, L.pimc)}</span><span class="v-value v-num">{fmt(lang, cands[0].value, 4)}</span></div>
        <div class="v-stat"><span class="v-label">{say(lang, L.gap)}</span><span class="v-value v-num">{fmt(lang, knowing - cands[0].value, 4)}</span></div>
      </div>
    {/if}

    <p class="sub">{say(lang, L.compare)}</p>
    <div class="v-row">
      <select bind:value={a}>{#each cands as c, i (c.action)}<option value={i}>{label(c.action)}</option>{/each}</select>
      <span>−</span>
      <select bind:value={b}>{#each cands as c, i (c.action)}<option value={i}>{label(c.action)}</option>{/each}</select>
    </div>
    <svg width={CW} height="40" role="img" aria-label={say(lang, L.diff)} style="margin-top: 0.4rem;">
      <line x1={dx(0)} x2={dx(0)} y1="2" y2="24" stroke="var(--v-axis)" />
      <line x1={dx(meanOf(diffs) - unpaired)} x2={dx(meanOf(diffs) + unpaired)} y1="20" y2="20" stroke="var(--v-grid)" stroke-width="5" stroke-linecap="round" />
      <line x1={dx(meanOf(diffs) - ciOf(diffs))} x2={dx(meanOf(diffs) + ciOf(diffs))} y1="12" y2="12" stroke="var(--v-axis)" stroke-width="5" stroke-linecap="round" />
      {#each diffs as d, k (k)}<circle cx={dx(d)} cy="12" r="3.2" fill={color(k)} stroke="var(--v-surface)" stroke-width="1.2"><title>{t('world')} {k + 1}: {fmt(lang, d, 4)}</title></circle>{/each}
      <line x1={dx(meanOf(diffs))} x2={dx(meanOf(diffs))} y1="5" y2="24" stroke="var(--v-ink)" stroke-width="1.5" />
      {#each dx.ticks(4) as v (v)}<text x={dx(v)} y="37" text-anchor="middle">{fmt(lang, v, 2)}</text>{/each}
    </svg>
    <div class="v-row">
      <div class="v-stat"><span class="v-label">{t('mean')}</span><span class="v-value v-num">{fmt(lang, meanOf(diffs), 4)}</span></div>
      <div class="v-stat"><span class="v-label">{say(lang, L.paired)}</span><span class="v-value v-num">± {fmt(lang, ciOf(diffs), 4)}</span></div>
      <div class="v-stat"><span class="v-label">{say(lang, L.unpaired)}</span><span class="v-value v-num">± {fmt(lang, unpaired, 4)}</span></div>
    </div>
  {/if}
</div>

<style>
  .wt { border-collapse: collapse; width: max-content; margin: 0; }
  .wt th, .wt td { border: none; padding: 0.05rem 0.6rem 0.05rem 0; background: none; vertical-align: middle; font-size: 0.8125rem; }
  .wt th { color: var(--v-muted); font-weight: normal; font-size: 0.75rem; text-align: left; }
  .wt td:first-child { max-width: 15rem; }
  .pick td:first-child { font-weight: 600; }
  .tag { font-size: 0.7rem; color: var(--v-muted); border: 1px solid var(--v-grid); border-radius: 0.25rem; padding: 0 0.3rem; margin-left: 0.4rem; }
  .sub { color: var(--v-ink-2); font-size: 0.8125rem; margin: 0.9rem 0 0.3rem 0; font-weight: 600; }
</style>
