<script lang="ts">
  // One real run through net.Net, step by step: v9-1's tests[0], a Defect run at A0 at a map move (floor 42), values as
  // the trainer gave them. Vectors show their first four numbers (docs: macro-brain/network §9).
  import type { Lang } from './names.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  type W = { en: string; 'zh-CN': string; 'zh-TW': string };
  const w = (en: string, cn: string, tw: string): W => ({ en, 'zh-CN': cn, 'zh-TW': tw });
  const STEPS: { title: W; text: W; rows: string[][] }[] = [
    {
      title: w('The run', '这一局', '這一局'),
      text: w(
        'The afterstate of a map move: an unknown room is about to be entered. Nothing else has changed.',
        '一次选路的 afterstate：正要进入一个未知房间，其他都没变。',
        '一次選路的 afterstate：正要進入一個未知房間，其他都沒變。',
      ),
      rows: [['character', 'DEFECT'], ['floor', '42'], ['HP', '46 / 65'], ['gold', '78'], ['deck', '22'], ['relics', '7'], ['potions', '0 / 3 slots']],
    },
    {
      title: w('Tokens', '词元', '詞元'),
      text: w(
        'Ids, and three numbers a card can grow, its enchantment’s and affliction’s amounts, damage and block, each read through rows of its own; each relic’s counter, melted flag and status; a card’s cost is gone. No card here has an enchantment or affliction. The map is 10 tokens; repeats stay.',
        'id，加上牌会成长的三个数字：附魔和诅咒的数值、伤害和格挡，各由自己的那一行读取；每件遗物的计数、熔化标记和状态；牌的费用不读。这里没有牌带附魔或诅咒。地图有 10 个词元，重复的也保留。',
        'id，加上牌會成長的三個數字：附魔和詛咒的數值、傷害和格擋，各由自己的那一列讀取；每件遺物的計數、熔化標記和狀態；牌的費用不讀。這裡沒有牌帶附魔或詛咒。地圖有 10 個詞元，重複的也保留。',
      ),
      rows: [
        ['cards', 'DEFEND_DEFECT+ ×2, NULL+ ×2, UPROAR ×2, BEAM_CELL+, BULK_UP, CHARGE_BATTERY, CHILL+, CLAW, CLUMSY (Ethereal, Unplayable), DARKNESS+, DEFEND_DEFECT, DUALCAST, FINESSE+, FOLLY (Eternal, Ethereal, Innate, Unplayable), MOMENTUM_STRIKE, MOMENTUM_STRIKE+, NULL, STRIKE_DEFECT, SUPERCRITICAL (Exhaust)'],
        ['damage ÷ 20', 'BEAM_CELL+ 0.20, CLAW 0.15, MOMENTUM_STRIKE 0.55, MOMENTUM_STRIKE+ 0.75, NULL 0.50, NULL+ 0.65 ×2, STRIKE_DEFECT 0.30, UPROAR 0.30 ×2'],
        ['block ÷ 20', 'CHARGE_BATTERY 0.35, DEFEND_DEFECT 0.25, DEFEND_DEFECT+ 0.40 ×2, FINESSE+ 0.35'],
        ['relics', 'CRACKED_CORE, FISHING_ROD, RED_MASK, MINIATURE_CANNON, VERY_HOT_COCOA, GORGET, PRESERVED_FOG'],
        ['relic counter ÷ 10', 'FISHING_ROD 0.20; every status Normal, none melted'],
        ['kept cards', '–'],
        ['potions', '–'],
        ['context', 'character:DEFECT, act:GLORY, boss:AEONGLASS_BOSS, ancient:VAKUU'],
        ['map', 'Unknown+0, Elite+1, Monster+2, Monster+2, Elite+3, Unknown+3, Monster+3, RestSite+4, RestSite+4, Boss+5'],
      ],
    },
    {
      title: w('Table entries', '表项', '表項'),
      text: w('Each token becomes its row in its table.', '每个词元换成它在表里的行号。', '每個詞元換成它在表裡的行號。'),
      rows: [
        ['DEFEND_DEFECT', '146'], ['STRIKE_DEFECT', '514'], ['MOMENTUM_STRIKE', '346'], ['keyword:Ethereal', '32'], ['CRACKED_CORE', '53'], ['character:DEFECT', '27'], ['Unknown+0', '337'], ['Elite+1', '87'],
      ],
    },
    {
      title: w('Card vectors', '牌向量', '牌向量'),
      text: w(
        'x = E + u·U + d·D + k·K + Σ (M + a·A), 64 numbers; each card adds the terms it has. A keyword has no amount, so CLUMSY adds its two keywords’ M alone.',
        'x = E + u·U + d·D + k·K + Σ (M + a·A)，64 个数；每张牌只加上它有的项。关键词没有数值，所以 CLUMSY 只加上它两个关键词的 M。',
        'x = E + u·U + d·D + k·K + Σ (M + a·A)，64 個數；每張牌只加上它有的項。關鍵字沒有數值，所以 CLUMSY 只加上它兩個關鍵字的 M。',
      ),
      rows: [
        ['STRIKE_DEFECT', 'E + 0.30·D = [−0.015, −0.013, 0.039, 0.016, …]'],
        ['DEFEND_DEFECT', 'E + 0.25·K = [−0.011, −0.032, −0.006, 0.016, …]'],
        ['DEFEND_DEFECT+', 'E + U + 0.40·K = [0.073, −0.187, 0.011, −0.020, …]'],
        ['MOMENTUM_STRIKE+', 'E + U + 0.75·D = [−0.338, 0.381, 0.035, 0.213, …]'],
        ['CLUMSY', 'E + M[keyword:Ethereal] + M[keyword:Unplayable] = [−0.127, −0.032, 0.068, 0.120, …]'],
      ],
    },
    {
      title: w('φ, card by card', 'φ，逐张计算', 'φ，逐張計算'),
      text: w('The same two layers for each card, 256 numbers, none below 0. Equal cards give equal vectors; no card sees another. The length is the vector’s norm.', '每张牌用同样的两层，256 个数，都不小于 0。相同的牌得到相同的向量，牌与牌互相看不到。长度是向量的范数。', '每張牌用同樣的兩層，256 個數，都不小於 0。相同的牌得到相同的向量，牌與牌互相看不到。長度是向量的範數。'),
      rows: [
        ['STRIKE_DEFECT', '[0.165, 0.337, 0.372, 0.157, …] |3.21|'], ['DEFEND_DEFECT', '[0.153, 0.239, 0.283, 0.096, …] |2.80|'], ['DEFEND_DEFECT+', '[0.222, 0.297, 0.231, 0.092, …] |3.47|'], ['MOMENTUM_STRIKE+', '[0.648, 1.069, 1.259, 0.817, …] |8.07|'], ['CLUMSY', '[0.143, 0.216, 0.273, 0.148, …] |3.35|'],
      ],
    },
    {
      title: w('Pooling the deck', '汇总牌组', '匯聚牌組'),
      text: w('All 22 vectors added up, then divided by 22 for the mean and by 16 for the scaled sum. The 16 is a fixed factor in the code that keeps the sum about a mean’s size; the two differ by 22/16, which is how the body can read the deck’s size.', '22 个向量全部相加，再除以 22 得到平均，除以 16 得到缩放后的和。16 是代码里固定的因子，让和保持在平均值的量级；两者相差 22/16 倍，这就是主干读到牌数的方式。', '22 個向量全部相加，再除以 22 得到平均，除以 16 得到縮放後的和。16 是程式碼裡固定的係數，讓和大約和平均值一樣大；兩者相差 22/16 倍，這就是主幹讀到牌數的方式。'),
      rows: [['mean', '[0.418, 0.453, 0.530, 0.390, …]'], ['sum ÷ 16', '[0.574, 0.623, 0.728, 0.536, …]']],
    },
    {
      title: w('The bags', '四个袋', '四個袋'),
      text: w('Each bag’s embeddings added up, 64 numbers each, with no layer in between; a relic’s with its counter’s, melted and status rows. No potion: the zero vector.', '每个袋的嵌入直接相加，各 64 个数，中间没有任何一层；遗物的还加上它的计数、熔化和状态那几行。没有药水，就是零向量。', '每個袋的嵌入直接相加，各 64 個數，中間沒有任何一層；遺物的還加上它的計數、熔化和狀態那幾列。沒有藥水，就是零向量。'),
      rows: [['relics', '[−0.261, 0.347, 0.341, 0.303, …] |2.30|'], ['potions', '[0, 0, 0, 0, …] |0|'], ['context', '[0.172, 0.260, −1.734, 0.047, …] |4.42|'], ['map', '[1.431, −0.289, −0.830, −1.145, …] |4.65|']],
    },
    {
      title: w('Six numbers', '六个数字', '六個數字'),
      text: w('(x − μ)/σ, by the net file’s μ and σ, kept from the nets it went on from. Ascension has μ 0 and σ read as 1, since the first of those nets’ training runs were all A0: this A0 run reads 0, and A10 would read 10.', '(x − μ)/σ，用网络文件里的 μ 和 σ，它们沿用自它接着训练的那些网络。其中最早那个的训练局都是 A0，所以进阶的 μ 是 0、σ 读作 1：这一局是 A0，读作 0，A10 则会读作 10。', '(x − μ)/σ，用網路檔案裡的 μ 和 σ，它們沿用自它接著訓練的那些網路。其中最早那個的訓練局都是 A0，所以晉階的 μ 是 0、σ 讀作 1：這一局是 A0，讀作 0，A10 則會讀作 10。'),
      rows: [['floor', '42 → 3.016'], ['HP', '46 → −0.983'], ['max HP', '65 → −1.132'], ['gold', '78 → −0.697'], ['ascension', '0 → 0'], ['potion slots', '3 → −0.229']],
    },
    {
      title: w('Join and body', '拼接与主干', '串接與主幹'),
      text: w('512 + 4 × 64 + 6 + 256 = 1,030 numbers, the last 256 the cards the relics keep, none here: zeros. Then two ReLU layers. Here 356 of the first layer’s 512 numbers and 274 of the second’s are above 0.', '512 + 4 × 64 + 6 + 256 = 1,030 个数，最后 256 个是遗物保留的牌，这里没有，全是零。再经过两层 ReLU。这里第一层 512 个数有 356 个大于 0，第二层有 274 个。', '512 + 4 × 64 + 6 + 256 = 1,030 個數，最後 256 個是遺物保留的牌，這裡沒有，全是零。再經過兩層 ReLU。這裡第一層 512 個數有 356 個大於 0，第二層有 274 個。'),
      rows: [['z', '1,030'], ['b₁', '512 (356 > 0)'], ['b₂', '512 (274 > 0)']],
    },
    {
      title: w('Hazards and the value', '风险值与价值', '風險值與價值'),
      text: w('One chance per floor. Floor 48 is the third act’s boss. The product of every (1 − h) from floor 42 on is the value.', '每层一个概率。第 48 层是第三幕的 Boss。从第 42 层起所有 (1 − h) 的连乘就是价值。', '每層一個機率。第 48 層是第三幕的頭目。從第 42 層起所有 (1 − h) 的連乘就是價值。'),
      rows: [['h₄₂ … h₄₇', '0.0537, 0.0596, 0.1202, 0.2785, 0.3748, 0.0000'], ['h₄₈', '0.9933'], ['h₄₉ … h₆₃', '0.0000, 0.0022, 0.0000 ×13'], ['P(win)', '0.00235']],
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
