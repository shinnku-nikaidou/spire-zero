<script lang="ts">
  // One real run through net.Net, step by step: v8-1's test run at a map move (floor 4), values as the trainer gave
  // them. Vectors show their first four numbers (docs: macro-brain/network §9).
  import type { Lang } from './names.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  type W = { en: string; 'zh-CN': string; 'zh-TW': string };
  const w = (en: string, cn: string, tw: string): W => ({ en, 'zh-CN': cn, 'zh-TW': tw });
  const STEPS: { title: W; text: W; rows: string[][] }[] = [
    {
      title: w('The run', '这一局', '這一局'),
      text: w(
        'The afterstate of a map move: the shop is about to be entered. Nothing else has changed.',
        '一次选路的 afterstate：正要进入商店，其他都没变。',
        '一次選路的 afterstate：正要進入商店，其他都沒變。',
      ),
      rows: [['floor', '4'], ['HP', '64 / 70'], ['gold', '136'], ['deck', '14'], ['relics', '2'], ['potions', '0 / 4 slots']],
    },
    {
      title: w('Tokens', '词元', '詞元'),
      text: w(
        'Ids only: a card’s cost and numbers are gone. The map is 32 tokens; repeats stay.',
        '只留下 id：牌的费用和数值都不读。地图有 32 个词元，重复的也保留。',
        '只留下 id：牌的費用和數值都不讀。地圖有 32 個詞元，重複的也保留。',
      ),
      rows: [
        ['cards', 'DEFEND_SILENT ×5, STRIKE_SILENT ×5, DEADLY_POISON, NEUTRALIZE, SNAKEBITE, SURVIVOR'],
        ['relics', 'RING_OF_THE_SNAKE, PHIAL_HOLSTER'],
        ['potions', '–'],
        ['context', 'character:SILENT, act:OVERGROWTH, boss:THE_KIN_BOSS, ancient:NEOW'],
        ['map', 'Shop+0, Monster+1, RestSite+2, Unknown+2, Monster+2, … Boss+12'],
      ],
    },
    {
      title: w('Table entries', '表项', '表項'),
      text: w('Each token becomes its row in its table.', '每个词元换成它在表里的行号。', '每個詞元換成它在表裡的行號。'),
      rows: [
        ['DEFEND_SILENT', '150'], ['STRIKE_SILENT', '518'], ['DEADLY_POISON', '137'], ['RING_OF_THE_SNAKE', '228'], ['character:SILENT', '31'], ['Shop+0', '211'], ['Monster+1', '129'],
      ],
    },
    {
      title: w('Card vectors', '牌向量', '牌向量'),
      text: w('x = E + u·U + Σ M, 32 numbers. No card here is upgraded or modified, so x is E alone.', 'x = E + u·U + Σ M，32 个数。这里的牌都没升级也没修饰，所以 x 就是 E。', 'x = E + u·U + Σ M，32 個數。這裡的牌都沒升級也沒修飾，所以 x 就是 E。'),
      rows: [
        ['DEFEND_SILENT', '[−0.106, −0.195, 0.153, 0.141, …]'], ['STRIKE_SILENT', '[0.119, 0.020, −0.001, −0.146, …]'], ['DEADLY_POISON', '[−0.110, 0.098, −0.030, −0.119, …]'], ['SNAKEBITE', '[0.199, 0.204, 0.206, −0.070, …]'],
      ],
    },
    {
      title: w('φ, card by card', 'φ，逐张计算', 'φ，逐張計算'),
      text: w('The same two layers for each card, 128 numbers, none below 0. Equal cards give equal vectors; no card sees another. The length is the vector’s norm.', '每张牌用同样的两层，128 个数，都不小于 0。相同的牌得到相同的向量，牌与牌互相看不到。长度是向量的范数。', '每張牌用同樣的兩層，128 個數，都不小於 0。相同的牌得到相同的向量，牌與牌互相看不到。長度是向量的範數。'),
      rows: [
        ['DEFEND_SILENT', '[0.288, 0.227, 0.485, 0.161, …] |4.24|'], ['STRIKE_SILENT', '[0.186, 0.351, 0.308, 0.149, …] |3.80|'], ['DEADLY_POISON', '[0.422, 0.849, 0.574, 0.360, …] |6.92|'], ['SNAKEBITE', '[0.597, 1.130, 0.715, 0.512, …] |9.30|'],
      ],
    },
    {
      title: w('Pooling the deck', '汇总牌组', '匯聚牌組'),
      text: w('All 14 vectors added up, then divided by 14 and by 16. The two are one vector at two scales: here their ratio is 16/14, which is how the body can read the deck’s size.', '14 个向量全部相加，再分别除以 14 和 16。两者是同一个向量的两种比例：这里比值是 16/14，主干就是这样读到牌数的。', '14 個向量全部相加，再分別除以 14 和 16。兩者是同一個向量的兩種比例：這裡比值是 16/14，主幹就是這樣讀到牌數的。'),
      rows: [['mean', '[0.291, 0.382, 0.442, 0.219, …]'], ['sum ÷ 16', '[0.254, 0.334, 0.387, 0.191, …]']],
    },
    {
      title: w('The bags', '四个袋', '四個袋'),
      text: w('Each bag’s embeddings added up, 32 numbers each, with no layer in between. No potion: the zero vector.', '每个袋的嵌入直接相加，各 32 个数，中间没有任何一层。没有药水，就是零向量。', '每個袋的嵌入直接相加，各 32 個數，中間沒有任何一層。沒有藥水，就是零向量。'),
      rows: [['relics', '[−0.079, −0.023, 0.079, 0.159, …] |0.85|'], ['potions', '[0, 0, 0, 0, …] |0|'], ['context', '[−0.443, −0.064, 0.348, 0.091, …] |2.02|'], ['map', '[0.537, 0.142, −0.168, −0.956, …] |5.88|']],
    },
    {
      title: w('Six numbers', '六个数字', '六個數字'),
      text: w('(x − μ)/σ, by the net file’s μ and σ. Ascension has σ read as 1: all training runs are A0.', '(x − μ)/σ，用网络文件里的 μ 和 σ。进阶的 σ 读作 1：训练局都是 A0。', '(x − μ)/σ，用網路檔案裡的 μ 和 σ。晉階的 σ 讀作 1：訓練局都是 A0。'),
      rows: [['floor', '4 → −1.011'], ['HP', '64 → 0.118'], ['max HP', '70 → −0.165'], ['gold', '136 → −0.261'], ['ascension', '0 → 0'], ['potion slots', '4 → 1.785']],
    },
    {
      title: w('Join and body', '拼接与主干', '串接與主幹'),
      text: w('256 + 4 × 32 + 6 = 390 numbers, then two ReLU layers. Here 200 of the first layer’s 256 numbers and 92 of the second’s are above 0.', '256 + 4 × 32 + 6 = 390 个数，再经过两层 ReLU。这里第一层 256 个数有 200 个大于 0，第二层有 92 个。', '256 + 4 × 32 + 6 = 390 個數，再經過兩層 ReLU。這裡第一層 256 個數有 200 個大於 0，第二層有 92 個。'),
      rows: [['z', '390'], ['b₁', '256 (200 > 0)'], ['b₂', '256 (92 > 0)']],
    },
    {
      title: w('Hazards and the value', '风险值与价值', '風險值與價值'),
      text: w('One chance per floor. Floor 17 is the act’s boss. The product of every (1 − h) from floor 4 on is the value.', '每层一个概率，第 17 层是这一幕的 Boss。从第 4 层起所有 (1 − h) 的连乘就是价值。', '每層一個機率，第 17 層是這一幕的頭目。從第 4 層起所有 (1 − h) 的連乘就是價值。'),
      rows: [['h₄ … h₉', '0.0007, 0.0001, 0.0013, 0.0091, 0.0360, 0.0356'], ['h₁₁ … h₁₅', '0.0267, 0.0195, 0.0154, 0.0361, 0.0365'], ['h₁₇', '0.7174'], ['P(win)', '0.00192']],
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
