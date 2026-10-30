<script lang="ts">
  // How WorkerPool.Map serves a set of requests (docs: simulator/pool §2): each goes to whichever worker is free, they
  // finish in any order, and the replies come back in request order. Durations are drawn, not measured.
  import type { Lang } from '../v/names.ts';
  import { w, say } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();

  const REQUESTS = 8;
  let workers = $state(3);
  let draw = $state(1);

  function mulberry32(a: number) {
    return () => {
      a |= 0;
      a = (a + 0x6d2b79f5) | 0;
      let t = Math.imul(a ^ (a >>> 15), 1 | a);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }
  const durations = $derived.by(() => {
    const r = mulberry32(draw * 7919);
    return Array.from({ length: REQUESTS }, () => 0.4 + r() * 1.6);
  });
  // Requests are taken in order by the first worker free, as Parallel.For over a queue of free workers does.
  const schedule = $derived.by(() => {
    const free = Array.from({ length: workers }, (_, k) => ({ k, at: 0 }));
    return durations.map((d, i) => {
      free.sort((a, b) => a.at - b.at || a.k - b.k);
      const slot = free[0];
      const start = slot.at;
      slot.at = start + d;
      return { i, worker: slot.k, start, end: start + d };
    });
  });
  const finished = $derived([...schedule].sort((a, b) => a.end - b.end).map((s) => s.i));
  const span = $derived(Math.max(...schedule.map((s) => s.end)));

  const W = 640;
  const ROW = 26;
  const M = { l: 70, r: 12, t: 8, b: 26 };
  const H = $derived(M.t + M.b + ROW * workers);
  const x = (t: number) => M.l + (t / span) * (W - M.l - M.r);
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.75rem;">
    <label>
      {say(lang, w('Workers', '工作进程', '工作行程'))}
      <input type="range" min="1" max="8" step="1" bind:value={workers} />
      <span class="v-num">{workers}</span>
    </label>
    <button onclick={() => draw++}>{say(lang, w('Draw other durations', '换一组耗时', '換一組耗時'))}</button>
  </div>
  <svg viewBox="0 0 {W} {H}" width="100%" role="img" aria-label="worker pool schedule">
    {#each Array.from({ length: workers }, (_, k) => k) as k (k)}
      <text x={M.l - 8} y={M.t + ROW * k + ROW / 2 + 4} text-anchor="end">{say(lang, w(`worker ${k + 1}`, `进程 ${k + 1}`, `行程 ${k + 1}`))}</text>
    {/each}
    {#each schedule as s (s.i)}
      <rect x={x(s.start) + 1} y={M.t + ROW * s.worker + 3} width={Math.max(2, x(s.end) - x(s.start) - 2)} height={ROW - 6} rx="3" fill="var(--v-wash)" stroke="var(--v-s1)" />
      <text x={(x(s.start) + x(s.end)) / 2} y={M.t + ROW * s.worker + ROW / 2 + 4} text-anchor="middle">{s.i}</text>
    {/each}
    <text x={M.l} y={H - 6}>{say(lang, w('time →', '时间 →', '時間 →'))}</text>
  </svg>
  <table>
    <tbody>
      <tr>
        <td class="v-ink2">{say(lang, w('finished in the order', '完成的顺序', '完成的順序'))}</td>
        <td class="v-num">{finished.join(', ')}</td>
      </tr>
      <tr>
        <td class="v-ink2">{say(lang, w('replies returned in the order', '回覆交回的顺序', '回覆交回的順序'))}</td>
        <td class="v-num">{schedule.map((s) => s.i).join(', ')}</td>
      </tr>
    </tbody>
  </table>
  <p class="v-caption">
    {say(lang, w(
      'Illustrative: eight requests of one decision (one per NoSL world, say), with drawn durations. However many workers there are and whatever order the requests finish in, the caller gets the replies in request order, so it merges them the same way every time.',
      '示意：同一个决策的八个请求（例如每个 NoSL 世界一个），耗时是随机抽的。不管有几个工作进程、请求按什么顺序完成，调用方拿到的回覆总是按请求的顺序排列，所以每次都以同样的方式合并。',
      '示意：同一個決策的八個請求（例如每個 NoSL 世界一個），耗時是隨機抽的。不管有幾個工作行程、請求按什麼順序完成，呼叫端拿到的回覆總是按請求的順序排列，所以每次都以同樣的方式合併。',
    ))}
  </p>
</div>
