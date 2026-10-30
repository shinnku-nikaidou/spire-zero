<script lang="ts">
  // How the Pump runs a card play (docs: simulator/headless §7): continuations queue on one thread and run in the
  // order they were posted; a real-time wait leaves the queue empty until a timer thread posts, which settling
  // counts as an idle wait. The steps are a simplified card play, not a trace of the game.
  import type { Lang } from '../v/names.ts';
  import { w, say, words, type Words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));

  interface Frame {
    note: Words;
    queue: string[];
    ran?: string;
    idle?: boolean;
    settled?: boolean;
  }

  const PLAY: Frame[] = [
    { note: w('The driver starts the action: CombatActions.Start(play:7:2). The card play begins and awaits its first step; its continuation is posted.', '驱动程序开始动作：CombatActions.Start(play:7:2)。出牌开始，等待第一步；它的续体被投递。', '驅動程式開始動作：CombatActions.Start(play:7:2)。出牌開始，等待第一步；它的續體被投遞。'), queue: ['PlayCardAction'] },
    { note: w('Drain runs it on the driver’s thread. Playing the card raises the hook “before card played”, which awaits each listener.', 'Drain 在驱动程序的线程上执行它。出牌触发“出牌前”钩子，逐个等待监听者。', 'Drain 在驅動程式的執行緒上執行它。出牌觸發「出牌前」鉤子，逐一等待監聽者。'), queue: ['BeforeCardPlayed: relic', 'BeforeCardPlayed: power'], ran: 'PlayCardAction' },
    { note: w('They run in the order they were posted, never in parallel.', '它们按投递的顺序执行，从不并行。', '它們按投遞的順序執行，從不平行。'), queue: ['BeforeCardPlayed: power', 'DamageCmd'], ran: 'BeforeCardPlayed: relic' },
    { note: w('The damage resolves, then the “after damage” hooks.', '伤害结算，然后是“受伤后”钩子。', '傷害結算，然後是「受傷後」鉤子。'), queue: ['DamageCmd', 'AfterDamage: Thorns'], ran: 'BeforeCardPlayed: power' },
    { note: w('Each step posts what comes next; the queue empties as the play finishes.', '每一步投递下一步；出牌结束时队列清空。', '每一步投遞下一步；出牌結束時佇列清空。'), queue: ['AfterDamage: Thorns'], ran: 'DamageCmd' },
    { note: w('Queue empty, executor idle, the player’s turn: WaitingForInput holds. The combat has settled, and the next decision can be asked.', '队列空了，执行器闲置，轮到玩家：WaitingForInput 成立。战斗已经稳定，可以问下一个决策。', '佇列空了，執行器閒置，輪到玩家：WaitingForInput 成立。戰鬥已經穩定，可以問下一個決策。'), queue: [], ran: 'AfterDamage: Thorns', settled: true },
  ];
  const DELAY: Frame[] = [
    PLAY[0],
    PLAY[1],
    { note: w('Suppose a listener calls Task.Delay(300), as Amalgamator’s event code does unpatched: its continuation will be posted by a timer thread 300 ms later, not by this thread.', '假设一个监听者调用 Task.Delay(300)，就像未打补丁的 Amalgamator 事件代码那样：它的续体要 300 ms 后由计时器线程投递，而不是这个线程。', '假設一個監聽者呼叫 Task.Delay(300)，就像未打補丁的 Amalgamator 事件程式碼那樣：它的續體要 300 ms 後由計時器執行緒投遞，而不是這個執行緒。'), queue: ['BeforeCardPlayed: power'], ran: 'BeforeCardPlayed: relic (Task.Delay)' },
    { note: w('The queue runs dry. Is the play over? RunUntil cannot tell “done” from “not yet”, so it blocks for work from another thread: one idle wait.', '队列空了。出牌结束了吗？RunUntil 分不出“完成”和“还没”，只好等其他线程投递：一次空等。', '佇列空了。出牌結束了嗎？RunUntil 分不出「完成」和「還沒」，只好等其他執行緒投遞：一次空等。'), queue: [], ran: 'BeforeCardPlayed: power', idle: true },
    { note: w('The timer posts the rest of the delayed listener. Timing now decides when it runs: the order of events depends on the clock. The run’s line reports “pump idle waits”, which must be 0; such delays are patched away.', '计时器投递了延迟监听者的剩余部分。现在由时间决定它何时执行：事件顺序取决于时钟。对局输出会报告 “pump idle waits”，必须为 0；这类延迟都用补丁去掉。', '計時器投遞了延遲監聽者的剩餘部分。現在由時間決定它何時執行：事件順序取決於時鐘。對局輸出會報告「pump idle waits」，必須為 0；這類延遲都用補丁去掉。'), queue: ['(timer) rest of the relic listener'], idle: true },
  ];

  let delayed = $state(false);
  let i = $state(0);
  const frames = $derived(delayed ? DELAY : PLAY);
  const f = $derived(frames[Math.min(i, frames.length - 1)]);
  function mode(d: boolean) {
    delayed = d;
    i = 0;
  }
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.75rem;">
    <div class="v-tabs" style="margin: 0;">
      <button aria-pressed={!delayed} onclick={() => mode(false)}>{say(lang, w('A card play', '一次出牌', '一次出牌'))}</button>
      <button aria-pressed={delayed} onclick={() => mode(true)}>{say(lang, w('With a real-time wait', '加上真实时间等待', '加上真實時間等待'))}</button>
    </div>
    <button onclick={() => (i = Math.max(0, i - 1))} disabled={i === 0}>{t('back')}</button>
    <button onclick={() => (i = Math.min(frames.length - 1, i + 1))} disabled={i >= frames.length - 1}>{t('next')}</button>
    <span class="v-muted v-num">{i + 1} / {frames.length}</span>
  </div>

  <div class="lanes">
    <div>
      <div class="v-muted">{say(lang, w('Driver thread runs', '驱动程序线程执行', '驅動程式執行緒執行'))}</div>
      <div class="box run">{f.ran ?? '–'}</div>
    </div>
    <div>
      <div class="v-muted">{say(lang, w('Pump queue (next first)', 'Pump 队列（左边先执行）', 'Pump 佇列（左邊先執行）'))}</div>
      <div class="queue">
        {#each f.queue as q (q)}<span class="box">{q}</span>{/each}
        {#if f.queue.length === 0}<span class="v-muted">{say(lang, w('empty', '空', '空'))}</span>{/if}
      </div>
    </div>
    <div>
      <div class="v-muted">{say(lang, w('State', '状态', '狀態'))}</div>
      {#if f.settled}<span class="tag ok">{say(lang, w('settled', '已稳定', '已穩定'))}</span>
      {:else if f.idle}<span class="tag bad">{say(lang, w('idle wait', '空等', '空等'))}</span>
      {:else}<span class="tag">{say(lang, w('pumping', '执行中', '執行中'))}</span>{/if}
    </div>
  </div>
  <p class="v-ink2" style="margin: 0.75rem 0 0;">{say(lang, f.note)}</p>
  <p class="v-caption">{say(lang, w('A simplified card play: the names of the queued steps are illustrative, not the game’s exact sequence.', '简化的一次出牌：队列里步骤的名字是示意，不是游戏的确切顺序。', '簡化的一次出牌：佇列裡步驟的名字是示意，不是遊戲的確切順序。'))}</p>
</div>

<style>
  .lanes {
    display: grid;
    grid-template-columns: minmax(8rem, 1fr) minmax(12rem, 2fr) minmax(6rem, auto);
    gap: 0.75rem;
    align-items: start;
  }
  .queue {
    display: flex;
    flex-wrap: wrap;
    gap: 0.35rem;
    min-height: 2rem;
  }
  .box {
    display: inline-block;
    border: 1px solid var(--v-axis);
    border-radius: 0.375rem;
    padding: 0.2rem 0.5rem;
    font-family: var(--sl-font-mono, monospace);
    font-size: 0.75rem;
  }
  .box.run {
    border-color: var(--v-s1);
    background: var(--v-wash);
  }
  .tag {
    display: inline-block;
    border-radius: 0.375rem;
    padding: 0.15rem 0.5rem;
    border: 1px solid var(--v-axis);
  }
  .tag.ok {
    border-color: var(--v-s3);
    color: var(--v-s3);
  }
  .tag.bad {
    border-color: var(--v-s8);
    color: var(--v-s8);
  }
  @media (max-width: 40rem) {
    .lanes {
      grid-template-columns: 1fr;
    }
  }
</style>
