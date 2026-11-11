<script lang="ts">
  // One real run through net.Net, step by step: the champion's V file's tests[0], a Defect run at A0 at a map move
  // (floor 24), values as the trainer gave them. Vectors show their first four numbers.
  import type { Lang } from './names.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  type W = { en: string; 'zh-CN': string; 'zh-TW': string };
  const w = (en: string, cn: string, tw: string): W => ({ en, 'zh-CN': cn, 'zh-TW': tw });
  const STEPS: { title: W; text: W; rows: string[][] }[] = [
    {
      title: w('The run', '这一局', '這一局'),
      text: w(
        'The position after a map move: an unknown room is about to be entered. Nothing else has changed.',
        '一次选路之后的局面：正要进入一个未知房间，其他都没变。',
        '一次選路之後的局面：正要進入一個未知房間，其他都沒變。',
      ),
      rows: [['character', 'DEFECT'], ['floor', '24'], ['HP', '50 / 75'], ['gold', '532'], ['deck', '22'], ['relics', '6'], ['potions', '0 / 3 slots']],
    },
    {
      title: w('Tags', '标记', '標記'),
      text: w(
        'Ids, and three numbers a card can grow, its enchantment’s and affliction’s amounts, damage and block, each read through rows of its own; each relic’s counter, melted flag and status; a card’s cost is gone. No card here has an enchantment or affliction. The map is 12 tags, one per point; two points of one type at one distance would give the same tag twice.',
        'id，加上牌会成长的三个数字：附魔和诅咒的数值、伤害和格挡，各由自己的那一行读取；每件遗物的计数、熔化标记和状态；牌的费用不读。这里没有牌带附魔或诅咒。地图有 12 个标记，每个点一个；同类型、同距离的两个点会给出两个相同的标记。',
        'id，加上牌會成長的三個數字：附魔和詛咒的數值、傷害和格擋，各由自己的那一列讀取；每件遺物的計數、熔化標記和狀態；牌的費用不讀。這裡沒有牌帶附魔或詛咒。地圖有 12 個標記，每個點一個；同類型、同距離的兩個點會給出兩個相同的標記。',
      ),
      rows: [
        ['cards', 'DEFEND_DEFECT ×3, STRIKE_DEFECT ×3, BOOT_SEQUENCE (Exhaust, Innate), BULK_UP, CHARGE_BATTERY, COOLHEADED, DARKNESS, DEFEND_DEFECT+, DEFRAGMENT, FIGHT_THROUGH, FLAK_CANNON, LIGHTNING_ROD, NULL, PECK, QUADCAST, SHATTER (Exhaust), UPROAR+, ZAP'],
        ['damage ÷ 20', 'FLAK_CANNON 0.40, NULL 0.50, PECK 0.10, SHATTER 0.35, STRIKE_DEFECT 0.30 ×3, UPROAR+ 0.40'],
        ['block ÷ 20', 'BOOT_SEQUENCE 0.50, CHARGE_BATTERY 0.35, DEFEND_DEFECT 0.25 ×3, DEFEND_DEFECT+ 0.40, FIGHT_THROUGH 0.65, LIGHTNING_ROD 0.20'],
        ['relics', 'CRACKED_CORE, LAVA_ROCK, VEXING_PUZZLEBOX, CENTENNIAL_PUZZLE, TUNING_FORK, ARCHAIC_TOOTH'],
        ['relic counter ÷ 10', 'TUNING_FORK 0.00; LAVA_ROCK Disabled, every other status Normal, none melted'],
        ['kept cards', '–'],
        ['potions', '–'],
        ['context', 'character:DEFECT, act:HIVE, boss:KNOWLEDGE_DEMON_BOSS, ancient:OROBAS'],
        ['map', 'Unknown+0, Treasure+1, Monster+2, Shop+2, Elite+3, RestSite+3, RestSite+4, Shop+4, Elite+5, Shop+6, RestSite+7, Boss+8'],
      ],
    },
    {
      title: w('Table lookup', '查表', '查表'),
      text: w('Each tag becomes its row in its table.', '每个标记换成它在表里的行号。', '每個標記換成它在表裡的行號。'),
      rows: [
        ['DEFEND_DEFECT', '146'], ['STRIKE_DEFECT', '514'], ['BOOT_SEQUENCE', '60'], ['keyword:Exhaust', '33'], ['CRACKED_CORE', '53'], ['character:DEFECT', '27'], ['Unknown+0', '337'], ['Treasure+1', '255'],
      ],
    },
    {
      title: w('Card vectors', '牌向量', '牌向量'),
      text: w(
        'x = E + u·U + d·D + k·K + Σ (M + a·A), 64 numbers; each card adds the terms it has. A keyword has no amount, so BOOT_SEQUENCE adds its two keywords’ M alone.',
        'x = E + u·U + d·D + k·K + Σ (M + a·A)，64 个数；每张牌只加上它有的项。关键词没有数值，所以 BOOT_SEQUENCE 只加上它两个关键词的 M。',
        'x = E + u·U + d·D + k·K + Σ (M + a·A)，64 個數；每張牌只加上它有的項。關鍵字沒有數值，所以 BOOT_SEQUENCE 只加上它兩個關鍵字的 M。',
      ),
      rows: [
        ['STRIKE_DEFECT', 'E + 0.30·D = [−0.003, −0.016, 0.045, 0.027, …]'],
        ['DEFEND_DEFECT', 'E + 0.25·K = [−0.017, −0.032, 0.001, 0.007, …]'],
        ['DEFEND_DEFECT+', 'E + U + 0.40·K = [0.041, −0.199, 0.026, −0.052, …]'],
        ['UPROAR+', 'E + U + 0.40·D = [−0.313, 0.038, 0.127, 0.112, …]'],
        ['BOOT_SEQUENCE', 'E + 0.50·K + M[keyword:Exhaust] + M[keyword:Innate] = [−0.147, 0.151, −0.120, −0.114, …]'],
      ],
    },
    {
      title: w('φ, card by card', 'φ，逐张计算', 'φ，逐張計算'),
      text: w('The same two layers for each card, 256 numbers, none below 0. Equal cards give equal vectors; no card sees another. The length is the vector’s norm.', '每张牌用同样的两层，256 个数，都不小于 0。相同的牌得到相同的向量，牌与牌互相看不到。长度是向量的范数。', '每張牌用同樣的兩層，256 個數，都不小於 0。相同的牌得到相同的向量，牌與牌互相看不到。長度是向量的範數。'),
      rows: [
        ['STRIKE_DEFECT', '[0.133, 0.373, 0.357, 0.160, …] |3.22|'], ['DEFEND_DEFECT', '[0.196, 0.218, 0.315, 0.136, …] |2.58|'], ['DEFEND_DEFECT+', '[0.340, 0.220, 0.328, 0.188, …] |3.10|'], ['UPROAR+', '[0.489, 0.220, 0.375, 0.260, …] |4.21|'], ['BOOT_SEQUENCE', '[0.400, 0.026, 0.510, 0.414, …] |3.61|'],
      ],
    },
    {
      title: w('Pooling the deck', '汇总牌组', '匯聚牌組'),
      text: w('All 22 vectors added up, then divided by 22 for the mean and by 16 for the scaled sum. The 16 is a fixed factor in the code that keeps the sum about a mean’s size; the two differ by 22/16, which is how the body can read the deck’s size.', '22 个向量全部相加，再除以 22 得到平均，除以 16 得到缩放后的和。16 是代码里固定的因子，让和保持在平均值的量级；两者相差 22/16 倍，这就是主干读到牌数的方式。', '22 個向量全部相加，再除以 22 得到平均，除以 16 得到縮放後的和。16 是程式碼裡固定的係數，讓和大約和平均值一樣大；兩者相差 22/16 倍，這就是主幹讀到牌數的方式。'),
      rows: [['mean', '[0.297, 0.360, 0.430, 0.276, …]'], ['sum ÷ 16', '[0.408, 0.495, 0.591, 0.379, …]']],
    },
    {
      title: w('Four groups added up', '四类加总', '四類加總'),
      text: w('The vectors each group looks up in its table are added up, 64 numbers each, with no layer in between; a relic’s with its counter’s, melted and status rows. No potion: the zero vector.', '每一类查表得到的向量直接相加，各 64 个数，中间没有任何一层；遗物的还加上它的计数、熔化和状态那几行。没有药水，就是零向量。', '每一類查表得到的向量直接相加，各 64 個數，中間沒有任何一層；遺物的還加上它的計數、熔化和狀態那幾列。沒有藥水，就是零向量。'),
      rows: [['relics', '[−0.158, −0.269, −0.288, −0.009, …] |2.25|'], ['potions', '[0, 0, 0, 0, …] |0|'], ['context', '[−0.108, −0.689, −0.022, −0.309, …] |2.70|'], ['map', '[1.066, 0.065, −1.150, −1.403, …] |4.62|']],
    },
    {
      title: w('Nine numbers', '九个数字', '九個數字'),
      text: w('(x − μ)/σ, by the net file’s μ and σ. Every training run was at A0, so ascension has μ 0 and σ read as 1: this A0 run reads 0, and A10 would read 10. The last three are the game’s rule, 0 in a whole run: whether each act wins its plain monster fights at their start.', '(x − μ)/σ，用网络文件里的 μ 和 σ。训练局全是 A0，所以进阶的 μ 是 0、σ 读作 1：这一局是 A0，读作 0，A10 则会读作 10。最后三个是游戏规则，完整的一局都是 0：每一幕的普通怪物战是否在开打时就算赢。', '(x − μ)/σ，用網路檔案裡的 μ 和 σ。訓練局全是 A0，所以晉階的 μ 是 0、σ 讀作 1：這一局是 A0，讀作 0，A10 則會讀作 10。最後三個是遊戲規則，完整的一局都是 0：每一幕的普通怪物戰是否在開打時就算贏。'),
      rows: [['floor', '24 → 1.109'], ['HP', '50 → −0.738'], ['max HP', '75 → 0.802'], ['gold', '532 → 2.716'], ['ascension', '0 → 0'], ['potion slots', '3 → −0.229'], ['auto-win, acts 1–3', '0 · 0 · 0 → 0 · 0 · 0']],
    },
    {
      title: w('Join and body', '拼接与主干', '串接與主幹'),
      text: w('512 + 4 × 64 + 9 + 256 = 1,033 numbers, the last 256 the cards the relics keep, none here: zeros. Then two ReLU layers. Here 372 of the first layer’s 512 numbers and 292 of the second’s are above 0.', '512 + 4 × 64 + 9 + 256 = 1,033 个数，最后 256 个是遗物保留的牌，这里没有，全是零。再经过两层 ReLU。这里第一层 512 个数有 372 个大于 0，第二层有 292 个。', '512 + 4 × 64 + 9 + 256 = 1,033 個數，最後 256 個是遺物保留的牌，這裡沒有，全是零。再經過兩層 ReLU。這裡第一層 512 個數有 372 個大於 0，第二層有 292 個。'),
      rows: [['z', '1,033'], ['b₁', '512 (372 > 0)'], ['b₂', '512 (292 > 0)']],
    },
    {
      title: w('Hazards and the value', '风险值与价值', '風險值與價值'),
      text: w('One chance per floor. Floors 33 and 48 are the second and third acts’ bosses. The product of every (1 − h) from floor 24 on is the value.', '每层一个概率。第 33 层和第 48 层是第二幕和第三幕的 Boss。从第 24 层起所有 (1 − h) 的连乘就是价值。', '每層一個機率。第 33 層和第 48 層是第二幕和第三幕的頭目。從第 24 層起所有 (1 − h) 的連乘就是價值。'),
      rows: [
        ['h₂₄ … h₃₂', '0.0002, 0.0004, 0.0000, 0.0038, 0.0046, 0.0036, 0.0091, 0.0084, 0.0000'],
        ['h₃₃', '0.4790'],
        ['h₃₄ … h₄₇', '0.0000, 0.0003, 0.0003, 0.0017, 0.0045, 0.0082, 0.0111, 0.0000, 0.0158, 0.0175, 0.0149, 0.0122, 0.0146, 0.0000'],
        ['h₄₈', '0.6242'],
        ['h₄₉ … h₆₃', '0.0000, 0.0003, 0.0000 ×13'],
        ['P(win)', '0.172'],
      ],
    },
  ];
  let k = $state(0);
  const step = $derived(STEPS[k]);
  const stepWord = { en: 'Step', 'zh-CN': '步骤', 'zh-TW': '步驟' }[lang];
</script>

<div class="vfig">
  <label class="v-row">
    <span>{stepWord} {k + 1} / {STEPS.length}: <strong>{step.title[lang]}</strong></span>
    <input type="range" min="0" max={STEPS.length - 1} bind:value={k} aria-label={stepWord} />
  </label>
  <div class="v-scroll">
    <table>
      <tbody>
        {#each step.rows as [a, b] (a)}
          <tr><th>{a}</th><td class="v-num">{b}</td></tr>
        {/each}
      </tbody>
    </table>
  </div>
  <p class="v-caption" style="min-height: 3.6em;">{step.text[lang]}</p>
</div>

<style>
  label { display: flex; flex-wrap: wrap; gap: 0.6rem; align-items: center; font-size: 0.875rem; margin-bottom: 0.5rem; }
  input { flex: 1 1 10rem; }
  .v-scroll { min-height: 13rem; }
  table { width: 100%; margin: 0; font-size: 0.8125rem; }
  th { font-weight: 600; text-align: left; white-space: nowrap; padding: 0.15rem 0.6rem 0.15rem 0; }
  td { padding: 0.15rem 0; }
</style>
