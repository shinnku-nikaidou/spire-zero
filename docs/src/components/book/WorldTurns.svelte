<script lang="ts">
  // Worlds that take turns on one live combat (docs: simulator/worlds §2): acting in the live world is one step;
  // acting in another first rebuilds it from the root by replaying its trace, at the measured NoSL restore cost.
  import type { Lang } from '../v/names.ts';
  import { fmt } from '../v/i18n.ts';
  import { w, say } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();

  const K = 4;
  const RESTORE = 0.28;
  const PER = 0.073;
  const STEP = 0.073;
  // Each world's trace starts with its determinize step.
  let traces = $state(Array.from({ length: K }, () => 1));
  let live = $state<number | null>(null);
  let cost = $state(0);
  let steps = $state(0);
  let rebuilds = $state(0);
  let last = $state<{ world: number; rebuilt: boolean; ms: number } | null>(null);

  function act(k: number) {
    let ms = STEP;
    const rebuilt = live !== k;
    if (rebuilt) {
      ms += RESTORE + PER * traces[k];
      rebuilds++;
    }
    traces[k]++;
    live = k;
    cost += ms;
    steps++;
    last = { world: k, rebuilt, ms };
  }
  function reset() {
    traces = Array.from({ length: K }, () => 1);
    live = null;
    cost = 0;
    steps = 0;
    rebuilds = 0;
    last = null;
  }
  function depthFirst() {
    for (let k = 0; k < K; k++) for (let s = 0; s < 5; s++) act(k);
  }
  function roundRobin() {
    for (let s = 0; s < 5; s++) for (let k = 0; k < K; k++) act(k);
  }
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.75rem;">
    {#each traces as n, k (k)}
      <button aria-pressed={live === k} onclick={() => act(k)}>
        {say(lang, w(`Act in world ${k + 1}`, `在世界 ${k + 1} 走一步`, `在世界 ${k + 1} 走一步`))}
      </button>
    {/each}
  </div>
  <div class="v-row" style="margin-bottom: 0.75rem;">
    <button onclick={depthFirst}>{say(lang, w('5 steps per world, one world after another', '每个世界 5 步，一个接一个', '每個世界 5 步，一個接一個'))}</button>
    <button onclick={roundRobin}>{say(lang, w('5 rounds of one step in each world', '5 轮，每轮每个世界一步', '5 輪，每輪每個世界一步'))}</button>
    <button onclick={reset}>{say(lang, w('Reset', '重来', '重來'))}</button>
  </div>
  <div class="v-scroll">
    <table>
      <thead>
        <tr>
          <th>{say(lang, w('world', '世界', '世界'))}</th>
          <th>{say(lang, w('trace (steps since the root)', '轨迹（从根开始的步数）', '軌跡（從根開始的步數）'))}</th>
          <th>{say(lang, w('in the live combat', '是否就是那场活着的战斗', '是否就是那場活著的戰鬥'))}</th>
        </tr>
      </thead>
      <tbody>
        {#each traces as n, k (k)}
          <tr>
            <td>{k + 1}</td>
            <td class="v-num">{n}</td>
            <td>{live === k ? say(lang, w('yes', '是', '是')) : '–'}</td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
  <div class="v-row" style="margin-top: 0.5rem;">
    <div class="v-stat"><span class="v-label">{say(lang, w('steps', '步数', '步數'))}</span><span class="v-value v-num">{steps}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, w('rebuilds', '重建次数', '重建次數'))}</span><span class="v-value v-num">{rebuilds}</span></div>
    <div class="v-stat"><span class="v-label">{say(lang, w('time', '时间', '時間'))}</span><span class="v-value v-num">{fmt(lang, cost, 2)} ms</span></div>
  </div>
  {#if last}
    <p class="v-ink2" style="margin: 0.5rem 0 0;">
      {last.rebuilt
        ? say(lang, w(
            `World ${last.world + 1} was not live: a new combat from the root, its trace replayed, then the step (${fmt(lang, last.ms, 2)} ms).`,
            `世界 ${last.world + 1} 不是活着的那一个：从根开一场新战斗，重播它的轨迹，再走这一步（${fmt(lang, last.ms, 2)} ms）。`,
            `世界 ${last.world + 1} 不是活著的那一個：從根開一場新戰鬥，重播它的軌跡，再走這一步（${fmt(lang, last.ms, 2)} ms）。`))
        : say(lang, w(
            `World ${last.world + 1} was live: just the step (${fmt(lang, last.ms, 2)} ms).`,
            `世界 ${last.world + 1} 正是活着的那一个：只走这一步（${fmt(lang, last.ms, 2)} ms）。`,
            `世界 ${last.world + 1} 正是活著的那一個：只走這一步（${fmt(lang, last.ms, 2)} ms）。`))}
    </p>
  {/if}
  <p class="v-caption">
    {say(lang, w(
      'Illustrative: four NoSL worlds of one decision, each starting with its determinize step. The costs are the measured models (a restore 0.28 ms + 0.073 ms per replayed action; a step counted as one action), not a trace of a search. A search that finishes one world before the next rebuilds each once; one that hops between worlds rebuilds at every hop.',
      '示意：同一个决策的四个 NoSL 世界，每个都以它的确定化步骤开始。成本用的是实测模型（还原 0.28 ms + 每个重播动作 0.073 ms；一步算一个动作），不是真实搜索的记录。一个世界搜完再搜下一个，每个世界只重建一次；在世界之间来回跳，每跳一次就重建一次。',
      '示意：同一個決策的四個 NoSL 世界，每個都以它的確定化步驟開始。成本用的是實測模型（還原 0.28 ms + 每個重播動作 0.073 ms；一步算一個動作），不是真實搜尋的紀錄。一個世界搜完再搜下一個，每個世界只重建一次；在世界之間來回跳，每跳一次就重建一次。',
    ))}
  </p>
</div>
