<script lang="ts">
  // The opening of one SL run on the wire (docs: protocol/wire §4), message by message, in the forms Wire.cs writes:
  // the type, then v, then the fields in declaration order, snake_case, nulls left out. The run is the book's example
  // run (seed 0SSL7NS01MU6, run id ce13ba5116b7f48a); observations, hashes, the checkpoint and the picks are abbreviated
  // or made up, so this is an illustration of the format, not a recording.
  import type { Lang } from '../v/names.ts';
  import { w, say, words, type Words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));

  interface Step {
    from: 'host' | 'brain';
    json: string;
    note: Words;
  }
  const CHAMPION = 'combat plan c7-2 beam 8x300 easy 4 loss plans 3, combat prompts p0, macro l0 v7-1f clone, macro prompts l0 v7-1f clone';
  const STEPS: Step[] = [
    {
      from: 'host',
      json: '{"type":"hello","v":1,"game_version":"v0.111.0","env":"SL","role":"worker","run":{"run_id":"ce13ba5116b7f48a","character":"SILENT","ascension":0}}',
      note: w('A run starts. The run id is a hash of the seed. The environment is fixed for the whole run.', '一局开始。run id 是种子的哈希。环境在整局里固定不变。', '一局開始。run id 是種子的雜湊。環境在整局裡固定不變。'),
    },
    {
      from: 'brain',
      json: `{"type":"ready","v":1,"champion":"${CHAMPION}"}`,
      note: w('The brain makes a Harness for the run from its champion and names it. Had it refused the run, an error would come instead.', '大脑按它的冠军为这一局建一个 Harness，并报出冠军的名字。若它拒绝这一局，回来的会是 error。', '大腦按它的冠軍為這一局建一個 Harness，並報出冠軍的名字。若它拒絕這一局，回來的會是 error。'),
    },
    {
      from: 'host',
      json: '{"type":"node_checkpoint","v":1,"node_index":0,"checkpoint":"{\\"Save\\":\\"…\\",\\"Relics\\":[…]}"}',
      note: w('SL only: the first node’s checkpoint, sent just before the node’s first decision. The checkpoint is a JSON string of its own: the game’s save and the relics’ fields.', '仅限 SL：第一个节点的检查点，在节点第一个决策之前发出。检查点本身是一个 JSON 字符串：游戏的存档加上遗物的字段。', '僅限 SL：第一個節點的檢查點，在節點第一個決策之前送出。檢查點本身是一個 JSON 字串：遊戲的存檔加上遺物的欄位。'),
    },
    {
      from: 'host',
      json: '{"type":"decision","v":1,"decision_id":0,"obs_hash":"…","surface":"event","obs":{"run":{…},"screen":{"surface":"event","id":"NEOW",…}},"legal":["event:0","event:1","event:2"]}',
      note: w('The first decision: Neow’s event, three options. The observation hash lets the brain check that the observation arrived intact.', '第一个决策：涅奥的事件，三个选项。观测哈希让大脑能检查观测是否完整到达。', '第一個決策：涅奧的事件，三個選項。觀測雜湊讓大腦能檢查觀測是否完整到達。'),
    },
    {
      from: 'brain',
      json: '{"type":"recommendation","v":1,"decision_id":0,"obs_hash":"…","action":"event:0","brain":"l0 v7-1f clone"}',
      note: w('The answer, with the brain that chose it. The macro brain sends no plan here; a combat search would add its plan and candidates.', '回答，附上做出选择的大脑。宏观大脑在这里不附计划；战斗搜索会附上它的计划与候选。', '回答，附上做出選擇的大腦。宏觀大腦在這裡不附計畫；戰鬥搜尋會附上它的計畫與候選。'),
    },
    {
      from: 'host',
      json: '{"type":"executed","v":1,"decision_id":0,"action":"event:0","ok":true}',
      note: w('Sent once the game has reached the next decision: the action ran.', '游戏走到下一个决策时才发出：这个动作执行完了。', '遊戲走到下一個決策時才送出：這個動作執行完了。'),
    },
    {
      from: 'host',
      json: '{"type":"node_action","v":1,"node_index":0,"seq":0,"action":"event event:0"}',
      note: w('SL only: the node’s decision so far, in its trace form: the surface, a space, the action.', '仅限 SL：节点到目前为止的决策，用轨迹的写法：决策界面、空格、动作。', '僅限 SL：節點到目前為止的決策，用軌跡的寫法：決策介面、空格、動作。'),
    },
    {
      from: 'host',
      json: '{"type":"decision","v":1,"decision_id":1,"obs_hash":"…","surface":"card_choice","obs":{"run":{…},"screen":{"surface":"card_choice","choice":{"options":[…],"min":1,"max":1,"context":{…}}}},"legal":[]}',
      note: w('A card prompt opened by the option. Its legal list is empty: the prompt itself says what an answer may be (min, max, options).', '选项打开了一个卡牌提示。它的合法动作列表是空的：提示本身说明怎样的回答才算数（min、max、选项）。', '選項打開了一個卡牌提示。它的合法動作列表是空的：提示本身說明怎樣的回答才算數（min、max、選項）。'),
    },
    {
      from: 'brain',
      json: '{"type":"recommendation","v":1,"decision_id":1,"obs_hash":"…","action":"pick:2","brain":"l0 v7-1f clone"}',
      note: w('A pick, by the options’ canonical indexes.', '一次挑选，用选项的规范顺序下标。', '一次挑選，用選項的規範順序索引。'),
    },
    {
      from: 'host',
      json: '{"type":"executed","v":1,"decision_id":1,"action":"pick:2","ok":true}',
      note: w('The pick ran.', '这次挑选执行了。', '這次挑選執行了。'),
    },
    {
      from: 'host',
      json: '{"type":"node_action","v":1,"node_index":0,"seq":1,"action":"card_choice 4"}',
      note: w('The same pick in the node’s trace, by the game’s own index: the clone replays it through the game’s card selector, which knows no canonical order.', '同一次挑选进入节点轨迹，用的是游戏自己的下标：克隆通过游戏的卡牌选择器重播它，选择器不认识规范顺序。', '同一次挑選進入節點軌跡，用的是遊戲自己的索引：複本透過遊戲的卡牌選擇器重播它，選擇器不認得規範順序。'),
    },
    {
      from: 'host',
      json: '{"type":"node_checkpoint","v":1,"node_index":1,"checkpoint":"…"}',
      note: w('Later, after the travel to the next map point: a new node starts with its own checkpoint. The travel itself is never sent as a node action; the old node ends with it.', '之后，移动到下一个地图点：新的节点以它自己的检查点开始。移动本身从不作为节点动作发出；旧节点随它结束。', '之後，移動到下一個地圖點：新的節點以它自己的檢查點開始。移動本身從不作為節點動作送出；舊節點隨它結束。'),
    },
    {
      from: 'host',
      json: '{"type":"run_end","v":1,"won":true,"floor":48,"reason":"won"}',
      note: w('Much later: the end of the run. The connection can carry the next run’s hello.', '很久之后：这一局结束。同一个连接可以接着送下一局的 hello。', '很久之後：這一局結束。同一個連線可以接著送下一局的 hello。'),
    },
  ];
  let i = $state(0);
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.75rem;">
    <button onclick={() => (i = Math.max(0, i - 1))} disabled={i === 0}>{t('back')}</button>
    <button onclick={() => (i = Math.min(STEPS.length - 1, i + 1))} disabled={i >= STEPS.length - 1}>{t('next')}</button>
    <span class="v-muted v-num">{i + 1} / {STEPS.length}</span>
  </div>
  <ol class="log">
    {#each STEPS.slice(0, i + 1) as s, k (k)}
      <li class:now={k === i} class:brain={s.from === 'brain'}>
        <span class="dir">{s.from === 'host' ? say(lang, w('host → brain', '宿主 → 大脑', '宿主 → 大腦')) : say(lang, w('brain → host', '大脑 → 宿主', '大腦 → 宿主'))}</span>
        <code>{s.json}</code>
      </li>
    {/each}
  </ol>
  <p class="v-ink2" style="margin: 0.5rem 0 0;">{say(lang, STEPS[i].note)}</p>
  <p class="v-caption">
    {say(lang, w(
      'The opening of the book’s example run as an SL host would send it, one JSON line per message. The forms are the code’s; observations, hashes, the checkpoint and the picks are abbreviated (…) or made up.',
      '贯穿全书的那一局开头，按 SL 宿主发送的样子，每条消息一行 JSON。格式与代码一致；观测、哈希、检查点与挑选是省略的（…）或编造的。',
      '貫穿全書的那一局開頭，按 SL 宿主送出的樣子，每則訊息一行 JSON。格式與程式碼一致；觀測、雜湊、檢查點與挑選是省略的（…）或編造的。',
    ))}
  </p>
</div>

<style>
  .log {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
    max-height: 22rem;
    overflow-y: auto;
  }
  .log li {
    display: grid;
    grid-template-columns: 6.5rem minmax(0, 1fr);
    gap: 0.5rem;
    align-items: start;
    opacity: 0.55;
    margin: 0;
  }
  .log li.now {
    opacity: 1;
  }
  .log li code {
    font-size: 0.72rem;
    white-space: pre-wrap;
    word-break: break-all;
    border-left: 2px solid var(--v-s1);
    padding-left: 0.4rem;
  }
  .log li.brain code {
    border-left-color: var(--v-s2);
  }
  .dir {
    font-size: 0.75rem;
    color: var(--v-ink-2);
  }
</style>
