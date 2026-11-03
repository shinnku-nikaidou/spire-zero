<script lang="ts">
  // Pure V's forward pass on a run state the reader can edit: every layer as a row of coloured cells, each card's
  // contribution, and the value (docs: macro-brain/network).
  import { onMount } from 'svelte';
  import type { Card, RunView } from '../../lib/v/encode.ts';
  import { BAGS, STATE } from '../../lib/v/encode.ts';
  import type { Meta, V } from '../../lib/v/net.ts';
  import { loadV } from '../../lib/v/load.ts';
  import Heat from './Heat.svelte';
  import Picker from './Picker.svelte';
  import { fmt, words } from './i18n.ts';
  import { loadNames, type Lang, type Names } from './names.ts';
  import { states, type Picked } from './states.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));

  let v = $state.raw<V | null>(null);
  let meta = $state.raw<Meta | null>(null);
  let names = $state.raw<Names | null>(null);
  let choices = $state.raw<Picked[]>([]);
  let picked = $state('card_reward');
  let view = $state.raw<RunView | null>(null);
  let tip = $state<{ text: string; x: number; y: number } | null>(null);
  let box: HTMLDivElement;

  onMount(async () => {
    const [loaded, n] = await Promise.all([loadV(), loadNames(lang)]);
    v = loaded.v;
    meta = loaded.meta;
    names = n;
    choices = states(lang, loaded.meta);
    pick(picked);
  });

  function pick(id: string) {
    picked = id;
    const s = choices.find((c) => c.id === id);
    if (s) view = structuredClone(s.view);
  }

  const original = $derived(choices.find((c) => c.id === picked)?.view ?? null);
  const trace = $derived(v && view ? v.trace(view) : null);
  const value = $derived(v && view && trace ? v.value(trace, view.floor) : null);
  const base = $derived(v && original ? v.of(original) : null);

  const key = (c: Card) => `${c.id}|${c.up ?? 0}|${c.enchant?.id ?? ''}|${c.affliction?.id ?? ''}`;

  // The deck's distinct cards, each with its copies, its trace and what it adds to V.
  const groups = $derived.by(() => {
    if (!v || !view || !trace) return [];
    const byKey = new Map<string, { card: Card; count: number; at: number }>();
    view.deck.forEach((c, i) => {
      const g = byKey.get(key(c));
      if (g) g.count++;
      else byKey.set(key(c), { card: c, count: 1, at: i });
    });
    const now = value!.value;
    return [...byKey.values()]
      .map((g) => {
        const deck = [...view!.deck];
        deck.splice(g.at, 1);
        const without = v!.of({ ...view!, deck });
        return { ...g, trace: trace.cards[g.at], gain: now - without };
      })
      .sort((a, b) => b.gain - a.gain);
  });

  function edit(change: (deck: Card[]) => Card[]) {
    if (view) view = { ...view, deck: change([...view.deck]) };
  }
  const removeOne = (c: Card) => edit((d) => (d.splice(d.findIndex((x) => key(x) === key(c)), 1), d));
  const addOne = (c: Card) => edit((d) => [...d, { ...c }]);
  const upgradeOne = (c: Card) =>
    edit((d) => {
      const i = d.findIndex((x) => key(x) === key(c));
      d[i] = { ...d[i], up: (d[i].up ?? 0) + 1 };
      return d;
    });

  const cardChoices = $derived(names && meta ? meta.vocab.cards.slice(1).map((id) => ({ id, label: names!.full('card', id) })) : []);
  function addByLabel(e: Event) {
    const input = e.currentTarget as HTMLInputElement;
    const hit = cardChoices.find((c) => c.label === input.value || c.id === input.value);
    if (!hit) return;
    addOne({ id: hit.id });
    input.value = '';
  }

  function show(text: string, e: PointerEvent) {
    const r = box.getBoundingClientRect();
    tip = { text, x: e.clientX - r.left + 12, y: e.clientY - r.top + 12 };
  }
  const hover = (what: string) => (row: number | null, col: number | null, val: number | null, e: PointerEvent) => {
    if (row === null || col === null || val === null) tip = null;
    else show(`${what} [${col}] = ${val.toFixed(3)}`, e);
  };

  // The body's input, in the order net.py concatenates it.
  const zParts = $derived([
    { name: t('mean'), size: 256 },
    { name: t('sum'), size: 256 },
    ...BAGS.map((b) => ({ name: b, size: 64 })),
    { name: t('numbers'), size: 6 },
  ]);
  const unknown = (table: string, ids: string[]) => (v ? ids.filter((id) => !v!.index[table].has(id)).length : 0);
  const maxGain = $derived(Math.max(0.05, ...groups.map((g) => Math.abs(g.gain))));
  // One colour scale per column, so the cards of a column compare.
  const absMax = (rows: ArrayLike<number>[]) => Math.max(1e-6, ...rows.flatMap((r) => Array.from(r, Math.abs)));
  const maxX = $derived(absMax(groups.map((g) => g.trace.x)));
  const maxH = $derived(absMax([...groups.map((g) => g.trace.h), ...(trace ? [trace.mean, trace.sum] : [])]));
  const maxBag = $derived(trace ? absMax(BAGS.map((b) => trace!.bags[b].vector)) : 1);
</script>

<div class="vfig" bind:this={box} style="position: relative;">
  {#if !v || !view || !trace || !value || !names}
    <p class="v-muted">{t('loading')}</p>
  {:else}
    <div class="v-row" style="margin-bottom: 0.75rem;">
      <Picker states={choices} value={picked} label={t('state')} onpick={pick} />
      <button onclick={() => pick(picked)}>{t('reset')}</button>
    </div>

    <div class="v-row" style="margin-bottom: 0.75rem; gap: 1.5rem;">
      <div class="v-stat">
        <span class="v-label">{t('value')}</span>
        <span class="v-value v-num">{fmt(lang, value.value, 2)}</span>
        {#if base !== null && Math.abs(value.value - base) > 1e-6}
          <span class="v-ink2 v-num">{value.value >= base ? '+' : '−'}{fmt(lang, Math.abs(value.value - base), 3)}</span>
        {/if}
      </div>
      <div class="v-stat"><span class="v-label">{t('expectedFloor')}</span><span class="v-value v-num">{fmt(lang, value.expectedFloor, 2)}</span></div>
    </div>

    <div class="v-row" style="margin-bottom: 1rem;">
      <label>
        <span class="v-ink2">HP {view.hp}/{view.max_hp}</span>
        <input type="range" min="1" max={view.max_hp} value={view.hp} oninput={(e) => (view = { ...view!, hp: +(e.currentTarget as HTMLInputElement).value })} />
      </label>
      <label>
        <span class="v-ink2">{t('gold')} {view.gold}</span>
        <input type="range" min="0" max="600" step="5" value={view.gold} oninput={(e) => (view = { ...view!, gold: +(e.currentTarget as HTMLInputElement).value })} />
      </label>
      <label>
        <input type="search" list="v-cards-{lang}" placeholder={t('addCard')} onchange={addByLabel} style="width: 14rem;" />
      </label>
      <datalist id="v-cards-{lang}">
        {#each cardChoices as c (c.id)}<option value={c.label}></option>{/each}
      </datalist>
    </div>

    <div class="v-scroll">
      <!-- 1. Each card through phi -->
      <div class="grid">
        <div class="head">{t('deck')} ({view.deck.length})</div>
        <div class="head">x (64)</div>
        <div class="head">φ(x) (256)</div>
        <div class="head">{t('gain')}</div>
        {#each groups as g (key(g.card))}
          <div class="name" title={names.card(g.card.id, g.card.up ?? 0)}>
            <button class="mini" aria-label={t('remove')} onclick={() => removeOne(g.card)}>−</button>
            <button class="mini" aria-label="+1" onclick={() => addOne(g.card)}>+</button>
            <button class="mini" aria-label={t('upgrade')} disabled={(g.card.up ?? 0) >= 3} onclick={() => upgradeOne(g.card)}>⇧</button>
            <span class:v-muted={g.trace.index === 0}>{names.card(g.card.id, g.card.up ?? 0)}{g.count > 1 ? ` ×${g.count}` : ''}</span>
          </div>
          <Heat rows={[g.trace.x]} cell={3} height={11} diverging max={maxX} onhover={hover('x')} />
          <Heat rows={[g.trace.h]} cell={1.5} height={11} max={maxH} onhover={hover('φ(x)')} />
          <svg width="90" height="11" role="img" aria-label="{fmt(lang, g.gain, 3)}">
            <line x1="45" x2="45" y1="0" y2="11" stroke="var(--v-axis)" />
            <rect
              x={g.gain >= 0 ? 45 : 45 - (44 * -g.gain) / maxGain}
              width={(44 * Math.abs(g.gain)) / maxGain}
              y="2"
              height="7"
              rx="2"
              fill={g.gain >= 0 ? 'var(--v-pos)' : 'var(--v-neg)'}
            >
              <title>{g.gain >= 0 ? '+' : '−'}{fmt(lang, Math.abs(g.gain), 3)}</title>
            </rect>
          </svg>
        {/each}
        <div class="name v-ink2">{t('mean')}</div>
        <div></div>
        <Heat rows={[trace.mean]} cell={1.5} height={11} max={maxH} onhover={hover(t('mean'))} />
        <div></div>
        <div class="name v-ink2">{t('sum')}</div>
        <div></div>
        <Heat rows={[trace.sum]} cell={1.5} height={11} max={maxH} onhover={hover(t('sum'))} />
        <div></div>
      </div>

      <!-- 2. The bags and the numbers -->
      <div class="grid2">
        {#each BAGS as b (b)}
          <div class="name v-ink2">
            {t(b)} ({trace.tokens[b].length}{unknown(b, trace.tokens[b]) ? `, ${unknown(b, trace.tokens[b])} ${t('unknown')}` : ''})
          </div>
          <Heat rows={[trace.bags[b].vector]} cell={3} height={11} diverging max={maxBag} onhover={hover(b)} />
        {/each}
        <div class="name v-ink2">{t('numbers')}</div>
        <div class="numbers v-num">
          {#each STATE as s, i (s)}
            <span class="v-muted">{s}</span><span>{trace.raw[i]}</span><span>→ {fmt(lang, trace.scaled[i], 2)}</span>
          {/each}
        </div>
      </div>

      <!-- 3. The body and the heads -->
      <div class="stack">
        <div class="v-ink2">z = [{zParts.map((p) => `${p.name} ${p.size}`).join(' | ')}] (774)</div>
        <Heat rows={[trace.z]} cell={1.5} height={12} diverging onhover={hover('z')} />
        <div class="v-ink2">{t('body')}: ReLU(W₁z + b₁) (512)</div>
        <Heat rows={[trace.body1]} cell={2} height={12} onhover={hover('body₁')} />
        <div class="v-ink2">{t('body')}: ReLU(W₂h₁ + b₂) (512)</div>
        <Heat rows={[trace.body2]} cell={2} height={12} onhover={hover('body₂')} />
        <div class="v-ink2">{t('heads')}: σ(hazard) (64) · σ(share) (64)</div>
        <Heat rows={[trace.hazards]} cell={8} height={12} max={1} onhover={hover('hazard')} />
        {#if trace.shares}<Heat rows={[trace.shares]} cell={8} height={12} max={1} onhover={hover('share')} />{/if}
      </div>
    </div>
  {/if}
  {#if tip}<div class="v-tip v-num" style:left="{tip.x}px" style:top="{tip.y}px">{tip.text}</div>{/if}
</div>

<style>
  .grid {
    display: grid;
    grid-template-columns: 11rem auto auto auto;
    gap: 2px 0.75rem;
    align-items: center;
    width: max-content;
  }
  .grid2 {
    display: grid;
    grid-template-columns: 11rem auto;
    gap: 2px 0.75rem;
    align-items: center;
    width: max-content;
    margin-top: 1rem;
  }
  .stack {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    margin-top: 1rem;
    width: max-content;
  }
  .head {
    color: var(--v-muted);
    font-size: 0.75rem;
    padding-bottom: 0.25rem;
  }
  .name {
    font-size: 0.75rem;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 11rem;
  }
  .numbers {
    display: grid;
    grid-template-columns: repeat(3, auto);
    gap: 0 0.6rem;
    font-size: 0.75rem;
    width: max-content;
  }
  .mini {
    padding: 0 0.3rem !important;
    font-size: 0.7rem !important;
    line-height: 1.1;
  }
</style>
