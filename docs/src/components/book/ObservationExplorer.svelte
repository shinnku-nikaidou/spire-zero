<script lang="ts">
  // A real CombatObservation, field by field (docs: protocol/observation §3): the start of a round from NoSL self-play
  // that the combat net c2's file carries as a test state (src/data/book/round-start.json). Clicking a field shows what
  // it holds and where the game shows it.
  import data from '../../data/book/round-start.json';
  import { loadNames, type Lang, type Names, type Kind } from '../v/names.ts';
  import { w, say, type Words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();

  let names = $state<Names | null>(null);
  $effect(() => {
    loadNames(lang).then((v) => (names = v));
  });

  type Json = null | boolean | number | string | Json[] | { [k: string]: Json };
  const obs = data.obs as unknown as { [k: string]: Json };

  // Field docs by path, array indexes as []; a card's fields are documented once, under card.
  const DOCS: Record<string, Words> = {
    encounter: w('The encounter’s id: which monsters, in which places, under the fight’s own rules.', '遭遇的 id：哪些怪物、站在哪里、这场战斗自己的规则。', '遭遇的 id：哪些怪物、站在哪裡、這場戰鬥自己的規則。'),
    room: w('The encounter’s room type as the map shows it: Monster, Elite or Boss.', '地图上显示的房间类型：Monster、Elite 或 Boss。', '地圖上顯示的房間類型：Monster、Elite 或 Boss。'),
    act: w('The act, counted from 0.', '第几幕，从 0 开始数。', '第幾幕，從 0 開始數。'),
    ascension: w('The run’s ascension level.', '这一局的进阶等级。', '這一局的晉階等級。'),
    round: w('The combat’s round, from 1.', '战斗的回合数，从 1 开始。', '戰鬥的回合數，從 1 開始。'),
    player: w('The player’s creature and counters: HP, block, energy, stars, gold, powers, orbs.', '玩家的生物与各种计数：生命、格挡、能量、星星、金币、能力、充能球。', '玩家的生物與各種計數：生命、格擋、能量、星星、金幣、能力、充能球。'),
    'player.uid': w('The player creature’s uid, its CombatId: what an action names as a target.', '玩家生物的 uid，也就是它的 CombatId：动作指定目标时用的名字。', '玩家生物的 uid，也就是它的 CombatId：動作指定目標時用的名字。'),
    'player.stars': w('The Regent’s stars; 0 for other characters.', '储君的星星；其他角色为 0。', '儲君的星星；其他角色為 0。'),
    'player.orb_slots': w('The Defect’s orb slots and orbs; 0 and empty for other characters.', '故障机器人的充能球槽位与充能球；其他角色为 0 和空。', '故障機器人的充能球欄位與充能球；其他角色為 0 和空。'),
    'power.id': w('A power, as its icon shows it.', '一个能力，如它的图标所示。', '一個能力，如它的圖示所示。'),
    'power.amount': w('The number on the power’s icon. When the icon shows another number than the power’s own amount, stacks gives the amount (Sloth’s icon counts this turn’s cards; its stacks are the limit).', '能力图标上的数字。图标显示的不是能力本身的数值时，stacks 给出那个数值（懒惰的图标数的是本回合打出的牌，stacks 才是上限）。', '能力圖示上的數字。圖示顯示的不是能力本身的數值時，stacks 給出那個數值（怠惰的圖示數的是本回合打出的牌，stacks 才是上限）。'),
    hand: w('The cards in hand, in hand order, each with its uid: the only cards an action can name.', '手牌，按手牌顺序，每张带 uid：动作只能指名这些牌。', '手牌，按手牌順序，每張帶 uid：動作只能指名這些牌。'),
    draw: w('The draw pile, sorted by card key: the player knows what is in it, not its order, so it is sent as a sorted multiset.', '抽牌堆，按卡牌键排序：玩家知道里面有什么，不知道顺序，所以以排好序的多重集合发送。', '抽牌堆，按卡牌鍵排序：玩家知道裡面有什麼，不知道順序，所以以排好序的多重集合傳送。'),
    discard: w('The discard pile, in pile order (the player saw the cards go there).', '弃牌堆，按牌堆顺序（玩家看着这些牌进去）。', '棄牌堆，按牌堆順序（玩家看著這些牌進去）。'),
    exhaust: w('The exhaust pile, in pile order.', '消耗堆，按牌堆顺序。', '消耗堆，按牌堆順序。'),
    'card.id': w('The card’s model id.', '卡牌的模型 id。', '卡牌的模型 id。'),
    'card.uid': w('Only in hand: the card’s id in the game’s per-combat card database (NetCombatCardDb). Cards in the piles have none: the game numbers cards right after the opening shuffle, so an id would give away the shuffle.', '只有手牌有：卡牌在游戏每场战斗的卡牌数据库（NetCombatCardDb）里的 id。牌堆里的牌没有：游戏在开局洗牌后才给牌编号，id 会泄露洗牌结果。', '只有手牌有：卡牌在遊戲每場戰鬥的卡牌資料庫（NetCombatCardDb）裡的 id。牌堆裡的牌沒有：遊戲在開局洗牌後才給牌編號，id 會洩漏洗牌結果。'),
    'card.up': w('The upgrade level, when upgraded.', '升级等级（有升级时）。', '升級等級（有升級時）。'),
    'card.cost': w('The energy cost with every modifier; absent and x_cost true for an X-cost card.', '计入所有修正后的能量费用；X 费卡没有它，改为 x_cost 为 true。', '計入所有修正後的能量費用；X 費卡沒有它，改為 x_cost 為 true。'),
    'card.enchant': w('An enchantment on the card and its amount, as the card’s frame shows it.', '卡牌上的附魔及其数值，如卡框所示。', '卡牌上的附魔及其數值，如卡框所示。'),
    'card.keywords': w('The card’s keywords, sorted.', '卡牌的关键字，已排序。', '卡牌的關鍵字，已排序。'),
    'card.vars': w('The card’s own numbers by name, sorted: base values, not what Strength or Dexterity make of them; a calculated number appears as its parts.', '卡牌自己的数字，按名称排序：是基础值，不是力量或敏捷改过之后的值；计算出来的数字以它的组成部分出现。', '卡牌自己的數字，按名稱排序：是基礎值，不是力量或敏捷改過之後的值；計算出來的數字以它的組成部分出現。'),
    potions: w('One entry per potion slot: the potion’s id, or null for an empty slot.', '每个药水槽一项：药水的 id，空槽为 null。', '每個藥水欄一項：藥水的 id，空欄為 null。'),
    relics: w('The relics, each with what its icon and tip show: a counter, a status other than normal, melted or used up, the cards it keeps.', '遗物，各带图标与提示显示的内容：计数器、非正常的状态、熔化或用尽、它保存的卡牌。', '遺物，各帶圖示與提示顯示的內容：計數器、非正常的狀態、熔化或用盡、它保存的卡牌。'),
    'relics[].id': w('The relic’s id. A relic with nothing to show has nothing else.', '遗物的 id。没有东西可显示的遗物就只有它。', '遺物的 id。沒有東西可顯示的遺物就只有它。'),
    allies: w('Allies other than the player: other players, summons.', '玩家以外的友方：其他玩家、召唤物。', '玩家以外的友方：其他玩家、召喚物。'),
    enemies: w('The enemies, each with its uid, HP, block, powers and intents.', '敌人，各带 uid、生命、格挡、能力与意图。', '敵人，各帶 uid、生命、格擋、能力與意圖。'),
    'enemies[].uid': w('The enemy’s CombatId, which an attack names as its target.', '敌人的 CombatId，攻击指定目标时用它。', '敵人的 CombatId，攻擊指定目標時用它。'),
    'enemies[].id': w('The monster’s id.', '怪物的 id。', '怪物的 id。'),
    'enemies[].intents': w('The icons above a living enemy: each intent’s type and, for an attack, the damage per hit against the player and the hits. Never which move is queued: two moves can show the same icons.', '活着的敌人头上的图标：每个意图的类型，攻击则带对玩家的单次伤害与次数。从不包括排好的是哪一招：两招可以显示同样的图标。', '活著的敵人頭上的圖示：每個意圖的類型，攻擊則帶對玩家的單次傷害與次數。從不包括排好的是哪一招：兩招可以顯示同樣的圖示。'),
  };
  function docKey(path: string): string {
    const p = path.replace(/\[\d+\]/g, '[]');
    const card = p.match(/^(hand|draw|discard|exhaust)\[\]\.(\w+)/);
    if (card) return `card.${card[2]}`;
    const power = p.match(/\.powers\[\]\.(\w+)$/);
    if (power) return `power.${power[1]}`;
    if (DOCS[p]) return p;
    const parent = p.replace(/\.[^.]+$|\[\]$/, '');
    return DOCS[parent] ? parent : p.split(/[.[]/)[0];
  }
  let selected = $state('draw');
  let open = $state<Record<string, boolean>>({ player: true, hand: false, draw: true, enemies: true, 'enemies[0]': true, 'draw[0]': true });

  const KIND: Record<string, Kind> = { encounter: 'encounter', potions: 'potion', relics: 'relic', enemies: 'monster' };
  function gloss(path: string, v: Json): string | null {
    if (!names || typeof v !== 'string') return null;
    if (/\.id$/.test(path) && /^(hand|draw|discard|exhaust)\[/.test(path)) return names.name('card', v);
    if (/powers\[\d+\]\.id$/.test(path)) return names.name('power', v);
    if (/^relics\[\d+\]\.id$/.test(path)) return names.name('relic', v);
    if (/^enemies\[\d+\]\.id$/.test(path)) return names.name('monster', v);
    if (/^potions\[\d+\]$/.test(path)) return names.name('potion', v);
    if (path === 'encounter') return names.name('encounter', v);
    if (path === 'player.character') return names.name('character', v);
    return null;
  }
  const isObj = (v: Json): v is { [k: string]: Json } => v !== null && typeof v === 'object' && !Array.isArray(v);
  const summary = (v: Json) => (Array.isArray(v) ? `[${v.length}]` : isObj(v) ? `{${Object.keys(v).length}}` : '');
</script>

{#snippet node(key: string, path: string, v: Json, depth: number)}
  <div class="row" style:padding-left="{depth * 1.1}rem">
    {#if Array.isArray(v) || isObj(v)}
      <button class="tog" aria-expanded={!!open[path]} onclick={() => (open[path] = !open[path])}>{open[path] ? '▾' : '▸'}</button>
    {:else}
      <span class="tog"></span>
    {/if}
    <button class="key" class:sel={docKey(path) === docKey(selected) && path === selected} onclick={() => (selected = path)}>{key}</button>
    {#if Array.isArray(v) || isObj(v)}
      <span class="v-muted">{summary(v)}</span>
    {:else}
      <span class="val">{JSON.stringify(v)}</span>
      {#if gloss(path, v)}<span class="v-muted">· {gloss(path, v)}</span>{/if}
    {/if}
  </div>
  {#if open[path]}
    {#if Array.isArray(v)}
      {#each v as item, i (i)}{@render node(String(i), `${path}[${i}]`, item, depth + 1)}{/each}
    {:else if isObj(v)}
      {#each Object.entries(v) as [k, item] (k)}{@render node(k, `${path}.${k}`, item, depth + 1)}{/each}
    {/if}
  {/if}
{/snippet}

<div class="vfig">
  <div class="grid">
    <div class="tree v-scroll">
      {#each Object.entries(obs) as [k, v] (k)}{@render node(k, k, v, 0)}{/each}
    </div>
    <div class="doc">
      <div class="v-muted"><code>{selected}</code></div>
      <p style="margin: 0.4rem 0 0;">{DOCS[docKey(selected)] ? say(lang, DOCS[docKey(selected)]) : say(lang, w('Click a field name to see what it holds.', '点一个字段名，看它装的是什么。', '點一個欄位名稱，看它裝的是什麼。'))}</p>
    </div>
  </div>
  <p class="v-caption">
    {say(lang, w(
      'A real start of a round from NoSL self-play, one of the test states the combat net c2’s file carries (weights/silent/c2.json), as Protocol serializes it: snake_case names, null fields left out. Names after a · are the game’s own, added here for reading; the observation sends ids only.',
      '一个真实的回合开始，来自 NoSL 自我对弈，是战斗网络 c2 的文件附带的测试状态之一（weights/silent/c2.json），按 Protocol 的序列化：snake_case 名称，省略 null 字段。· 后面的名字取自游戏本身，是为了方便阅读加上的；观测只发送 id。',
      '一個真實的回合開始，來自 NoSL 自我對弈，是戰鬥網路 c2 的檔案附帶的測試狀態之一（weights/silent/c2.json），按 Protocol 的序列化：snake_case 名稱，省略 null 欄位。· 後面的名字取自遊戲本身，是為了方便閱讀加上的；觀測只傳送 id。',
    ))}
  </p>
</div>

<style>
  .grid {
    display: grid;
    grid-template-columns: minmax(0, 3fr) minmax(12rem, 2fr);
    gap: 1rem;
    align-items: start;
  }
  .tree {
    max-height: 26rem;
    overflow-y: auto;
    font-family: var(--sl-font-mono, monospace);
    font-size: 0.75rem;
    line-height: 1.5;
  }
  .row {
    display: flex;
    gap: 0.3rem;
    align-items: baseline;
    white-space: nowrap;
  }
  .tog {
    width: 1rem;
    border: 0;
    background: none;
    padding: 0;
    color: var(--v-ink-2);
    cursor: pointer;
  }
  .key {
    border: 0;
    background: none;
    padding: 0 0.15rem;
    color: var(--v-s1);
    cursor: pointer;
    font: inherit;
    border-radius: 0.2rem;
  }
  .key.sel {
    background: var(--v-wash);
    outline: 1px solid var(--v-s1);
  }
  .val {
    color: var(--v-ink);
  }
  .doc {
    position: sticky;
    top: 4rem;
    border-left: 2px solid var(--v-s1);
    padding-left: 0.75rem;
  }
  @media (max-width: 40rem) {
    .grid {
      grid-template-columns: 1fr;
    }
    .doc {
      position: static;
    }
  }
</style>
