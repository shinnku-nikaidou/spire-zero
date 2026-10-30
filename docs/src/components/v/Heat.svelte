<script lang="ts">
  // A vector or a matrix of numbers as coloured cells on a canvas (one row per vector).
  import { onMount } from 'svelte';
  import { colourScale, onTheme } from './theme.ts';

  interface Props {
    rows: ArrayLike<number>[];
    cell?: number; // width of a cell, px
    height?: number; // height of a row, px
    gap?: number; // between rows, px
    diverging?: boolean;
    max?: number; // the scale's end; default: the largest |value|
    highlight?: number | null; // a row drawn with a ring
    onhover?: (row: number | null, col: number | null, value: number | null, event: PointerEvent) => void;
    label?: string;
  }
  let { rows, cell = 3, height = 10, gap = 1, diverging = false, max, highlight = null, onhover, label = '' }: Props = $props();

  let canvas: HTMLCanvasElement;
  let tick = $state(0);
  const cols = $derived(Math.max(1, ...rows.map((r) => r.length)));
  const scale = $derived(max ?? Math.max(1e-9, ...rows.flatMap((r) => Array.from(r, Math.abs))));
  const width = $derived(cols * cell);
  const total = $derived(rows.length * (height + gap) - gap);

  function draw() {
    if (!canvas) return;
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.ceil(width * dpr);
    canvas.height = Math.ceil(Math.max(1, total) * dpr);
    const ctx = canvas.getContext('2d')!;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, total);
    const colour = colourScale(canvas, diverging, scale);
    rows.forEach((r, i) => {
      const y = i * (height + gap);
      for (let j = 0; j < r.length; j++) {
        ctx.fillStyle = colour(r[j]);
        ctx.fillRect(j * cell, y, Math.max(1, cell - (cell > 3 ? 0.5 : 0)), height);
      }
      if (highlight === i) {
        ctx.strokeStyle = getComputedStyle(canvas).getPropertyValue('--v-ink').trim();
        ctx.lineWidth = 1.5;
        ctx.strokeRect(0.75, y + 0.75, r.length * cell - 1.5, height - 1.5);
      }
    });
  }

  $effect(() => {
    void [rows, cell, height, gap, diverging, scale, highlight, tick];
    draw();
  });
  onMount(() => onTheme(() => tick++));

  function move(e: PointerEvent) {
    if (!onhover) return;
    const rect = canvas.getBoundingClientRect();
    const x = e.clientX - rect.left;
    const y = e.clientY - rect.top;
    const row = Math.floor(y / (height + gap));
    const col = Math.floor(x / cell);
    if (row < 0 || row >= rows.length || col < 0 || col >= rows[row].length) onhover(null, null, null, e);
    else onhover(row, col, rows[row][col], e);
  }
</script>

<canvas
  bind:this={canvas}
  style:width="{width}px"
  style:height="{Math.max(1, total)}px"
  style:display="block"
  aria-label={label}
  onpointermove={move}
  onpointerleave={(e) => onhover?.(null, null, null, e)}
></canvas>
