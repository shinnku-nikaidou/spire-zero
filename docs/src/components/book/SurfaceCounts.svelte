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
      ? w('combat brain', '战斗大脑', '戰鬥大腦')
      : s === 'card_choice'
        ? w('in combat the combat brain, outside it the macro brain', '战斗中由战斗大脑，战斗外由宏观大脑', '戰鬥中由戰鬥大腦，戰鬥外由宏觀大腦')
        : w('macro brain', '宏观大脑', '宏觀大腦');
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.75rem;">
    <div class="v-tabs" style="margin: 0;">
      <button aria-pressed={view === 'mean'} onclick={() => (view = 'mean')}>{say(lang, w(`Mean of ${N} runs`, `${N} 局的平均`, `${N} 局的平均`))}</button>
      <button aria-pressed={view === 'example'} onclick={() => (view = 'example')}>{say(lang, w('The example run', '示例对局', '範例對局'))}</button>
    </div>
    <span class="v-muted v-num">
      {say(lang, w('decisions per run:', '每局决策数：', '每局決策數：'))} {fmt(lang, total, view === 'mean' ? 1 : 0)}
    </span>
  </div>
  <div class="v-scroll">
    <table>
      <thead>
        <tr>
          <th>{say(lang, w('screen', '画面', '畫面'))}</th>
          <th style="width: 45%;">{view === 'mean' ? say(lang, w('decisions per run', '每局决策数', '每局決策數')) : say(lang, w('decisions', '决策数', '決策數'))}</th>
          {#if view === 'mean'}<th>{say(lang, w('runs that met it', '遇到它的局数', '遇到它的局數'))}</th>{/if}
          <th>{say(lang, w('answered by', '由谁回答', '由誰回答'))}</th>
        </tr>
      </thead>
      <tbody>
        {#each data.surfaces as s (s.surface)}
          <tr>
            <td>{label(s.surface)}</td>
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
      `How often each screen came up in the champion’s ${N} Silent runs (${data.won} won). A card choice is counted together wherever it comes up: ${prompts.decisions} in all, ${data.card_choice_macro.all} of them outside combat.`,
      `冠军打静默猎手的 ${N} 局（赢 ${data.won} 局）里，每种画面出现几次。选牌不管在哪里出现都算在一起：总共 ${prompts.decisions} 次，其中 ${data.card_choice_macro.all} 次在战斗之外。`,
      `冠軍打寂靜獵手的 ${N} 局（贏 ${data.won} 局）裡，每種畫面出現幾次。選牌不論在哪裡出現都算在一起：總共 ${prompts.decisions} 次，其中 ${data.card_choice_macro.all} 次在戰鬥之外。`,
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
