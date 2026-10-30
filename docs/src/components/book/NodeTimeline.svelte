<script lang="ts">
  // The first three nodes of the run v6-3-2 won (src/data/book/example-nodes.json): each node's steps, where the game
  // itself saves, and what NoSL and SL may know at each step (docs: environments/information §3).
  import data from '../../data/book/example-nodes.json';
  import { loadNames, type Lang, type Names } from '../v/names.ts';
  import { surface } from '../v/i18n.ts';
  import { w, say, words, type Words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));

  interface Step {
    kind: 'arrive' | 'decision' | 'combat' | 'prompt' | 'save' | 'roll';
    surface?: string;
    action?: string;
    decision?: number;
    decisions?: number[];
    save?: string;
    encounter?: string;
    hp?: number;
    max_hp?: number;
    end_hp?: number;
  }
  interface Node {
    floor: number;
    point: string;
    steps: Step[];
  }
  const NODES = data.nodes as Node[];

  let names = $state<Names | null>(null);
  $effect(() => {
    loadNames(lang).then((n) => (names = n));
  });

  let n = $state(1);
  let s = $state(1);
  let env = $state<'NoSL' | 'SL'>('NoSL');
  const node = $derived(NODES[n]);
  const step = $derived(node.steps[Math.min(s, node.steps.length - 1)]);

  const SAVE: Record<string, Words> = {
    run_start: w('arrival at the run’s first point', '抵达本局第一个点', '抵達本局第一個點'),
    arrival: w('arrival, before the room is built', '抵达，建房间之前', '抵達，建房間之前'),
    combat_won: w('combat won', '战斗胜利', '戰鬥勝利'),
    ancient: w('Ancient finished', '先古之民事件结束', '遠古之民事件結束'),
  };

  function label(st: Step): string {
    switch (st.kind) {
      case 'arrive':
        return say(lang, w('Arrive', '抵达', '抵達'));
      case 'save':
        return `◆ ${say(lang, w('Game saves', '游戏存档', '遊戲存檔'))}`;
      case 'roll':
        return say(lang, w('The ? becomes an event', '? 变成事件', '? 變成事件'));
      case 'prompt':
        return say(lang, w('Card prompt', '卡牌提示', '卡牌提示'));
      case 'combat':
        return `${say(lang, w('Combat', '战斗', '戰鬥'))}: ${names ? names.full('encounter', st.encounter!) : st.encounter}`;
      default:
        return `${surface(lang, st.surface!)}: ${st.action}`;
    }
  }

  function detail(st: Step): string {
    switch (st.kind) {
      case 'arrive':
        return st.save === 'run_start'
          ? say(lang, w('The run starts here. Entering the first point saves the run, as every arrival does; there is no separate start save. Node 0 starts here.', '一局从这里开始。进入第一个点时存档，和每次抵达一样；没有另外的开局存档。节点 0 从这里开始。', '一局從這裡開始。進入第一個點時存檔，和每次抵達一樣；沒有另外的開局存檔。節點 0 從這裡開始。'))
          : say(lang, w('The game saves on arrival, before it rolls a ? and builds the room. The node starts here.', '游戏在抵达时存档，在掷 ? 和建房间之前。节点从这里开始。', '遊戲在抵達時存檔，在擲 ? 和建房間之前。節點從這裡開始。'));
      case 'save':
        return st.save === 'combat_won'
          ? say(lang, w('Winning a combat saves the run with the room marked as finished: a reload comes back after the combat.', '赢下战斗时存档，房间标为已完成：重新载入会回到战斗之后。', '贏下戰鬥時存檔，房間標為已完成：重新載入會回到戰鬥之後。'))
          : say(lang, w('An Ancient event saves when it finishes, and only an Ancient event does.', '先古之民事件结束时存档，也只有先古之民事件会这样。', '遠古之民事件結束時存檔，也只有遠古之民事件會這樣。'));
      case 'combat':
        return `${say(lang, w('Decisions', '决策', '決策'))} ${st.decisions![0]}–${st.decisions![1]}; HP ${st.hp}/${st.max_hp} → ${st.end_hp}/${st.max_hp}.`;
      case 'prompt':
        return `${say(lang, w('Decision', '决策', '決策'))} ${st.decisions![0]}: ${say(lang, w('a card prompt the option opened.', '这个选项打开的卡牌提示。', '這個選項打開的卡牌提示。'))}`;
      case 'roll':
        return say(lang, w('Entering the point rolls what it becomes; both environments see the result at once.', '进入这个点时掷出它变成什么；两个环境都立刻看到结果。', '進入這個點時擲出它變成什麼；兩個環境都立刻看到結果。'));
      default:
        return `${say(lang, w('Decision', '决策', '決策'))} ${st.decision}` + (st.surface === 'map' ? ` · ${say(lang, w('the travel: the node’s last step', '移动：节点的最后一步', '移動：節點的最後一步'))}` : '');
    }
  }

  // The latest game save at or before the step: where quitting and continuing would go back to.
  const lastSave = $derived.by(() => {
    let found = '';
    for (let i = 0; i <= s && i < node.steps.length; i++) {
      const st = node.steps[i];
      if (st.save) found = st.save;
    }
    return found;
  });

  type Status = 'known' | 'partly' | 'unknown' | 'never';
  interface Row {
    what: string;
    status: Status;
    why: string;
  }

  // What each environment may know at the step, by the rules of docs: environments/information §2 and §5.
  const rows = $derived.by((): Row[] => {
    const i = Math.min(s, node.steps.length - 1);
    const at = (k: Step['kind']) => node.steps.findIndex((x) => x.kind === k);
    const sl = env === 'SL';
    const out: Row[] = [];
    if (node.point === 'Monster') {
      const fight = at('combat');
      if (i <= fight) {
        out.push({
          what: say(lang, w('This combat’s draw order', '这场战斗的抽牌顺序', '這場戰鬥的抽牌順序')),
          status: sl ? 'known' : i < fight ? 'unknown' : 'partly',
          why: sl
            ? say(lang, w('The clone can play the combat out in any line: the order and every reshuffle.', '克隆可以用任何打法把战斗打完：顺序和每次洗牌都知道。', '複本可以用任何打法把戰鬥打完：順序和每次洗牌都知道。'))
            : i < fight
              ? say(lang, w('The combat has not started: the deck is known, not the opening hand.', '战斗还没开始：知道牌组，不知道起手。', '戰鬥還沒開始：知道牌組，不知道起手。'))
              : say(lang, w('Which cards are in the pile, not their order.', '只知道牌堆里有哪些牌，不知道顺序。', '只知道牌堆裡有哪些牌，不知道順序。')),
        });
        out.push({
          what: say(lang, w('The encounter and the monsters’ moves', '遭遇与怪物的行动', '遭遇與怪物的行動')),
          status: sl ? 'known' : i < fight ? 'unknown' : 'partly',
          why: sl
            ? say(lang, w('The clone enters the room as the arrival did: the same monsters, the same moves.', '克隆像抵达时一样进入房间：同样的怪物、同样的行动。', '複本像抵達時一樣進入房間：同樣的怪物、同樣的行動。'))
            : i < fight
              ? say(lang, w('Known once the room is built.', '房间建好之后才知道。', '房間建好之後才知道。'))
              : say(lang, w('The intent each shows; the rest is inferred from their history.', '每只怪显示的意图；其余只能从历史推断。', '每隻怪顯示的意圖；其餘只能從歷史推斷。')),
        });
      }
      const rewards = node.steps.findIndex((x) => x.surface === 'rewards');
      out.push({
        what: say(lang, w('The card reward’s cards', '卡牌奖励的牌', '卡牌獎勵的牌')),
        status: i >= rewards || sl ? 'known' : 'unknown',
        why: i >= rewards
          ? say(lang, w('Shown on the rewards screen (rolled before it shows).', '奖励画面上看得到（在画面出现前就掷好了）。', '獎勵畫面上看得到（在畫面出現前就擲好了）。'))
          : sl
            ? say(lang, w('The clone can play on to the rewards screen, and see how another line of play would change them.', '克隆可以一路打到奖励画面，也能看到换一种打法奖励会怎么变。', '複本可以一路打到獎勵畫面，也能看到換一種打法獎勵會怎麼變。'))
            : say(lang, w('Not until the rewards screen shows them.', '要等奖励画面出现才知道。', '要等獎勵畫面出現才知道。')),
      });
    } else {
      const choose = node.steps.findIndex((x) => x.surface === 'event');
      if (node.point === 'Unknown') {
        const roll = at('roll');
        out.push({
          what: say(lang, w('What this ? becomes', '这个 ? 会变成什么', '這個 ? 會變成什麼')),
          status: i >= roll || sl ? 'known' : 'unknown',
          why: i >= roll
            ? say(lang, w('Rolled on entering, and shown at once.', '进入时掷出，立刻显示。', '進入時擲出，立刻顯示。'))
            : sl
              ? say(lang, w('The roll happens inside the node, so the clone sees it.', '这次掷骰在节点里，所以克隆看得到。', '這次擲骰在節點裡，所以複本看得到。'))
              : say(lang, w('Not until the point is entered.', '要进入这个点才知道。', '要進入這個點才知道。')),
        });
      }
      out.push({
        what: say(lang, w('What each event option gives', '每个事件选项会给什么', '每個事件選項會給什麼')),
        status: i > choose ? 'known' : sl ? 'known' : 'partly',
        why: i > choose
          ? say(lang, w('The option is chosen and its result shown.', '已经选了，结果也显示了。', '已經選了，結果也顯示了。'))
          : sl
            ? say(lang, w('The exact outcome of each option, played on the clone.', '每个选项的确切结果，在克隆上试出来。', '每個選項的確切結果，在複本上試出來。'))
            : say(lang, w('The options and what their text says; not what a random option will give.', '选项和它们的文字；不知道随机选项会给什么。', '選項和它們的文字；不知道隨機選項會給什麼。')),
      });
    }
    const travel = node.steps.length - 1;
    const following = NODES[n + 1];
    out.push({
      what:
        i === travel && following?.point === 'Unknown'
          ? say(lang, w('What the next ? (floor 3) becomes', '下一个 ?（第 3 层）会变成什么', '下一個 ?（第 3 層）會變成什麼'))
          : say(lang, w('Anything in a later node: its encounter, rewards, stock, what a ? becomes', '之后节点里的任何东西：遭遇、奖励、商品、? 会变成什么', '之後節點裡的任何東西：遭遇、獎勵、商品、? 會變成什麼')),
      status: 'unknown',
      why: sl
        ? say(lang, w('Not even SL: the clone stops at the travel and refuses another step.', 'SL 也不知道：克隆在移动时停下，拒绝再走一步。', 'SL 也不知道：複本在移動時停下，拒絕再走一步。'))
        : say(lang, w('Hidden until the player gets there.', '玩家走到那里之前都看不到。', '玩家走到那裡之前都看不到。')),
    });
    out.push({
      what: say(lang, w('The seed, the RNG state or the save, read directly', '直接读种子、随机数状态或存档', '直接讀種子、亂數狀態或存檔')),
      status: 'never',
      why: sl
        ? say(lang, w('Only through the sealed clone, in a worker process.', '只能通过封存的克隆，在工作进程里。', '只能透過封存的複本，在工作行程裡。'))
        : say(lang, w('Never: the observation has no field for them.', '永远不行：观测里没有这些字段。', '永遠不行：觀測裡沒有這些欄位。')),
    });
    return out;
  });

  const STATUS: Record<Status, Words> = {
    known: w('known', '知道', '知道'),
    partly: w('partly', '部分知道', '部分知道'),
    unknown: w('not known', '不知道', '不知道'),
    never: w('never', '永远不', '永遠不'),
  };
  const COLOUR: Record<Status, string> = { known: 'var(--v-s3)', partly: 'var(--v-s4)', unknown: 'var(--v-axis)', never: 'var(--v-s8)' };

  function pick(i: number) {
    n = i;
    s = 0;
  }
</script>

<div class="vfig">
  <div class="v-tabs" role="tablist">
    {#each NODES as nd, i (nd.floor)}
      <button aria-pressed={n === i} onclick={() => pick(i)}>
        {t('floor')} {nd.floor} · {names ? names.name('map', nd.point) : nd.point}{nd.point === 'Unknown' ? ' (?)' : ''}
      </button>
    {/each}
  </div>

  <div class="steps" role="list">
    {#each node.steps as st, i (i)}
      <button
        role="listitem"
        class="chip"
        class:save={st.kind === 'save'}
        class:here={i === s}
        aria-pressed={i === s}
        onclick={() => (s = i)}
      >{label(st)}</button>
    {/each}
    <span class="chip out">→ {t('floor')} {node.floor + 1}</span>
  </div>
  <div class="span" style:width="calc(100% - 4.5rem)"><span>{t('nodeSpan')}</span></div>

  <p class="v-ink2" style="margin: 0.5rem 0;">{detail(step)}</p>

  <div class="v-row" style="margin-bottom: 0.5rem;">
    <div class="v-tabs" style="margin: 0;">
      <button aria-pressed={env === 'NoSL'} onclick={() => (env = 'NoSL')}>NoSL</button>
      <button aria-pressed={env === 'SL'} onclick={() => (env = 'SL')}>SL</button>
    </div>
    <span class="v-muted">
      {say(lang, w('Quit and continue would go back to', '此时退出再继续会回到', '此時退出再繼續會回到'))}: ◆ {say(lang, SAVE[lastSave])}.
      {#if env === 'SL'}{say(lang, w('SL’s clone can go back to the node’s arrival.', 'SL 的克隆可以回到节点的抵达。', 'SL 的複本可以回到節點的抵達。'))}{/if}
    </span>
  </div>

  <table>
    <tbody>
      {#each rows as r (r.what)}
        <tr>
          <td style="width: 38%;">{r.what}</td>
          <td style="white-space: nowrap;"><span class="v-key" style:background={COLOUR[r.status]}></span> {say(lang, STATUS[r.status])}</td>
          <td class="v-ink2">{r.why}</td>
        </tr>
      {/each}
    </tbody>
  </table>
  <p class="v-caption">
    {say(
      lang,
      w(
        'The won SL run of seed 0SSL7NS01MU6: its macro decisions from the decision log, its floor-2 combat from the run record. Gaps in the decision numbers are combat decisions and card prompts. The game’s save points are where the game’s code saves.',
        '种子 0SSL7NS01MU6 那一局赢下的 SL 对局：宏观决策取自决策日志，第 2 层的战斗取自对局记录。决策编号的空缺是战斗决策与卡牌提示。游戏存档点是游戏代码存档的地方。',
        '種子 0SSL7NS01MU6 那一局贏下的 SL 對局：宏觀決策取自決策日誌，第 2 層的戰鬥取自對局記錄。決策編號的空缺是戰鬥決策與卡牌提示。遊戲存檔點是遊戲程式碼存檔的地方。',
      ),
    )}
  </p>
</div>

<style>
  .steps {
    display: flex;
    flex-wrap: wrap;
    gap: 0.35rem;
    align-items: center;
  }
  .chip {
    font-size: 0.8125rem;
  }
  .chip.save {
    border-style: dashed;
    color: var(--v-ink-2);
  }
  .chip.here {
    background: var(--v-wash);
  }
  .chip.out {
    color: var(--v-muted);
    border: 1px dashed var(--v-axis);
    border-radius: 0.375rem;
    padding: 0.2rem 0.6rem;
  }
  .span {
    margin-top: 0.35rem;
    border-top: 2px solid var(--v-s1);
    font-size: 0.75rem;
    color: var(--v-ink-2);
  }
  table {
    width: 100%;
    border-collapse: collapse;
  }
  td {
    padding: 0.3rem 0.4rem;
    border-top: 1px solid var(--v-grid);
    vertical-align: top;
  }
</style>
