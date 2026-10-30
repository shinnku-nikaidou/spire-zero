<script lang="ts">
  // What pure V reads of a run state: the act's map with the points still reachable, and every token
  // (docs: macro-brain/network §1).
  import { onMount } from 'svelte';
  import { here, reachable, mapToken, tokens, state as numbers, STATE, type RunView } from '../../lib/v/encode.ts';
  import type { Meta } from '../../lib/v/net.ts';
  import { loadV } from '../../lib/v/load.ts';
  import Picker from './Picker.svelte';
  import { words } from './i18n.ts';
  import { loadNames, type Lang, type Names } from './names.ts';
  import { states, type Picked } from './states.ts';
  import { COLOUR, DX, DY, GLYPH, TYPES } from './map.ts';

  let { lang = 'en', start = 'map' }: { lang?: Lang; start?: string } = $props();
  const t = $derived(words(lang));
  let meta = $state.raw<Meta | null>(null);
  let names = $state.raw<Names | null>(null);
  let choices = $state.raw<Picked[]>([]);
  let picked = $state(start);
  let hovered = $state<string | null>(null);

  onMount(async () => {
    const [loaded, n] = await Promise.all([loadV(), loadNames(lang)]);
    meta = loaded.meta;
    names = n;
    choices = states(lang, loaded.meta);
  });

  const view = $derived<RunView | null>(choices.find((c) => c.id === picked)?.view ?? null);
  const tk = $derived(view ? tokens(view) : null);
  const known = (table: string, id: string) => !!meta && meta.vocab[table].includes(id);

  const map = $derived.by(() => {
    if (!view) return null;
    const m = view.map;
    const rows = Math.max(...m.points.map((p) => p.row));
    const cols = Math.max(...m.points.map((p) => p.col));
    const at = here(m);
    const reach = new Set(reachable(m).map((p) => `${p.col},${p.row}`));
    const visited = new Set(m.visited.map((p) => `${p.col},${p.row}`));
    return { m, rows, cols, at, reach, visited };
  });
  const px = (col: number) => 16 + col * DX;
  const py = (row: number) => 16 + (map!.rows - row) * DY;

  const deckCounts = $derived.by(() => {
    if (!tk) return [];
    const counts = new Map<string, { id: string; up: number; mods: string[]; n: number }>();
    for (const [id, up, mods] of tk.cards) {
      const k = `${id}|${up}|${mods.join(',')}`;
      const c = counts.get(k);
      if (c) c.n++;
      else counts.set(k, { id, up, mods, n: 1 });
    }
    return [...counts.values()];
  });
</script>

<div class="vfig">
  {#if !meta || !names || !view || !tk || !map}
    <p class="v-muted">{t('loading')}</p>
  {:else}
    <div class="v-row" style="margin-bottom: 0.75rem;">
      <Picker states={choices} value={picked} label={t('state')} onpick={(id) => (picked = id)} />
    </div>
    <div class="layout">
      <div>
        <svg width={px(map.cols) + 16} height={py(0) + 16} role="img" aria-label={t('map')}>
          {#each map.m.points as p (`${p.col},${p.row}`)}
            {#each p.next as n (`${n.col},${n.row}`)}
              {@const both = map.visited.has(`${p.col},${p.row}`) && map.visited.has(`${n.col},${n.row}`)}
              <line x1={px(p.col)} y1={py(p.row)} x2={px(n.col)} y2={py(n.row)} stroke={both ? 'var(--v-ink-2)' : 'var(--v-grid)'} stroke-width={both ? 2.5 : 1} />
            {/each}
          {/each}
          {#each map.m.points as p (`${p.col},${p.row}`)}
            {@const k = `${p.col},${p.row}`}
            {@const reach = map.reach.has(k)}
            {@const token = reach ? mapToken(p, map.at.row) : null}
            <g
              transform="translate({px(p.col)},{py(p.row)})"
              role="img"
              aria-label={token ?? p.type}
              onpointerenter={() => (hovered = token ? `${token} · ${names!.token('map', token)}` : `${names!.name('map', p.type)} · ${map!.visited.has(k) ? t('visited') : t('unreachable')}`)}
              onpointerleave={() => (hovered = null)}
            >
              <circle r="9" fill="var(--v-surface)" stroke={reach ? COLOUR[p.type] : 'var(--v-axis)'} stroke-width={reach ? 2.5 : 1} />
              {#if map.at.col === p.col && map.at.row === p.row}<circle r="12.5" fill="none" stroke="var(--v-ink)" stroke-width="1.5" />{/if}
              <text text-anchor="middle" y="3.5" style="font-size: 10px; font-weight: 600;" fill={reach ? 'var(--v-ink)' : 'var(--v-muted)'}>{GLYPH[p.type] ?? '·'}</text>
            </g>
          {/each}
        </svg>
        <p class="v-caption" style="min-height: 2.6em;">{hovered ?? `◯ ${t('here')}`}</p>
        <div class="legend">
          {#each TYPES as ty (ty)}
            <span><span class="dot" style="border-color: {COLOUR[ty]}">{GLYPH[ty]}</span>{names.name('map', ty)}</span>
          {/each}
        </div>
      </div>
      <div class="tokens">
        <h4>{t('tokens')}</h4>
        <p><span class="v-muted">{t('context')}</span><br />{tk.context.map((x) => names!.token('context', x)).join(' · ')}</p>
        <p><span class="v-muted">{t('numbers')}</span><br /><span class="v-num">{STATE.map((s, i) => `${s} ${numbers(view!)[i]}`).join(' · ')}</span></p>
        <p>
          <span class="v-muted">{t('deck')} ({tk.cards.length})</span><br />
          {#each deckCounts as c, i (i)}
            <span class:v-muted={!known('cards', c.id)}>{names.card(c.id, c.up)}{c.mods.length ? ` [${c.mods.join(', ')}]` : ''}{c.n > 1 ? ` ×${c.n}` : ''}</span>{i < deckCounts.length - 1 ? ' · ' : ''}
          {/each}
        </p>
        <p><span class="v-muted">{t('relics')} ({tk.relics.length})</span><br />{tk.relics.map((x) => names!.token('relics', x)).join(' · ') || t('none')}</p>
        <p><span class="v-muted">{t('potions')} ({tk.potions.length})</span><br />{tk.potions.map((x) => names!.token('potions', x)).join(' · ') || t('none')}</p>
        <p><span class="v-muted">{t('map')} ({tk.map.length})</span><br /><code class="maptok">{tk.map.join(' ')}</code></p>
      </div>
    </div>
  {/if}
</div>

<style>
  .layout {
    display: flex;
    flex-wrap: wrap;
    gap: 1.25rem;
    align-items: flex-start;
  }
  .tokens {
    flex: 1 1 18rem;
    min-width: 16rem;
    font-size: 0.8125rem;
  }
  .tokens h4 {
    font-size: 0.875rem;
    margin-bottom: 0.4rem;
  }
  .tokens p {
    margin: 0 0 0.5rem;
  }
  .maptok {
    font-size: 0.7rem;
    word-break: break-word;
  }
  .legend {
    display: flex;
    flex-wrap: wrap;
    gap: 0.2rem 0.7rem;
    font-size: 0.75rem;
    max-width: 16rem;
    color: var(--v-ink-2);
  }
  .dot {
    display: inline-block;
    width: 1rem;
    height: 1rem;
    border: 2px solid;
    border-radius: 50%;
    font-size: 0.6rem;
    font-weight: 600;
    text-align: center;
    line-height: 0.8rem;
    margin-right: 0.25rem;
    color: var(--v-ink);
  }
</style>
