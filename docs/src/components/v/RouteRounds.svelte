<script lang="ts">
  // The route encoder's graph for a run state (encode.route) and how far its rounds carry: after k rounds a node's
  // vector depends on the nodes up to k steps ahead, so the boss reaches the player's node after as many rounds as
  // floors lie between them (docs: macro-brain/network §4). Structure only: no weights, the net is not trained yet.
  import { onMount } from 'svelte';
  import { here, reachable, route, MAX_AHEAD, type RunView } from '../../lib/v/encode.ts';
  import { loadV } from '../../lib/v/load.ts';
  import Picker from './Picker.svelte';
  import { words } from './i18n.ts';
  import { COLOUR, DX, DY, GLYPH } from './map.ts';
  import { loadNames, type Lang, type Names } from './names.ts';
  import { states, type Picked } from './states.ts';

  let { lang = 'en', start = 'card_reward' }: { lang?: Lang; start?: string } = $props();
  const t = $derived(words(lang));
  const L = {
    en: {
      rounds: 'Rounds',
      node: 'node',
      ahead: 'floors ahead',
      quest: 'quest',
      leads: 'leads to',
      nothing: 'nothing (the boss)',
      heard: 'holds the boss',
      notYet: 'not yet',
      cols: ['#', 'type', 'ahead', 'quest', 'next'],
      tensors: 'One row per node: route, route_ahead, route_quest, route_next (route_mask and route_next_mask mark the padding).',
      after: (k: number, d: number) =>
        `After ${k} round${k === 1 ? '' : 's'}, a node's vector depends on the nodes up to ${k} step${k === 1 ? '' : 's'} ahead. The player's node 0 is ${d} step${d === 1 ? '' : 's'} from the boss, so it holds the boss after ${d}; the net always runs ${MAX_AHEAD}.`,
    },
    'zh-CN': {
      rounds: '轮数',
      node: '节点',
      ahead: '层之后',
      quest: '任务',
      leads: '通向',
      nothing: '无（Boss）',
      heard: '已含 Boss 的信息',
      notYet: '尚未',
      cols: ['#', '类型', '前方', '任务', '通向'],
      tensors: '每个节点一行：route、route_ahead、route_quest、route_next（route_mask 和 route_next_mask 标出补齐的位置）。',
      after: (k: number, d: number) =>
        `经过 ${k} 轮，一个节点的向量取决于前方 ${k} 步以内的节点。玩家所在的节点 0 离 Boss ${d} 步，所以 ${d} 轮之后它就含有 Boss 的信息；网络总是跑 ${MAX_AHEAD} 轮。`,
    },
    'zh-TW': {
      rounds: '輪數',
      node: '節點',
      ahead: '層之後',
      quest: '任務',
      leads: '通往',
      nothing: '無（頭目）',
      heard: '已含頭目的資訊',
      notYet: '尚未',
      cols: ['#', '類型', '前方', '任務', '通往'],
      tensors: '每個節點一列：route、route_ahead、route_quest、route_next（route_mask 和 route_next_mask 標出補齊的位置）。',
      after: (k: number, d: number) =>
        `經過 ${k} 輪，一個節點的向量取決於前方 ${k} 步以內的節點。玩家所在的節點 0 離頭目 ${d} 步，所以 ${d} 輪之後它就含有頭目的資訊；網路固定跑 ${MAX_AHEAD} 輪。`,
    },
  }[lang];

  let names = $state.raw<Names | null>(null);
  let choices = $state.raw<Picked[]>([]);
  let picked = $state(start);
  let rounds = $state<number | null>(null);
  let hovered = $state<number | null>(null);

  onMount(async () => {
    const [loaded, n] = await Promise.all([loadV(), loadNames(lang)]);
    names = n;
    choices = states(lang, loaded.meta);
  });

  const view = $derived<RunView | null>(choices.find((c) => c.id === picked)?.view ?? null);

  const graph = $derived.by(() => {
    if (!view) return null;
    const m = view.map;
    const nodes = route(m);
    const points = [here(m), ...reachable(m)]; // node i's point, as route orders them
    // steps to the boss, from the last node back (every link goes one row up, so a node's next come after it)
    const steps = new Array<number>(nodes.length).fill(0);
    for (let i = nodes.length - 1; i >= 0; i--) {
      const next = nodes[i][3];
      steps[i] = next.length ? 1 + Math.min(...next.map((j) => steps[j])) : 0;
    }
    const rows = Math.max(...m.points.map((p) => p.row));
    const cols = Math.max(...m.points.map((p) => p.col));
    return { m, nodes, points, steps, rows, cols };
  });

  const k = $derived(rounds ?? graph?.steps[0] ?? 0);

  // the nodes a hovered node's vector depends on after k rounds: itself and those up to k steps ahead
  const cone = $derived.by(() => {
    const s = new Set<number>();
    if (hovered === null || !graph) return s;
    let layer = [hovered];
    s.add(hovered);
    for (let r = 0; r < k && layer.length; r++) {
      const nextLayer: number[] = [];
      for (const i of layer) for (const j of graph.nodes[i][3]) if (!s.has(j)) (s.add(j), nextLayer.push(j));
      layer = nextLayer;
    }
    return s;
  });

  const px = (col: number) => 16 + col * DX;
  const py = (row: number) => 16 + (graph!.rows - row) * DY;

  function describe(i: number) {
    const [type, ahead, quest, next] = graph!.nodes[i];
    return `${L.node} ${i} · ${names!.name('map', type)} (${type}) · ${ahead} ${L.ahead}${quest ? ` · ${L.quest}` : ''} · ${L.leads} ${next.length ? next.join(', ') : L.nothing}`;
  }
</script>

<div class="vfig">
  {#if !names || !graph}
    <p class="v-muted">{t('loading')}</p>
  {:else}
    <div class="v-row" style="margin-bottom: 0.75rem;">
      <Picker states={choices} value={picked} label={t('state')} onpick={(id) => ((picked = id), (rounds = null), (hovered = null))} />
      <label class="v-row" style="gap: 0.5rem;">
        <span class="v-ink2">{L.rounds}</span>
        <input type="range" min="0" max={MAX_AHEAD} step="1" value={k} oninput={(e) => (rounds = +(e.currentTarget as HTMLInputElement).value)} />
        <span class="v-num">{k}</span>
      </label>
    </div>
    <div class="layout">
      <div>
        <svg width={px(graph.cols) + 24} height={py(0) + 16} role="img" aria-label={t('map')}>
          {#each graph.m.points as p (`${p.col},${p.row}`)}
            {#each p.next as n (`${n.col},${n.row}`)}
              <line x1={px(p.col)} y1={py(p.row)} x2={px(n.col)} y2={py(n.row)} stroke="var(--v-grid)" stroke-width="1" />
            {/each}
          {/each}
          {#each graph.nodes as node, i (i)}
            {#each node[3] as j (j)}
              {@const lit = cone.has(i) && cone.has(j)}
              <line
                x1={px(graph.points[i].col)} y1={py(graph.points[i].row)} x2={px(graph.points[j].col)} y2={py(graph.points[j].row)}
                stroke={lit ? 'var(--v-ink)' : 'var(--v-axis)'} stroke-width={lit ? 2.5 : 1.5}
              />
            {/each}
          {/each}
          {#each graph.m.points as p (`${p.col},${p.row}`)}
            {#if !graph.points.includes(p)}
              <circle cx={px(p.col)} cy={py(p.row)} r="3" fill="var(--v-axis)" />
            {/if}
          {/each}
          {#each graph.nodes as node, i (i)}
            {@const p = graph.points[i]}
            {@const heard = graph.steps[i] <= k}
            <g
              transform="translate({px(p.col)},{py(p.row)})"
              role="img"
              aria-label={describe(i)}
              onpointerenter={() => (hovered = i)}
              onpointerleave={() => (hovered = null)}
            >
              <circle r="12" fill="transparent" />
              <circle
                r="9"
                fill={heard ? COLOUR[node[0]] ?? 'var(--v-muted)' : 'var(--v-surface)'}
                fill-opacity={heard ? 0.3 : 1}
                stroke={COLOUR[node[0]] ?? 'var(--v-muted)'}
                stroke-width={hovered === i ? 3 : 2}
                stroke-dasharray={heard ? undefined : '3 2'}
              />
              {#if i === 0}<circle r="12.5" fill="none" stroke="var(--v-ink)" stroke-width="1.5" />{/if}
              <text text-anchor="middle" y="3.5" style="font-size: 10px; font-weight: 600;" fill="var(--v-ink)">{GLYPH[node[0]] ?? '·'}</text>
              <text x="11" y="-7" style="font-size: 8px;" fill="var(--v-muted)">{i}</text>
            </g>
          {/each}
        </svg>
        <div class="v-row legend">
          <span><span class="dot heard"></span>{L.heard}</span>
          <span><span class="dot"></span>{L.notYet}</span>
          <span>◯ {t('here')}</span>
        </div>
        <p class="v-caption" style="min-height: 2.6em; max-width: 26rem;">{hovered === null ? L.after(k, graph.steps[0]) : describe(hovered)}</p>
      </div>
      <div class="table">
        <div class="v-scroll">
          <table>
            <thead><tr>{#each L.cols as c (c)}<th>{c}</th>{/each}</tr></thead>
            <tbody>
              {#each graph.nodes as [type, ahead, quest, next], i (i)}
                <tr class:hot={hovered === i} onpointerenter={() => (hovered = i)} onpointerleave={() => (hovered = null)}>
                  <td class="v-num">{i}</td>
                  <td>{names.name('map', type)}</td>
                  <td class="v-num">{ahead}</td>
                  <td>{quest ? '✓' : ''}</td>
                  <td class="v-num">{next.join(', ') || '–'}</td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
        <p class="v-caption">{L.tensors}</p>
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
  .table {
    flex: 1 1 16rem;
    min-width: 14rem;
    font-size: 0.8125rem;
  }
  .table .v-scroll {
    max-height: 22rem;
    overflow-y: auto;
  }
  table {
    width: 100%;
    margin: 0;
    font-size: 0.75rem;
  }
  th,
  td {
    padding: 0.1rem 0.5rem;
  }
  tr.hot td {
    background: var(--v-wash);
  }
  .legend {
    gap: 0.25rem 0.9rem;
    font-size: 0.75rem;
    color: var(--v-ink-2);
    margin-top: 0.25rem;
  }
  .dot {
    display: inline-block;
    width: 0.8rem;
    height: 0.8rem;
    border: 2px dashed var(--v-ink-2);
    border-radius: 50%;
    margin-right: 0.3rem;
    vertical-align: -0.1rem;
  }
  .dot.heard {
    border-style: solid;
    background: var(--v-wash);
  }
</style>
