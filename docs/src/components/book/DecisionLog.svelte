<script lang="ts">
  // Three lines of a decision log (docs: harness/decision-log §2), one per kind of decision, in the form Dispatcher
  // writes them: DecisionEntry's fields in declaration order, snake_case, nulls left out. The run is the book's example
  // run (seed 0SSL7NS01MU6, run id ce13ba5116b7f48a, its champion). Real: the decision numbers, surfaces and the rest
  // site's answer and run view (src/data/macro-examples.json, from the run's log), and the seeds, computed with
  // Dispatcher's formula. Made up or abbreviated: the hashes, times, the combat's actions and values, the prompt's pick.
  import type { Lang } from '../v/names.ts';
  import { w, say, type Words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();

  interface Field {
    key: string;
    value: string;
    note: Words;
  }
  interface Line {
    name: Words;
    fields: Field[];
  }

  const RUN: Field = { key: 'run', value: '"ce13ba5116b7f48a"', note: w('The run id: a hash of the run’s seed, which the brain never sees.', 'run id：这一局种子的哈希，大脑看不到种子本身。', 'run id：這一局種子的雜湊，大腦看不到種子本身。') };
  const ENV: Field = { key: 'env', value: '"SL"', note: w('The run’s environment, fixed for the whole run.', '这一局的环境，整局固定不变。', '這一局的環境，整局固定不變。') };
  const ID = (n: number): Field => ({ key: 'decision_id', value: String(n), note: w('Harness’s own count of the run’s decisions from 0, all three kinds together; not the wire’s id.', 'Harness 自己对这一局决策的计数，从 0 开始，三种决策一起数；不是线路上的 id。', 'Harness 自己對這一局決策的計數，從 0 開始，三種決策一起數；不是線路上的 id。') });
  const HASH = (what: Words): Field => ({ key: 'obs_hash', value: '"…"', note: what });
  const SEED = (s: string): Field => ({ key: 'seed', value: s, note: w('Drawn from the run id and the decision’s number (SplitMix64). Logged on every line; only NoSL PIMC reads it.', '由 run id 和决策编号抽出（SplitMix64）。每一行都记下；只有 NoSL PIMC 会用到它。', '由 run id 和決策編號抽出（SplitMix64）。每一行都記下；只有 NoSL PIMC 會用到它。') });
  const MS = (ms: string): Field => ({ key: 'elapsed_ms', value: ms, note: w('How long the brain took, in milliseconds.', '大脑用了多久，单位毫秒。', '大腦用了多久，單位毫秒。') });
  const L0 = '"l0 v8-2 clone"';

  const LINES: Line[] = [
    {
      name: w('combat', '战斗', '戰鬥'),
      fields: [
        RUN,
        ID(4),
        ENV,
        { key: 'surface', value: '"combat"', note: w('The floor-2 fight, SLIMES_WEAK: its first decision.', '第 2 层的战斗 SLIMES_WEAK：它的第一个决策。', '第 2 層的戰鬥 SLIMES_WEAK：它的第一個決策。') },
        { key: 'brain', value: '"plan c8-2"', note: w('The SL planner with the combat net c8-2 in its leaves.', 'SL 规划器，叶节点用战斗网络 c8-2。', 'SL 規劃器，葉節點用戰鬥網路 c8-2。') },
        HASH(w('The hash of the combat observation the brain was shown. Not the wire’s obs_hash, which hashes the whole observation.', '大脑看到的战斗观测的哈希。不是线路上的 obs_hash，那个是整个观测的哈希。', '大腦看到的戰鬥觀測的雜湊。不是線路上的 obs_hash，那個是整個觀測的雜湊。')),
        { key: 'legal', value: '7', note: w('How many legal actions there were.', '合法动作有几个。', '合法動作有幾個。') },
        { key: 'chosen', value: '"play:3:1"', note: w('The action in text form: play the card with uid 3 at the creature with uid 1.', '文本形式的动作：把 uid 为 3 的牌打向 uid 为 1 的生物。', '文字形式的動作：把 uid 為 3 的牌打向 uid 為 1 的生物。') },
        { key: 'plan', value: '["play:5:1", "end_turn", "play:9", "…"]', note: w('What the brain plans after it. For the SL planner, the rest of the combat.', '大脑在它之后的计划。SL 规划器的计划是整场战斗剩下的部分。', '大腦在它之後的計畫。SL 規劃器的計畫是整場戰鬥剩下的部分。') },
        { key: 'candidates', value: '[{"action": "play:3:1", "value": 0.912, "ci": 0, "samples": 1, "values": [0.912]}, {"action": "play:4", "value": 0.905, …}, …]', note: w('Each first action the planner’s kept lines began with, valued by the best of them; the pick first. One world, so ci 0 and one sample.', '规划器保留的各条打法的每个第一步，用其中最好的一条估值；选中的排第一。只有一个世界，所以 ci 为 0、样本为 1。', '規劃器保留的各條打法的每個第一步，用其中最好的一條估值；選中的排第一。只有一個世界，所以 ci 為 0、樣本為 1。') },
        { key: 'budget', value: '{"worlds": 8, "nodes_per_world": 300, "elite": 1, "boss": 1, "halving": false, "follow": false, "beam": 8, "replan": 1, "loss_plans": 3, "easy_beam": 4}', note: w('The route’s budget as configured (the low effort), before the room’s multiple.', '路由配置的预算（low 力度），还没乘上房间的倍数。', '路由設定的預算（low 力度），還沒乘上房間的倍數。') },
        SEED('6737716408865394762'),
        MS('812.4'),
        { key: 'obs', value: '{"encounter": "SLIMES_WEAK", "room": "Monster", "act": 0, "ascension": 0, "round": 1, "player": {…}, "hand": […], …}', note: w('The combat observation the brain was shown, about 1.6 KB: the input of the learned combat parts.', '大脑看到的战斗观测，约 1.6 KB：学到的战斗部件的输入。', '大腦看到的戰鬥觀測，約 1.6 KB：學到的戰鬥部件的輸入。') },
        { key: 'history', value: '[{"uid": 1, "id": "…", "turns": [{"round": 1, "intents": […]}]}, …]', note: w('The record of enemy moves so far, this round included.', '到目前为止的敌人招式记录，包括这一回合。', '到目前為止的敵人招式紀錄，包括這一回合。') },
      ],
    },
    {
      name: w('card prompt', '卡牌提示', '卡牌提示'),
      fields: [
        RUN,
        ID(1),
        ENV,
        { key: 'surface', value: '"card_choice"', note: w('The prompt Neow’s option opened, on floor 1. Asked outside combat, so the macro side answers.', '涅奥的选项在第 1 层打开的提示。不在战斗中，所以由宏观那一侧回答。', '涅奧的選項在第 1 層打開的提示。不在戰鬥中，所以由宏觀那一側回答。') },
        { key: 'brain', value: L0, note: w('L0 answers the card prompts outside combat too.', '战斗外的卡牌提示也由 L0 回答。', '戰鬥外的卡牌提示也由 L0 回答。') },
        HASH(w('The hash of the card prompt itself.', '卡牌提示本身的哈希。', '卡牌提示本身的雜湊。')),
        { key: 'legal', value: '5', note: w('The prompt’s options.', '提示的选项数。', '提示的選項數。') },
        { key: 'chosen', value: '"2"', note: w('The picked options’ indexes, joined by commas, without "pick:".', '挑中的选项下标，用逗号连接，不带 "pick:"。', '挑中的選項索引，用逗號連接，不帶 "pick:"。') },
        SEED('10674926705250088611'),
        MS('95.1'),
      ],
    },
    {
      name: w('macro', '宏观', '宏觀'),
      fields: [
        RUN,
        ID(378),
        ENV,
        { key: 'surface', value: '"rest"', note: w('A rest site on floor 25, at 38 of 70 HP.', '第 25 层的休息处，HP 38/70。', '第 25 層的休息處，HP 38/70。') },
        { key: 'brain', value: L0, note: w('L0 with V v8-2, each option played on the node’s clone.', 'L0 用 V v8-2，每个选项都在节点克隆上打一遍。', 'L0 用 V v8-2，每個選項都在節點複本上打一遍。') },
        HASH(w('The hash of the whole observation: here the same as the wire’s obs_hash.', '整个观测的哈希：这里与线路上的 obs_hash 相同。', '整個觀測的雜湊：這裡與線路上的 obs_hash 相同。')),
        { key: 'legal', value: '2', note: w('rest:HEAL and rest:SMITH; no potions, so no belt actions.', 'rest:HEAL 和 rest:SMITH；没有药水，所以没有药水栏动作。', 'rest:HEAL 和 rest:SMITH；沒有藥水，所以沒有藥水欄動作。') },
        { key: 'chosen', value: '"rest:HEAL"', note: w('The run’s V valued healing a little above smithing (42.62 against 42.20 floors).', '这一局的 V 认为休息略好于锻造（42.62 对 42.20 层）。', '這一局的 V 認為休息略好於鍛造（42.62 對 42.20 層）。') },
        SEED('10733443197887323299'),
        MS('1530.7'),
        { key: 'run_view', value: '{"character": "SILENT", "ascension": 0, "act": 1, "act_id": "HIVE", "floor": 25, "hp": 38, "max_hp": 70, "gold": 36, "deck": […], "relics": […], "potions": [null, null, null], "map": {…}}', note: w('The run view the brain was shown, about 6 KB.', '大脑看到的对局视图，约 6 KB。', '大腦看到的對局視圖，約 6 KB。') },
      ],
    },
  ];

  let tab = $state(0);
  let field = $state(0);
  const line = $derived(LINES[tab]);
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.75rem;">
    {#each LINES as l, k (k)}
      <button aria-pressed={tab === k} onclick={() => ((tab = k), (field = 0))}>{say(lang, l.name)}</button>
    {/each}
  </div>
  <div class="line">
    <span>{'{'}</span>
    {#each line.fields as f, k (k)}
      <button class="field" class:now={k === field} onclick={() => (field = k)} onmouseenter={() => (field = k)}>
        <span class="key">"{f.key}"</span>: <span class="val">{f.value}</span>{k < line.fields.length - 1 ? ',' : ''}
      </button>
    {/each}
    <span>{'}'}</span>
  </div>
  <p class="v-ink2" style="margin: 0.5rem 0 0;"><code>{line.fields[field].key}</code>: {say(lang, line.fields[field].note)}</p>
  <p class="v-caption">
    {say(lang, w(
      'One line per decision, as Harness writes it, from the book’s example run, which an earlier V and combat net played; the brain names show the current nets. Real: the decision numbers, surfaces, the rest site’s answer and run view, and the seeds, computed with Harness’s formula. Hashes, times, the combat’s actions and values and the prompt’s pick are abbreviated (…) or made up.',
      '每个决策一行，按 Harness 写出的样子，取自贯穿全书的那一局，它由较早的 V 和战斗网络打出；大脑名称用的是现在的网络。真实的有：决策编号、决策界面、休息处的回答和对局视图，以及按 Harness 公式算出的种子。哈希、耗时、战斗的动作与价值、提示的挑选是省略的（…）或编造的。',
      '每個決策一行，按 Harness 寫出的樣子，取自貫穿全書的那一局，它由較早的 V 和戰鬥網路打出；大腦名稱用的是現在的網路。真實的有：決策編號、決策介面、休息處的回答和對局視圖，以及按 Harness 公式算出的種子。雜湊、耗時、戰鬥的動作與價值、提示的挑選是省略的（…）或編造的。',
    ))}
  </p>
</div>

<style>
  .line {
    font-family: var(--sl-font-mono, monospace);
    font-size: 0.72rem;
    line-height: 1.5;
    display: flex;
    flex-direction: column;
    max-height: 24rem;
    overflow-y: auto;
  }
  .field {
    all: unset;
    cursor: pointer;
    display: block;
    padding: 0.05rem 0.4rem 0.05rem 1rem;
    border-left: 2px solid transparent;
    white-space: pre-wrap;
    word-break: break-all;
  }
  .field.now {
    border-left-color: var(--v-s1);
    background: var(--v-wash);
  }
  .key {
    color: var(--v-s1);
  }
  .val {
    color: var(--v-ink-2);
  }
</style>
