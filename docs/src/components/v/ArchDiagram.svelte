<script lang="ts">
  // A net's architecture as stacked blocks (arch.ts places them). Point at a block for its shape and formula.
  import { DIAGRAMS, LINE, layout, type Tone } from './arch.ts';
  import type { Lang } from './names.ts';

  let { lang = 'en', net }: { lang?: Lang; net: keyof typeof DIAGRAMS } = $props();
  const drawn = $derived(layout(DIAGRAMS[net](lang)));
  let hovered = $state<number | null>(null);

  const TONE: Record<Tone, string> = {
    embed: 'var(--v-s8)',
    attn: 'var(--v-s2)',
    ff: 'var(--v-s1)',
    norm: 'var(--v-s4)',
    linear: 'var(--v-s7)',
    pool: 'var(--v-s6)',
    head: 'var(--v-s3)',
    msg: 'var(--v-s5)',
  };
  const HINT = {
    en: 'Point at a block for what it computes.',
    'zh-CN': '指向一个方块，看它计算什么。',
    'zh-TW': '指向一個方塊，看它計算什麼。',
  }[lang];
  const info = $derived.by(() => {
    if (hovered === null) return HINT;
    const s = drawn.shapes[hovered];
    return 'info' in s ? s.info : HINT;
  });
</script>

<div class="vfig">
  <!-- on a narrow screen the drawing keeps a readable size and scrolls sideways -->
  <div class="v-scroll">
  <svg viewBox="-8 -4 {drawn.width + 16} {drawn.height + 8}" style="width: 100%; min-width: {Math.min(drawn.width + 16, 540)}px; max-width: {drawn.width + 16}px;" role="img" aria-label={HINT}>
    <defs>
      <marker id="arch-head-{net}" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
        <path d="M0,0.5 L8,4 L0,7.5 z" fill="var(--v-ink-2)" />
      </marker>
    </defs>
    {#each drawn.shapes as s, i (i)}
      {#if s.t === 'frame'}
        <g role="img" aria-label={s.info} onpointerenter={() => (hovered = i)} onpointerleave={() => (hovered = null)}>
          <rect x={s.x} y={s.y} width={s.w} height={s.h} rx="12" fill="var(--v-mid)" fill-opacity="0.55" stroke="var(--v-ink-2)" stroke-width={hovered === i ? 2.5 : 1.5} />
          <text x={s.x + s.w - 8} y={s.y + 14} text-anchor="end" class="times">{s.times}</text>
        </g>
      {/if}
    {/each}
    {#each drawn.shapes as s, i (i)}
      {#if s.t === 'arrow'}
        <path d={s.d} fill="none" stroke="var(--v-ink-2)" stroke-width="1.5" marker-end={s.head ? `url(#arch-head-${net})` : undefined} />
      {/if}
    {/each}
    {#each drawn.shapes as s, i (i)}
      {#if s.t === 'block'}
        <g
          role="button"
          tabindex="0"
          aria-label={`${s.lines.join(' ')}: ${s.info}`}
          onpointerenter={() => (hovered = i)}
          onpointerleave={() => (hovered = null)}
          onfocus={() => (hovered = i)}
          onblur={() => (hovered = null)}
        >
          <rect x={s.x} y={s.y} width={s.w} height={s.h} rx="5" fill="var(--v-surface)" />
          <rect x={s.x} y={s.y} width={s.w} height={s.h} rx="5" fill={TONE[s.tone]} fill-opacity="0.22" stroke={TONE[s.tone]} stroke-width={hovered === i ? 3 : 1.75} />
          {#each s.lines as line, k (k)}
            <text x={s.x + s.w / 2} y={s.y + s.h / 2 + (k - (s.lines.length - 1) / 2) * LINE + 4} text-anchor="middle" class="label">{line}</text>
          {/each}
        </g>
      {:else if s.t === 'add'}
        <g role="img" aria-label={s.info} onpointerenter={() => (hovered = i)} onpointerleave={() => (hovered = null)}>
          <circle cx={s.cx} cy={s.cy} r="8" fill="var(--v-surface)" stroke="var(--v-ink-2)" stroke-width={hovered === i ? 2.5 : 1.5} />
          <path d="M{s.cx - 5},{s.cy}H{s.cx + 5}M{s.cx},{s.cy - 5}V{s.cy + 5}" stroke="var(--v-ink)" stroke-width="1.5" />
        </g>
      {:else if s.t === 'text'}
        {#each s.lines as line, k (k)}
          <text x={s.x} y={s.y + k * LINE} text-anchor="middle" class={s.kind}>{line}</text>
        {/each}
      {/if}
    {/each}
  </svg>
  </div>
  <p class="v-caption" style="min-height: 2.6em;">{info}</p>
</div>

<style>
  .label {
    font-size: 12px;
    fill: var(--v-ink);
  }
  .input,
  .output {
    font-size: 11.5px;
    fill: var(--v-ink-2);
  }
  .title {
    font-size: 12px;
    font-weight: 600;
    fill: var(--v-ink);
  }
  .times {
    font-size: 12px;
    font-weight: 600;
    fill: var(--v-ink-2);
  }
  g[role='button'] {
    cursor: default;
    outline: none;
  }
</style>
