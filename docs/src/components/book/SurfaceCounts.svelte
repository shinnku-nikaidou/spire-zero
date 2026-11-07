<script lang="ts">
  // How often a run meets each decision screen (docs: simulator/runs §4): the surfaces counts of the champion's Silent
  // report, summed over its runs (src/data/book/surface-counts.json), against the book's example run.
  import type { Lang } from '../v/names.ts';
  import { surface, fmt } from '../v/i18n.ts';
  import { w, say, type Words } from './words.ts';
  import data from '../../data/book/surface-counts.json';

  let { lang = 'en' }: { lang?: Lang } = $props();

  type View = 'mean' | 'example';
  let view = $state<View>('mean');
  const N = data.runs;
  const prompts = data.surfaces.find((s) => s.surface === 'card_choice')!;
  const OWN: Record<string, Words> = {
    combat: w('combat', '战斗', '戰鬥'),
    crystal_sphere: w('crystal sphere', '水晶球', '水晶球'),
  };
  const label = (s: string) => (OWN[s] ? say(lang, OWN[s]) : surface(lang, s));
  const value = (s: (typeof data.surfaces)[number]) => (view === 'mean' ? s.decisions / N : s.example);
  const max = $derived(Math.max(...data.surfaces.map(value)));
  const total = $derived(data.surfaces.reduce((a, s) => a + value(s), 0));
  const who = (s: string): Words =>
    s === 'combat'
      ? w('combat brain: its search', '战斗大脑：它的搜索', '戰鬥大腦：它的搜尋')
      : s === 'card_choice'
        ? w('in combat first or the search’s line, outside the macro brain', '战斗中 first 或搜索的那条打法，战斗外宏观大脑', '戰鬥中 first 或搜尋的那條打法，戰鬥外宏觀大腦')
        : w('macro brain', '宏观大脑', '宏觀大腦');
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.75rem;">
    <div class="v-tabs" style="margin: 0;">
      <button aria-pressed={view === 'mean'} onclick={() => (view = 'mean')}>{say(lang, w(`Mean of ${N} runs`, `${N} 局的平均`, `${N} 局的平均`))}</button>
      <button aria-pressed={view === 'example'} onclick={() => (view = 'example')}>{say(lang, w('The example run', '贯穿全书的那一局', '貫穿全書的那一局'))}</button>
    </div>
    <span class="v-muted v-num">
      {say(lang, w('decisions per run:', '每局决策数：', '每局決策數：'))} {fmt(lang, total, view === 'mean' ? 1 : 0)}
    </span>
  </div>
  <div class="v-scroll">
    <table>
      <thead>
        <tr>
          <th>{say(lang, w('surface', '决策界面', '決策介面'))}</th>
          <th style="width: 45%;">{view === 'mean' ? say(lang, w('decisions per run', '每局决策数', '每局決策數')) : say(lang, w('decisions', '决策数', '決策數'))}</th>
          {#if view === 'mean'}<th>{say(lang, w('runs that met it', '遇到它的局数', '遇到它的局數'))}</th>{/if}
          <th>{say(lang, w('answered by', '由谁回答', '由誰回答'))}</th>
        </tr>
      </thead>
      <tbody>
        {#each data.surfaces as s (s.surface)}
          <tr>
            <td>{label(s.surface)}{#if label(s.surface) !== s.surface}&nbsp;<code>{s.surface}</code>{/if}</td>
            <td>
              <span class="bar" style:width="{(value(s) / max) * 80}%"></span>
              <span class="v-num">{fmt(lang, value(s), view === 'mean' && value(s) < 10 ? 2 : view === 'mean' ? 1 : 0)}</span>
            </td>
            {#if view === 'mean'}<td class="v-num">{s.runs} / {N}</td>{/if}
            <td class="v-ink2">{say(lang, who(s.surface))}</td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
  <p class="v-caption">
    {say(lang, w(
      `The surfaces counts of the run records in the champion’s Silent report on promotion-1-100 (${N} runs, ${data.won} won). A card prompt is counted with the screen kind card_choice wherever it is asked: of ${prompts.decisions} in all, ${data.card_choice_macro.all} came outside combat. The example run (seed ${data.example.seed}, won at floor ${data.example.floor}) had ${data.card_choice_macro.example} of its ${prompts.example} outside combat.`,
      `冠军的静默猎手报告（promotion-1-100）里，各对局记录的决策界面计数（${N} 局，赢 ${data.won} 局）。卡牌提示不管在哪里出现，都算作 card_choice：总共 ${prompts.decisions} 次，其中 ${data.card_choice_macro.all} 次在战斗之外。贯穿全书的那一局（种子 ${data.example.seed}，在第 ${data.example.floor} 层获胜）的 ${prompts.example} 次里有 ${data.card_choice_macro.example} 次在战斗之外。`,
      `冠軍的寂靜獵手報告（promotion-1-100）裡，各對局記錄的決策介面計數（${N} 局，贏 ${data.won} 局）。卡牌提示不論在哪裡出現，都算作 card_choice：總共 ${prompts.decisions} 次，其中 ${data.card_choice_macro.all} 次在戰鬥之外。貫穿全書的那一局（種子 ${data.example.seed}，在第 ${data.example.floor} 層獲勝）的 ${prompts.example} 次裡有 ${data.card_choice_macro.example} 次在戰鬥之外。`,
    ))}
  </p>
</div>

<style>
  .bar {
    display: inline-block;
    height: 0.6rem;
    background: var(--v-s1);
    border-radius: 2px;
    margin-right: 0.4rem;
    vertical-align: 0;
    min-width: 1px;
  }
</style>
