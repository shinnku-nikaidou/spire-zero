<script lang="ts">
  // From V's heads to its value, for a run state: per floor the hazard, the chance to be alive after it, the chance
  // the run ends there and the share taken there; the expected floor and progress (docs: macro-brain/value §1).
  import { onMount } from 'svelte';
  import { scaleLinear, scaleBand } from 'd3';
  import type { RunView } from '../../lib/v/encode.ts';
  import type { V } from '../../lib/v/net.ts';
  import { loadV } from '../../lib/v/load.ts';
  import Picker from './Picker.svelte';
  import { fmt, words } from './i18n.ts';
  import type { Lang } from './names.ts';
  import { states, type Picked } from './states.ts';

  let { lang = 'en', start = 'test-1' }: { lang?: Lang; start?: string } = $props();
  const t = $derived(words(lang));
  let v = $state.raw<V | null>(null);
  let choices = $state.raw<Picked[]>([]);
  let picked = $state(start);
  let hp = $state<number | null>(null);
  let hover = $state<number | null>(null);

  onMount(async () => {
    const loaded = await loadV();
    v = loaded.v;
    choices = states(lang, loaded.meta);
  });

  const base = $derived<RunView | null>(choices.find((c) => c.id === picked)?.view ?? null);
  const view = $derived(base ? { ...base, hp: Math.min(hp ?? base.hp, base.max_hp) } : null);
  const out = $derived.by(() => {
    if (!v || !view) return null;
    const trace = v.trace(view);
    return { trace, value: v.value(trace, view.floor) };
  });

  const LAST = 48; // floors shown: the last boss is fought on floor 48, where every gauntlet run ends
  const BOSSES = [17, 33, 48];
  const W = 640;
  const M = { l: 44, r: 12 };
  const floors = $derived(out ? Array.from({ length: LAST - out.value.from + 1 }, (_, i) => out!.value.from + i) : []);
  const x = $derived(scaleBand<number>().domain(floors).range([M.l, W - M.r]).paddingInner(0.2));
  const H = 110;
  const y = scaleLinear().domain([0, 1]).range([H - 18, 8]);
  const yEnd = $derived(scaleLinear().domain([0, Math.max(0.05, ...floors.map((g) => out!.value.end[g]))]).nice().range([H - 18, 8]));
  const surviveLine = $derived(
    floors.map((g, i) => `${i ? 'L' : 'M'}${(x(g)! + x.bandwidth() / 2).toFixed(1)},${y(out!.value.survive[g]).toFixed(1)}`).join(''),
  );
  function pick(id: string) {
    picked = id;
    hp = null;
  }
</script>

<div class="vfig" style="position: relative;">
  {#if !out || !view || !base}
    <p class="v-muted">{t('loading')}</p>
  {:else}
    <div class="v-row" style="margin-bottom: 0.5rem;">
      <Picker states={choices} value={picked} label={t('state')} onpick={pick} />
      <label><span class="v-ink2">HP {view.hp}/{view.max_hp}</span>
        <input type="range" min="1" max={base.max_hp} value={view.hp} oninput={(e) => (hp = +(e.currentTarget as HTMLInputElement).value)} /></label>
    </div>
    <div class="v-row" style="margin-bottom: 0.5rem; gap: 1.5rem;">
      <div class="v-stat"><span class="v-label">{t('expectedFloor')}</span><span class="v-value v-num">{fmt(lang, out.value.expectedFloor, 2)}</span></div>
      <div class="v-stat"><span class="v-label">{t('progress')}</span><span class="v-value v-num">{fmt(lang, out.value.progress ?? out.value.expectedFloor, 2)}</span></div>
    </div>
    {#snippet chart(title: string, draw: 'hazard' | 'survive' | 'end')}
      <p class="v-ink2" style="margin: 0.5rem 0 0;">{title}</p>
      <svg viewBox="0 0 {W} {H}" width="100%" role="img" aria-label={title} onpointerleave={() => (hover = null)}>
        {#each draw === 'end' ? yEnd.ticks(3) : [0, 0.5, 1] as tick (tick)}
          <line x1={M.l} x2={W - M.r} y1={(draw === 'end' ? yEnd : y)(tick)} y2={(draw === 'end' ? yEnd : y)(tick)} stroke="var(--v-grid)" />
          <text x={M.l - 6} y={(draw === 'end' ? yEnd : y)(tick) + 4} text-anchor="end">{fmt(lang, tick, draw === 'end' ? 2 : 1)}</text>
        {/each}
        {#each BOSSES.filter((b) => b >= out!.value.from) as b (b)}
          <text x={x(b)! + x.bandwidth() / 2} y={H - 4} text-anchor="middle">{t('bossFloor')} {b}</text>
        {/each}
        {#if draw === 'survive'}
          <path d={surviveLine} fill="none" stroke="var(--v-s1)" stroke-width="2" />
        {/if}
        {#each floors as g (g)}
          {@const xb = x(g)!}
          {#if draw === 'hazard'}
            <rect x={xb} width={x.bandwidth()} y={y(out.trace.hazards[g])} height={Math.max(0, y(0) - y(out.trace.hazards[g]))} rx="1.5" fill="var(--v-s1)" />
          {:else if draw === 'end'}
            <rect x={xb} width={x.bandwidth()} y={yEnd(out.value.end[g])} height={Math.max(0, yEnd(0) - yEnd(out.value.end[g]))} rx="1.5" fill="var(--v-s2)" />
          {/if}
          <rect x={xb - 1} width={x.bandwidth() + 2} y="0" height={H - 16} fill="transparent" onpointerenter={() => (hover = g)} role="presentation" />
        {/each}
        {#if hover !== null && x(hover) !== undefined}
          <line x1={x(hover)! + x.bandwidth() / 2} x2={x(hover)! + x.bandwidth() / 2} y1="6" y2={H - 18} stroke="var(--v-ink-2)" />
        {/if}
      </svg>
    {/snippet}
    {@render chart(t('hazard'), 'hazard')}
    {@render chart(t('survive'), 'survive')}
    {@render chart(t('endChance'), 'end')}
    {#if hover !== null}
      <div class="v-tip v-num" style="right: 1rem; top: 1rem;">
        <strong>{t('floor')} {hover}</strong><br />
        h = {fmt(lang, out.trace.hazards[hover], 3)} · S = {fmt(lang, out.value.survive[hover], 3)}<br />
        P(end) = {fmt(lang, out.value.end[hover], 3)}{#if out.trace.shares} · s = {fmt(lang, out.trace.shares[hover], 2)}{/if}
      </div>
    {/if}
  {/if}
</div>
