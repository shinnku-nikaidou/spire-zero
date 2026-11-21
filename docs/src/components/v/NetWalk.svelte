<script lang="ts">
  // One real run through net.Net, step by step: the champion's V file's first test run, values as the trainer gives
  // them (src/data/v-walk.json, from scripts/v-walk.py). Vectors show their first four numbers.
  import walk from '../../data/v-walk.json';
  import type { Lang } from './names.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  type W = { en: string; 'zh-CN': string; 'zh-TW': string };
  const w = (en: string, cn: string, tw: string): W => ({ en, 'zh-CN': cn, 'zh-TW': tw });
  const num = (x: number, d = 3) => x.toFixed(d).replace('-', '−');
  const vec = (xs: number[]) => `[${xs.map((x) => num(x)).join(', ')}, …]`;
  const sub = (n: number) => String(n).replace(/\d/g, (c) => '₀₁₂₃₄₅₆₇₈₉'[+c]);
  const list = (xs: string[]) => (xs.length ? xs.join(', ') : '–');
  // A run of hazards: trailing equal values as "x ×n".
  const hazards = (hs: number[]) => {
    let end = hs.length;
    while (end > 1 && hs[end - 1] === hs[hs.length - 1]) end--;
    const tail = hs.length - end;
    const shown = (tail >= 3 ? hs.slice(0, end) : hs).map((h) => num(h, 4));
    return (tail >= 3 ? [...shown, `${num(hs[hs.length - 1], 4)} ×${tail}`] : shown).join(', ');
  };

  const r = walk.run;
  const t = walk.tags;
  const b = walk.body;
  const room: Record<string, W> = {
    Monster: w('a monster fight', '一场怪物战', '一場怪物戰'),
    Elite: w('an elite fight', '一场精英战', '一場菁英戰'),
    Boss: w('a boss fight', '一场 Boss 战', '一場頭目戰'),
  };
  const after = room[r.after ?? ''] ?? w('a room', '一个房间', '一個房間');
  const bosses = walk.hazards.bosses;
  const bossList = { en: bosses.join(', '), 'zh-CN': bosses.join('、'), 'zh-TW': bosses.join('、') };
  const STEPS: { title: W; text: W; rows: string[][] }[] = [
    {
      title: w('The run', '这一局', '這一局'),
      text: w(
        `A position the champion’s V file keeps as a test: just after ${after.en} on floor ${r.floor}.`,
        `冠军的 V 文件留作测试的一个局面：第 ${r.floor} 层${after['zh-CN']}刚打完。`,
        `冠軍的 V 檔案留作測試的一個局面：第 ${r.floor} 層${after['zh-TW']}剛打完。`,
      ),
      rows: [
        ['character', r.character], ['ascension', String(r.ascension)], ['floor', String(r.floor)], ['HP', `${r.hp} / ${r.max_hp}`],
        ['gold', String(r.gold)], ['deck', String(r.deck)], ['relics', String(r.relics)], ['potions', `${r.potions} / ${r.slots} slots`],
      ],
    },
    {
      title: w('Tags', '标记', '標記'),
      text: w(
        `Ids, and the numbers each read through rows of its own: a card’s damage, block and what it has grown in this run, its modifiers’ amounts; each relic’s counter, melted flag and status. A card’s cost is not read. ${t.modified} of these cards carry modifiers. The map is ${t.map.length} tags, one per point; two points of one type at one distance would give the same tag twice.`,
        `id，加上各由自己那一行读取的数字：牌的伤害、格挡和这一局里长大的数字，修饰的数值；每件遗物的计数、熔化标记和状态。牌的费用不读。这里有 ${t.modified} 张牌带修饰。地图有 ${t.map.length} 个标记，每个点一个；同类型、同距离的两个点会给出两个相同的标记。`,
        `id，加上各由自己那一列讀取的數字：牌的傷害、格擋和這一局裡長大的數字，修飾的數值；每件遺物的計數、熔化標記和狀態。牌的費用不讀。這裡有 ${t.modified} 張牌帶修飾。地圖有 ${t.map.length} 個標記，每個點一個；同類型、同距離的兩個點會給出兩個相同的標記。`,
      ),
      rows: [
        ['cards', list(t.cards)],
        ['damage ÷ 20', list(t.damage)],
        ['block ÷ 20', list(t.block)],
        ...(t.grown.length ? [['grown', list(t.grown)]] : []),
        ['relics', list(t.relics)],
        ['relic counter ÷ 10', `${list(t.counters)}; ${Object.entries(t.statuses).map(([s, n]) => `${s} ×${n}`).join(', ')}; ${t.melted} melted`],
        ['kept cards', list(t.kept)],
        ['potions', list(t.potions)],
        ['context', list(t.context)],
        ['map', list(t.map)],
      ],
    },
    {
      title: w('Table lookup', '查表', '查表'),
      text: w('Each tag becomes its row in its table.', '每个标记换成它在表里的行号。', '每個標記換成它在表裡的行號。'),
      rows: walk.lookup.map(([tag, row]) => [String(tag), String(row)]),
    },
    {
      title: w('Card vectors', '牌向量', '牌向量'),
      text: w(
        'x = E + u·U + d·D + k·K + g·G + Σ (M + a·A), 64 numbers; each card adds the terms it has. A keyword has no amount, so a card adds its keywords’ M alone.',
        'x = E + u·U + d·D + k·K + g·G + Σ (M + a·A)，64 个数；每张牌只加上它有的项。关键词没有数值，所以牌只加上它关键词的 M。',
        'x = E + u·U + d·D + k·K + g·G + Σ (M + a·A)，64 個數；每張牌只加上它有的項。關鍵字沒有數值，所以牌只加上它關鍵字的 M。',
      ),
      rows: walk.card_vectors.map(([name, formula, xs]) => [String(name), `${formula} = ${vec(xs as number[])}`]),
    },
    {
      title: w('φ, card by card', 'φ，逐张计算', 'φ，逐張計算'),
      text: w('The same two layers for each card, 256 numbers, none below 0. Equal cards give equal vectors; no card sees another. The length is the vector’s norm.', '每张牌用同样的两层，256 个数，都不小于 0。相同的牌得到相同的向量，牌与牌互相看不到。长度是向量的范数。', '每張牌用同樣的兩層，256 個數，都不小於 0。相同的牌得到相同的向量，牌與牌互相看不到。長度是向量的範數。'),
      rows: walk.phi.map(([name, xs, n]) => [String(name), `${vec(xs as number[])} |${num(n as number, 2)}|`]),
    },
    {
      title: w('Pooling the deck', '汇总牌组', '匯聚牌組'),
      text: w(
        `All ${walk.pool.n} vectors added up, then divided by ${walk.pool.n} for the mean and by 16 for the scaled sum. The 16 is a fixed factor in the code that keeps the sum about a mean’s size; the two differ by ${walk.pool.n}/16, which is how the body can read the deck’s size.`,
        `${walk.pool.n} 个向量全部相加，再除以 ${walk.pool.n} 得到平均，除以 16 得到缩放后的和。16 是代码里固定的因子，让和保持在平均值的量级；两者相差 ${walk.pool.n}/16 倍，这就是主干读到牌数的方式。`,
        `${walk.pool.n} 個向量全部相加，再除以 ${walk.pool.n} 得到平均，除以 16 得到縮放後的和。16 是程式碼裡固定的係數，讓和大約和平均值一樣大；兩者相差 ${walk.pool.n}/16 倍，這就是主幹讀到牌數的方式。`,
      ),
      rows: [['mean', vec(walk.pool.mean)], ['sum ÷ 16', vec(walk.pool.sum)]],
    },
    {
      title: w('Four groups added up', '四类加总', '四類加總'),
      text: w(
        `The vectors each group looks up in its table are added up, 64 numbers each, with no layer in between; a relic’s with its counter’s, melted and status rows.${r.potions ? '' : ' No potion: the zero vector.'}`,
        `每一类查表得到的向量直接相加，各 64 个数，中间没有任何一层；遗物的还加上它的计数、熔化和状态那几行。${r.potions ? '' : '没有药水，就是零向量。'}`,
        `每一類查表得到的向量直接相加，各 64 個數，中間沒有任何一層；遺物的還加上它的計數、熔化和狀態那幾列。${r.potions ? '' : '沒有藥水，就是零向量。'}`,
      ),
      rows: walk.groups.map(([name, xs, n]) => [String(name), `${vec(xs as number[])} |${num(n as number, 2)}|`]),
    },
    {
      title: w('Nine numbers', '九个数字', '九個數字'),
      text: w(
        `(x − μ)/σ, by the net file’s μ and σ. Ascension is read as it is (μ 0, σ 1): this A${r.ascension} run reads ${r.ascension}. The last three are the game’s rule, 0 in a whole run: whether each act wins its plain monster fights at their start.`,
        `(x − μ)/σ，用网络文件里的 μ 和 σ。进阶照原样读（μ 是 0、σ 是 1）：这一局是 A${r.ascension}，读作 ${r.ascension}。最后三个是游戏规则，完整的一局都是 0：每一幕的普通怪物战是否在开打时就算赢。`,
        `(x − μ)/σ，用網路檔案裡的 μ 和 σ。晉階照原樣讀（μ 是 0、σ 是 1）：這一局是 A${r.ascension}，讀作 ${r.ascension}。最後三個是遊戲規則，完整的一局都是 0：每一幕的普通怪物戰是否在開打時就算贏。`,
      ),
      rows: walk.numbers.map(([name, raw, z]) =>
        Array.isArray(raw)
          ? [`${name}, acts 1–3`, `${(raw as number[]).join(' · ')} → ${(z as number[]).map((x) => num(x)).join(' · ')}`]
          : [String(name), `${raw} → ${num(z as number)}`],
      ),
    },
    {
      title: w('Join and body', '拼接与主干', '串接與主幹'),
      text: w(
        `512 + 4 × 64 + 9 + 256 + 512 = ${b.width.toLocaleString('en')} numbers: 256 for the cards the relics keep${b.kept_zero ? ', none here: zeros' : ''}, and the last 512 for an event option picked and not yet played out${b.event_zero ? ', none here: zeros' : ''}. Then two ReLU layers. Here ${b.b1[0]} of the first layer’s ${b.b1[1]} numbers and ${b.b2[0]} of the second’s are above 0.`,
        `512 + 4 × 64 + 9 + 256 + 512 = ${b.width.toLocaleString('en')} 个数：256 个是遗物保留的牌${b.kept_zero ? '，这里没有，全是零' : ''}；最后 512 个是选了还没结算的事件选项${b.event_zero ? '，这里没有，全是零' : ''}。再经过两层 ReLU。这里第一层 ${b.b1[1]} 个数有 ${b.b1[0]} 个大于 0，第二层有 ${b.b2[0]} 个。`,
        `512 + 4 × 64 + 9 + 256 + 512 = ${b.width.toLocaleString('en')} 個數：256 個是遺物保留的牌${b.kept_zero ? '，這裡沒有，全是零' : ''}；最後 512 個是選了還沒結算的事件選項${b.event_zero ? '，這裡沒有，全是零' : ''}。再經過兩層 ReLU。這裡第一層 ${b.b1[1]} 個數有 ${b.b1[0]} 個大於 0，第二層有 ${b.b2[0]} 個。`,
      ),
      rows: [['z', b.width.toLocaleString('en')], ['b₁', `${b.b1[1]} (${b.b1[0]} > 0)`], ['b₂', `${b.b2[1]} (${b.b2[0]} > 0)`]],
    },
    {
      title: w('Hazards and the value', '风险值与价值', '風險值與價值'),
      text: w(
        `One chance per floor. Floors ${bossList.en} are the acts’ bosses. The product of every (1 − h) from floor ${walk.hazards.start} on is the value.`,
        `每层一个概率。第 ${bossList['zh-CN']} 层是各幕的 Boss。从第 ${walk.hazards.start} 层起所有 (1 − h) 的连乘就是价值。`,
        `每層一個機率。第 ${bossList['zh-TW']} 層是各幕的頭目。從第 ${walk.hazards.start} 層起所有 (1 − h) 的連乘就是價值。`,
      ),
      rows: [
        ...walk.hazards.spans.map((s) => [s.from === s.to ? `h${sub(s.from)}` : `h${sub(s.from)} … h${sub(s.to)}`, hazards(s.h)]),
        ['P(win)', num(walk.value)],
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
