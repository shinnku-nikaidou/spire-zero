<script lang="ts">
  // One real run through net.Net, step by step: v9-0's test run at a map move (floor 41), values as the trainer gave
  // them. Vectors show their first four numbers (docs: macro-brain/network §9).
  import type { Lang } from './names.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  type W = { en: string; 'zh-CN': string; 'zh-TW': string };
  const w = (en: string, cn: string, tw: string): W => ({ en, 'zh-CN': cn, 'zh-TW': tw });
  const STEPS: { title: W; text: W; rows: string[][] }[] = [
    {
      title: w('The run', '这一局', '這一局'),
      text: w(
        'The afterstate of a map move: a rest site is about to be entered. Nothing else has changed.',
        '一次选路的 afterstate：正要进入一个休息处，其他都没变。',
        '一次選路的 afterstate：正要進入一個休息處，其他都沒變。',
      ),
      rows: [['floor', '41'], ['HP', '75 / 85'], ['gold', '53'], ['deck', '25'], ['relics', '10'], ['potions', '0 / 2 slots']],
    },
    {
      title: w('Tokens', '词元', '詞元'),
      text: w(
        'Ids only: a card’s cost and numbers are gone. The map is 10 tokens; repeats stay.',
        '只留下 id：牌的费用和数值都不读。地图有 10 个词元，重复的也保留。',
        '只留下 id：牌的費用和數值都不讀。地圖有 10 個詞元，重複的也保留。',
      ),
      rows: [
        ['cards', 'DEFEND_SILENT ×5, BACKFLIP ×2, STRIKE_SILENT ×2, ACCELERANT+ (SWIFT), ADRENALINE, ASCENDERS_BANE, CALCULATED_GAMBLE, CLUMSY, DEFLECT, LEG_SWEEP, MALAISE+, MIRAGE, NEUTRALIZE+, OUTBREAK+, PREDATOR+, SNAKEBITE, SNAKEBITE+, SUCKER_PUNCH+, SURVIVOR'],
        ['relics', 'RING_OF_THE_SNAKE, PRECISE_SCISSORS, CHOSEN_CHEESE, WHETSTONE, ODDLY_SMOOTH_STONE, PANTOGRAPH, PAELS_LEGION, EMBER_TEA:used, MUSIC_BOX, SNECKO_SKULL'],
        ['potions', '–'],
        ['context', 'character:SILENT, act:GLORY, boss:QUEEN_BOSS, boss:TEST_SUBJECT_BOSS, ancient:VAKUU'],
        ['map', 'RestSite+0, Monster+1, Unknown+2, Elite+2, Monster+3, Elite+4, Unknown+4, RestSite+5, Boss+6, Boss+7'],
      ],
    },
    {
      title: w('Table entries', '表项', '表項'),
      text: w('Each token becomes its row in its table.', '每个词元换成它在表里的行号。', '每個詞元換成它在表裡的行號。'),
      rows: [
        ['DEFEND_SILENT', '150'], ['STRIKE_SILENT', '518'], ['SNAKEBITE', '482'], ['RING_OF_THE_SNAKE', '228'], ['character:SILENT', '31'], ['RestSite+0', '169'], ['Monster+1', '129'],
      ],
    },
    {
      title: w('Card vectors', '牌向量', '牌向量'),
      text: w('x = E + u·U + Σ M, 64 numbers. A plain card’s x is E alone; an upgraded one adds U, and ACCELERANT+ also its enchantment’s M.', 'x = E + u·U + Σ M，64 个数。普通牌的 x 就是 E；升级过的再加上 U，ACCELERANT+ 还加上它的附魔的 M。', 'x = E + u·U + Σ M，64 個數。普通牌的 x 就是 E；升級過的再加上 U，ACCELERANT+ 還加上它的附魔的 M。'),
      rows: [
        ['DEFEND_SILENT', '[−0.106, −0.196, 0.149, 0.139, …]'], ['STRIKE_SILENT', '[0.116, 0.020, −0.003, −0.149, …]'], ['SNAKEBITE+', '[0.259, 0.497, 0.264, −0.191, …]'], ['ACCELERANT+ (SWIFT)', '[−0.263, 0.333, −0.793, 0.159, …]'],
      ],
    },
    {
      title: w('φ, card by card', 'φ，逐张计算', 'φ，逐張計算'),
      text: w('The same two layers for each card, 256 numbers, none below 0. Equal cards give equal vectors; no card sees another. The length is the vector’s norm.', '每张牌用同样的两层，256 个数，都不小于 0。相同的牌得到相同的向量，牌与牌互相看不到。长度是向量的范数。', '每張牌用同樣的兩層，256 個數，都不小於 0。相同的牌得到相同的向量，牌與牌互相看不到。長度是向量的範數。'),
      rows: [
        ['DEFEND_SILENT', '[0.248, 0.182, 0.444, 0.137, …] |3.92|'], ['STRIKE_SILENT', '[0.165, 0.319, 0.280, 0.142, …] |3.58|'], ['SNAKEBITE+', '[1.052, 1.448, 1.035, 0.839, …] |14.86|'], ['ACCELERANT+ (SWIFT)', '[0.298, 0, 0, 0, …] |8.66|'],
      ],
    },
    {
      title: w('Pooling the deck', '汇总牌组', '匯聚牌組'),
      text: w('All 25 vectors added up, then divided by 25 for the mean and by 16 for the scaled sum. Their ratio is how the body can read the deck’s size.', '25 个向量全部相加，再除以 25 得到平均，除以 16 得到缩放后的和。两者的比值就是主干读到牌数的方式。', '25 個向量全部相加，再除以 25 得到平均，除以 16 得到縮放後的和。兩者的比值就是主幹讀到牌數的方式。'),
      rows: [['mean', '[0.357, 0.340, 0.412, 0.265, …]'], ['sum ÷ 16', '[0.558, 0.532, 0.643, 0.414, …]']],
    },
    {
      title: w('The bags', '四个袋', '四個袋'),
      text: w('Each bag’s embeddings added up, 64 numbers each, with no layer in between. No potion: the zero vector.', '每个袋的嵌入直接相加，各 64 个数，中间没有任何一层。没有药水，就是零向量。', '每個袋的嵌入直接相加，各 64 個數，中間沒有任何一層。沒有藥水，就是零向量。'),
      rows: [['relics', '[0.298, −0.681, 0.519, 0.636, …] |3.04|'], ['potions', '[0, 0, 0, 0, …] |0|'], ['context', '[0.083, 0.533, −1.454, 0.369, …] |4.89|'], ['map', '[2.050, 0.177, −0.747, −0.880, …] |3.92|']],
    },
    {
      title: w('Six numbers', '六个数字', '六個數字'),
      text: w('(x − μ)/σ, by the net file’s μ and σ, kept from the nets it went on from. Ascension has μ 0 and σ read as 1, since the first of those nets’ training runs were all A0, so A10 reads as 10.', '(x − μ)/σ，用网络文件里的 μ 和 σ，它们沿用自它接着训练的那些网络。其中最早那个的训练局都是 A0，所以进阶的 μ 是 0、σ 读作 1，A10 就读作 10。', '(x − μ)/σ，用網路檔案裡的 μ 和 σ，它們沿用自它接著訓練的那些網路。其中最早那個的訓練局都是 A0，所以晉階的 μ 是 0、σ 讀作 1，A10 就讀作 10。'),
      rows: [['floor', '41 → 2.910'], ['HP', '75 → 0.791'], ['max HP', '85 → 2.737'], ['gold', '53 → −0.885'], ['ascension', '10 → 10'], ['potion slots', '2 → −2.244']],
    },
    {
      title: w('Join and body', '拼接与主干', '串接與主幹'),
      text: w('512 + 4 × 64 + 6 = 774 numbers, then two ReLU layers. Here 349 of the first layer’s 512 numbers and 231 of the second’s are above 0.', '512 + 4 × 64 + 6 = 774 个数，再经过两层 ReLU。这里第一层 512 个数有 349 个大于 0，第二层有 231 个。', '512 + 4 × 64 + 6 = 774 個數，再經過兩層 ReLU。這裡第一層 512 個數有 349 個大於 0，第二層有 231 個。'),
      rows: [['z', '774'], ['b₁', '512 (349 > 0)'], ['b₂', '512 (231 > 0)']],
    },
    {
      title: w('Hazards and the value', '风险值与价值', '風險值與價值'),
      text: w('One chance per floor. Floors 48 and 49 are the third act’s two bosses. The product of every (1 − h) from floor 41 on is the value.', '每层一个概率。第 48、49 层是第三幕的两个 Boss。从第 41 层起所有 (1 − h) 的连乘就是价值。', '每層一個機率。第 48、49 層是第三幕的兩個頭目。從第 41 層起所有 (1 − h) 的連乘就是價值。'),
      rows: [['h₄₁ … h₄₇', '0.0000, 0.0058, 0.0088, 0.0065, 0.0176, 0.0372, 0.0000'], ['h₄₈, h₄₉', '0.4296, 0.8943'], ['h₅₀', '0.0001'], ['P(win)', '0.0558']],
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
