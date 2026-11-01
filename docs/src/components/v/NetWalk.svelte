<script lang="ts">
  // One real run through net.Net, step by step: v8-2's test run at a map move (floor 3), values as the trainer gave
  // them. Vectors show their first four numbers (docs: macro-brain/network §9).
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
      rows: [['floor', '3'], ['HP', '51 / 58'], ['gold', '124'], ['deck', '16'], ['relics', '4'], ['potions', '0 / 2 slots']],
    },
    {
      title: w('Tokens', '词元', '詞元'),
      text: w(
        'Ids only: a card’s cost and numbers are gone. The map is 21 tokens; repeats stay.',
        '只留下 id：牌的费用和数值都不读。地图有 21 个词元，重复的也保留。',
        '只留下 id：牌的費用和數值都不讀。地圖有 21 個詞元，重複的也保留。',
      ),
      rows: [
        ['cards', 'DEFEND_SILENT ×4, STRIKE_SILENT ×4, ASCENDERS_BANE, BACKSTAB, CALCULATED_GAMBLE, FLASH_OF_STEEL, HAZE, NEUTRALIZE, NORMALITY, SURVIVOR'],
        ['relics', 'RING_OF_THE_SNAKE, NEOWS_BONES, LEAD_PAPERWEIGHT, LEAFY_POULTICE'],
        ['potions', '–'],
        ['context', 'character:SILENT, act:UNDERDOCKS, boss:WATERFALL_GIANT_BOSS, ancient:NEOW'],
        ['map', 'Unknown+0, Unknown+1, Monster+2, RestSite+3, Elite+4, … Boss+13'],
      ],
    },
    {
      title: w('Table entries', '表项', '表項'),
      text: w('Each token becomes its row in its table.', '每个词元换成它在表里的行号。', '每個詞元換成它在表裡的行號。'),
      rows: [
        ['DEFEND_SILENT', '150'], ['STRIKE_SILENT', '518'], ['BACKSTAB', '27'], ['RING_OF_THE_SNAKE', '228'], ['character:SILENT', '31'], ['Unknown+0', '337'], ['Monster+2', '151'],
      ],
    },
    {
      title: w('Card vectors', '牌向量', '牌向量'),
      text: w('x = E + u·U + Σ M, 32 numbers. No card here is upgraded or modified, so x is E alone.', 'x = E + u·U + Σ M，32 个数。这里的牌都没升级也没修饰，所以 x 就是 E。', 'x = E + u·U + Σ M，32 個數。這裡的牌都沒升級也沒修飾，所以 x 就是 E。'),
      rows: [
        ['DEFEND_SILENT', '[−0.104, −0.197, 0.149, 0.139, …]'], ['STRIKE_SILENT', '[0.117, 0.023, −0.000, −0.148, …]'], ['BACKSTAB', '[−0.304, −0.107, 0.091, −0.242, …]'], ['ASCENDERS_BANE', '[0.009, −0.038, 0.217, 0.079, …]'],
      ],
    },
    {
      title: w('φ, card by card', 'φ，逐张计算', 'φ，逐張計算'),
      text: w('The same two layers for each card, 128 numbers, none below 0. Equal cards give equal vectors; no card sees another. The length is the vector’s norm.', '每张牌用同样的两层，128 个数，都不小于 0。相同的牌得到相同的向量，牌与牌互相看不到。长度是向量的范数。', '每張牌用同樣的兩層，128 個數，都不小於 0。相同的牌得到相同的向量，牌與牌互相看不到。長度是向量的範數。'),
      rows: [
        ['DEFEND_SILENT', '[0.230, 0.180, 0.410, 0.135, …] |3.74|'], ['STRIKE_SILENT', '[0.161, 0.332, 0.263, 0.152, …] |3.56|'], ['BACKSTAB', '[0.333, 0.019, 0.381, 0.299, …] |3.03|'], ['ASCENDERS_BANE', '[0.112, 0.842, 0.531, 0.217, …] |6.09|'],
      ],
    },
    {
      title: w('Pooling the deck', '汇总牌组', '匯聚牌組'),
      text: w('All 16 vectors added up, then divided by 16 for the mean and by 16 for the scaled sum. With a deck of 16 the two are equal; on any other deck their ratio is how the body can read the deck’s size.', '16 个向量全部相加，再除以 16 得到平均，除以 16 得到缩放后的和。牌组正好 16 张，所以两者相等；换成其他牌数，两者的比值就是主干读到牌数的方式。', '16 個向量全部相加，再除以 16 得到平均，除以 16 得到縮放後的和。牌組正好 16 張，所以兩者相等；換成其他牌數，兩者的比值就是主幹讀到牌數的方式。'),
      rows: [['mean', '[0.245, 0.292, 0.367, 0.214, …]'], ['sum ÷ 16', '[0.245, 0.292, 0.367, 0.214, …]']],
    },
    {
      title: w('The bags', '四个袋', '四個袋'),
      text: w('Each bag’s embeddings added up, 32 numbers each, with no layer in between. No potion: the zero vector.', '每个袋的嵌入直接相加，各 32 个数，中间没有任何一层。没有药水，就是零向量。', '每個袋的嵌入直接相加，各 32 個數，中間沒有任何一層。沒有藥水，就是零向量。'),
      rows: [['relics', '[−0.254, −0.057, 0.156, −0.033, …] |1.32|'], ['potions', '[0, 0, 0, 0, …] |0|'], ['context', '[−0.327, 0.065, 0.214, −0.257, …] |1.74|'], ['map', '[1.494, −0.515, −0.277, −0.356, …] |3.36|']],
    },
    {
      title: w('Six numbers', '六个数字', '六個數字'),
      text: w('(x − μ)/σ, by the net file’s μ and σ, those of the net it went on from. Ascension has μ 0 and σ read as 1, since that net’s training runs were all A0, so A10 reads as 10.', '(x − μ)/σ，用网络文件里的 μ 和 σ，也就是它接续的那个网络的值。那个网络的训练局都是 A0，所以进阶的 μ 是 0、σ 读作 1，A10 就读作 10。', '(x − μ)/σ，用網路檔案裡的 μ 和 σ，也就是它接續的那個網路的值。那個網路的訓練局都是 A0，所以晉階的 μ 是 0、σ 讀作 1，A10 就讀作 10。'),
      rows: [['floor', '3 → −1.117'], ['HP', '51 → −0.677'], ['max HP', '58 → −2.487'], ['gold', '124 → −0.351'], ['ascension', '10 → 10'], ['potion slots', '2 → −2.244']],
    },
    {
      title: w('Join and body', '拼接与主干', '串接與主幹'),
      text: w('256 + 4 × 32 + 6 = 390 numbers, then two ReLU layers. Here 208 of the first layer’s 256 numbers and 100 of the second’s are above 0.', '256 + 4 × 32 + 6 = 390 个数，再经过两层 ReLU。这里第一层 256 个数有 208 个大于 0，第二层有 100 个。', '256 + 4 × 32 + 6 = 390 個數，再經過兩層 ReLU。這裡第一層 256 個數有 208 個大於 0，第二層有 100 個。'),
      rows: [['z', '390'], ['b₁', '256 (208 > 0)'], ['b₂', '256 (100 > 0)']],
    },
    {
      title: w('Hazards and the value', '风险值与价值', '風險值與價值'),
      text: w('One chance per floor. Floors 17 and 33 are the first two acts’ bosses, and 48 and 49 the third’s two. The product of every (1 − h) from floor 3 on is the value.', '每层一个概率。第 17、33 层是前两幕的 Boss，第 48、49 层是第三幕的两个 Boss。从第 3 层起所有 (1 − h) 的连乘就是价值。', '每層一個機率。第 17、33 層是前兩幕的頭目，第 48、49 層是第三幕的兩個頭目。從第 3 層起所有 (1 − h) 的連乘就是價值。'),
      rows: [['h₃ … h₉', '0.0001, 0.0000, 0.0020, 0.0007, 0.0155, 0.0261, 0.0149'], ['h₁₁ … h₁₅', '0.0254, 0.0199, 0.0191, 0.0206, 0.0214'], ['h₁₇', '0.1648'], ['h₃₃', '0.4812'], ['h₄₈, h₄₉', '0.6952, 0.7383'], ['P(win)', '0.00607']],
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
