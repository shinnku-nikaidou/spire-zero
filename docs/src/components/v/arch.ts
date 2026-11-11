// A network drawn as stacked blocks, in the manner of the Transformer paper's figure 1 (Vaswani et al., 2017):
// columns of blocks from the inputs up, frames for repeated blocks, ⊕ for residual sums and side links for skip
// connections; the columns join in one block, then one trunk goes up to the output. `layout` places it all;
// ArchDiagram.svelte draws it. The net of the macro brain chapter (docs: macro-brain/network) and the combat net
// (docs: combat-brain/network) are at the end.
import type { Lang } from './names.ts';

export type Tone = 'embed' | 'attn' | 'ff' | 'norm' | 'linear' | 'pool' | 'head' | 'msg';

export interface Block {
  kind: 'block';
  id?: string;
  label: string; // lines split by \n
  tone: Tone;
  info: string;
}
export interface Add {
  kind: 'add';
  id?: string;
  info: string;
}
export interface Repeat {
  kind: 'repeat';
  times: string;
  info: string;
  items: Item[];
}
export type Item = Block | Add | Repeat;

export interface Column {
  title: string;
  input: string;
  items: Item[];
  narrow?: boolean; // short labels: narrower blocks
}

/** A skip connection: from the trunk just below (or above) block `from`, along the side, into block `to`. */
export interface Link {
  from: string;
  at: 'below' | 'above';
  to: string;
  side: 'left' | 'right';
}

export interface Diagram {
  columns: Column[];
  merge: Block;
  top: Item[];
  output: string;
  links: Link[];
}

export type Shape =
  | { t: 'block'; x: number; y: number; w: number; h: number; tone: Tone; lines: string[]; info: string }
  | { t: 'add'; cx: number; cy: number; info: string }
  | { t: 'frame'; x: number; y: number; w: number; h: number; times: string; info: string }
  | { t: 'arrow'; d: string; head: boolean }
  | { t: 'text'; x: number; y: number; lines: string[]; kind: 'title' | 'input' | 'output' };

const BW = 124; // block width
const NARROW = 100;
const TRUNK = 140;
export const LINE = 14;
const PAD = 7;
const NORM_H = 22;
const GAP = 18;
const R = 8;
const FX = 24;
const FY_TOP = 20;
const FY_BOT = 10;
const COL_GAP = 14;
const LANE = 12;

interface Box {
  cx: number;
  bottom: number;
  top: number;
  left: number;
  right: number;
}

const hasRepeat = (items: Item[]): boolean => items.some((i) => i.kind === 'repeat');
const height = (b: Block) => (b.tone === 'norm' && !b.label.includes('\n') ? NORM_H : 2 * PAD + b.label.split('\n').length * LINE);

/** Every shape's place, in SVG coordinates, and the drawing's size. */
export function layout(d: Diagram): { shapes: Shape[]; width: number; height: number } {
  // placed bottom up, in u (up from the bottom edge); flipped to SVG's y at the end
  type Up = Shape;
  const shapes: Up[] = [];
  const boxes = new Map<string, Box>();
  const arrow = (x: number, from: number, to: number) => shapes.push({ t: 'arrow', d: `M${x},${from}V${to}`, head: true });

  const one = (it: Item, cx: number, u: number, from: number | null, bw: number): number => {
    if (it.kind === 'block') {
      const h = height(it);
      if (from !== null) arrow(cx, from, u);
      shapes.push({ t: 'block', x: cx - bw / 2, y: u, w: bw, h, tone: it.tone, lines: it.label.split('\n'), info: it.info });
      if (it.id) boxes.set(it.id, { cx, bottom: u, top: u + h, left: cx - bw / 2, right: cx + bw / 2 });
      return u + h;
    }
    if (it.kind === 'add') {
      if (from !== null) arrow(cx, from, u);
      shapes.push({ t: 'add', cx, cy: u + R, info: it.info });
      if (it.id) boxes.set(it.id, { cx, bottom: u, top: u + 2 * R, left: cx - R, right: cx + R });
      return u + 2 * R;
    }
    const inner = list(it.items, cx, u + FY_BOT, from, bw);
    const top = inner + FY_TOP;
    shapes.push({ t: 'arrow', d: `M${cx},${inner}V${top}`, head: false });
    shapes.push({ t: 'frame', x: cx - bw / 2 - FX, y: u, w: bw + 2 * FX, h: top - u, times: it.times, info: it.info });
    return top;
  };
  const list = (items: Item[], cx: number, u: number, from: number | null, bw: number): number => {
    let top = from;
    let at = u;
    for (const it of items) {
      top = one(it, cx, at, top, bw);
      at = top + GAP;
    }
    return top!;
  };

  // the columns: title at the bottom, the input above it, then the blocks
  const bws = d.columns.map((c) => (c.narrow ? NARROW : BW));
  const widths = d.columns.map((c, i) => bws[i] + 2 * (hasRepeat(c.items) ? FX + 4 : 6));
  const cxs: number[] = [];
  let x = 0;
  for (const w of widths) {
    cxs.push(x + w / 2);
    x += w + COL_GAP;
  }
  const width = x - COL_GAP;
  const inputLines = Math.max(...d.columns.map((c) => c.input.split('\n').length));
  const titleAt = 6;
  const inputTop = titleAt + LINE + 4 + inputLines * LINE;
  const first = inputTop + GAP;
  const tops = d.columns.map((c, i) => {
    shapes.push({ t: 'text', x: cxs[i], y: titleAt, lines: [c.title], kind: 'title' });
    const lines = c.input.split('\n');
    shapes.push({ t: 'text', x: cxs[i], y: titleAt + LINE + 4 + (lines.length - 1) * LINE, lines, kind: 'input' });
    return list(c.items, cxs[i], first, titleAt + LINE + 6 + lines.length * LINE, bws[i]);
  });

  // the join, then the trunk
  const mergeAt = Math.max(...tops) + GAP;
  const mh = height(d.merge);
  const left = cxs[0] - bws[0] / 2;
  const right = cxs[cxs.length - 1] + bws[bws.length - 1] / 2;
  tops.forEach((top, i) => arrow(cxs[i], top, mergeAt));
  shapes.push({ t: 'block', x: left, y: mergeAt, w: right - left, h: mh, tone: d.merge.tone, lines: d.merge.label.split('\n'), info: d.merge.info });
  const mid = (left + right) / 2;
  const top = list(d.top, mid, mergeAt + mh + GAP, mergeAt + mh, TRUNK);
  const outLines = d.output.split('\n');
  const outAt = top + GAP + 4;
  arrow(mid, top, outAt - 2);
  shapes.push({ t: 'text', x: mid, y: outAt + (outLines.length - 1) * LINE + 10, lines: outLines, kind: 'output' });
  const total = outAt + outLines.length * LINE + 8;

  // the skip connections, along a lane beside the blocks
  for (const l of d.links) {
    const a = boxes.get(l.from)!;
    const b = boxes.get(l.to)!;
    const tap = l.at === 'below' ? a.bottom - 6 : a.top + GAP / 2;
    const lane = l.side === 'left' ? Math.min(a.left, b.left) - LANE : Math.max(a.right, b.right) + LANE;
    const cy = (b.bottom + b.top) / 2;
    shapes.push({ t: 'arrow', d: `M${a.cx},${tap}H${lane}V${cy}H${l.side === 'left' ? b.left : b.right}`, head: true });
  }

  // flip: u up from the bottom becomes y down from the top
  const flip = (u: number) => total - u;
  const flipPath = (p: string) =>
    p.replace(/([MV])([\d.-]+)(?:,([\d.-]+))?/g, (_, c: string, a: string, b?: string) => (c === 'M' ? `M${a},${flip(+b!)}` : `V${flip(+a)}`));
  const out: Shape[] = shapes.map((s) => {
    switch (s.t) {
      case 'block':
      case 'frame':
        return { ...s, y: flip(s.y + s.h) };
      case 'add':
        return { ...s, cy: flip(s.cy) };
      case 'arrow':
        return { ...s, d: flipPath(s.d) };
      case 'text':
        return { ...s, y: flip(s.y) };
    }
  });
  return { shapes: out, width, height: total };
}

// The nets, in the site's three languages.
type W = Record<Lang, string>;
const w = (en: string, cn: string, tw: string): W => ({ en, 'zh-CN': cn, 'zh-TW': tw });

const T = {
  numbersTitle: w('Nine numbers', '九个数字', '九個數字'),
  numbersInput: w('floor · HP · max HP ·\ngold · ascension ·\npotion slots ·\nauto-win acts 1–3', '楼层 · 生命 · 最大生命 ·\n金币 · 进阶 ·\n药水栏 ·\n第 1–3 幕自动胜利', '樓層 · 生命 · 最大生命 ·\n金幣 · 晉階 ·\n藥水欄 ·\n第 1–3 幕自動勝利'),
  standardize: w('Standardize', '标准化', '標準化'),
  standardizeInfo: w(
    '(x − μ)/σ, with the mean and standard deviation of the training data that the net file keeps: 9 numbers.',
    '(x − μ)/σ，用网络文件里存的训练数据均值和标准差：9 个数。',
    '(x − μ)/σ，用網路檔案裡存的訓練資料平均和標準差：9 個數。',
  ),
  body1: (n: number) => w(`Linear + ReLU\n${n.toLocaleString('en-US')} → 512`, `线性层 + ReLU\n${n.toLocaleString('en-US')} → 512`, `線性層 + ReLU\n${n.toLocaleString('en-US')} → 512`),
  body1Info: w('The body’s first layer: 512 numbers.', '主干第一层：512 个数。', '主幹第一層：512 個數。'),
  body2: w('Linear + ReLU\n512 → 512', '线性层 + ReLU\n512 → 512', '線性層 + ReLU\n512 → 512'),
  body2Info: w('The body’s second layer: 512 numbers, which the heads read.', '主干第二层：512 个数，输出头读它。', '主幹第二層：512 個數，輸出頭讀它。'),
};

function numbers(lang: Lang): Column {
  return {
    title: T.numbersTitle[lang],
    input: T.numbersInput[lang],
    narrow: true,
    items: [{ kind: 'block', label: T.standardize[lang], tone: 'norm', info: T.standardizeInfo[lang] }],
  };
}

function body(lang: Lang, width: number): Item[] {
  return [
    { kind: 'block', label: T.body1(width)[lang], tone: 'linear', info: T.body1Info[lang] },
    { kind: 'block', label: T.body2[lang], tone: 'linear', info: T.body2Info[lang] },
  ];
}

export function valueNet(lang: Lang): Diagram {
  const L = (x: W) => x[lang];
  const bag = (title: W, input: W, info: W): Column => ({
    title: L(title),
    input: L(input),
    narrow: true,
    items: [
      { kind: 'block', label: L(w('Embeddings\n64 each', '嵌入\n每个 64', '嵌入\n每個 64')), tone: 'embed', info: L(info) },
      {
        kind: 'block',
        label: L(w('Sum', '求和', '加總')),
        tone: 'pool',
        info: L(w('The vectors of one group added up, padding left out: 64 numbers.', '同一类的向量相加（不含补齐位置）：64 个数。', '同一類的向量相加（不含補齊位置）：64 個數。')),
      },
    ],
  });
  return {
    columns: [
      {
        title: L(w('Deck', '牌组', '牌組')),
        input: L(w('each card: id · upgrades ·\nmodifiers · grown numbers', '每张牌：id · 升级 ·\n修饰 · 成长的数字', '每張牌：id · 升級 ·\n修飾 · 成長的數字')),
        items: [
          {
            kind: 'block',
            label: L(w('Card vector\nE + u·U + d·D + k·K + Σ (M + a·A): 64', '牌向量\nE + u·U + d·D + k·K + Σ (M + a·A)：64', '牌向量\nE + u·U + d·D + k·K + Σ (M + a·A)：64')),
            tone: 'embed',
            info: L(w('x = E[c] + u·U[c] + d·D[c] + k·K[c] + Σ (M[m] + a·A[m]): the id’s vector, u steps along its upgrade direction, its damage and block along its own directions, its modifiers with their amounts: 64 numbers per card.', 'x = E[c] + u·U[c] + d·D[c] + k·K[c] + Σ (M[m] + a·A[m])：id 的向量，沿它的升级方向走 u 步，伤害和格挡沿它自己的方向走，再加修饰和它们的数值：每张牌 64 个数。', 'x = E[c] + u·U[c] + d·D[c] + k·K[c] + Σ (M[m] + a·A[m])：id 的向量，沿它的升級方向走 u 步，傷害和格擋沿它自己的方向走，再加修飾和它們的數值：每張牌 64 個數。')),
          },
          {
            kind: 'repeat',
            times: 'φ',
            info: L(w('The same two layers for every card, each card on its own: no card sees another here.', '每张牌用同样的两层，各自独立：这里牌与牌互相看不到。', '每張牌用同樣的兩層，各自獨立：這裡牌與牌互相看不到。')),
            items: [
              { kind: 'block', label: L(w('Linear + ReLU\n64 → 256', '线性层 + ReLU\n64 → 256', '線性層 + ReLU\n64 → 256')), tone: 'ff', info: L(w('φ’s first layer: 256 numbers per card.', 'φ 的第一层：每张牌 256 个数。', 'φ 的第一層：每張牌 256 個數。')) },
              { kind: 'block', label: L(w('Linear + ReLU\n256 → 256', '线性层 + ReLU\n256 → 256', '線性層 + ReLU\n256 → 256')), tone: 'ff', info: L(w('φ’s second layer: 256 numbers per card, none below 0.', 'φ 的第二层：每张牌 256 个数，都不小于 0。', 'φ 的第二層：每張牌 256 個數，都不小於 0。')) },
            ],
          },
          {
            kind: 'block',
            label: L(w('Mean + sum ÷ 16', '均值 + 和 ÷ 16', '平均 + 總和 ÷ 16')),
            tone: 'pool',
            info: L(w('The cards pooled two ways, padding left out: 256 + 256 = 512 numbers, the same vector at two scales, which together also give the deck’s size.', '牌用两种方式汇总（不含补齐位置）：256 + 256 = 512 个数，是同一个向量的两种比例，合起来也给出牌数。', '牌用兩種方式匯總（不含補齊位置）：256 + 256 = 512 個數，是同一個向量的兩種比例，合起來也給出牌數。')),
          },
        ],
      },
      bag(
        w('Relics', '遗物', '遺物'),
        w('id · counter ·\nmelted · status', 'id · 计数 ·\n熔化 · 状态', 'id · 計數 ·\n熔化 · 狀態'),
        w(
          'R[r] + q·R_q[r] + μ·W + R_s[s]: one vector per relic id, its counter q (/ 10) along a direction of its id’s own, a vector if melted and its status’s vector, as the combat net reads a relic; “:used” is an id of its own.',
          'R[r] + q·R_q[r] + μ·W + R_s[s]：每个遗物 id 一个向量，计数 q（/ 10）沿它 id 自己的方向，熔化时加一个向量，再加它状态的向量，和战斗网络读遗物的方式一样；“:used” 是单独的 id。',
          'R[r] + q·R_q[r] + μ·W + R_s[s]：每個遺物 id 一個向量，計數 q（/ 10）沿它 id 自己的方向，熔化時加一個向量，再加它狀態的向量，和戰鬥網路讀遺物的方式一樣；「:used」是單獨的 id。',
        ),
      ),
      bag(w('Potions', '药水', '藥水'), w('each potion', '每瓶药水', '每瓶藥水'), w('P[p]: one vector per potion id; an empty slot does not count.', 'P[p]：每个药水 id 一个向量；空栏不算。', 'P[p]：每個藥水 id 一個向量；空欄不算。')),
      bag(w('Context', '上下文', '上下文'), w('character · act ·\nbosses · ancient', '角色 · 幕 ·\nBoss · 先古之民', '角色 · 幕 ·\n頭目 · 遠古之民'), w('C[t]: one vector per tag.', 'C[t]：每个标记一个向量。', 'C[t]：每個標記一個向量。')),
      bag(w('Map', '地图', '地圖'), w('type + floors ahead\nof each point', '每个点的\n类型 + 前方层数', '每個點的\n類型 + 前方層數'), w('G[t]: one vector per tag such as Elite+3; on a map move, the point entered as Elite+0.', 'G[t]：每个标记一个向量，如 Elite+3；选路时，正要进入的点是 Elite+0。', 'G[t]：每個標記一個向量，如 Elite+3；選路時，正要進入的點是 Elite+0。')),
      numbers(lang),
      {
        title: L(w('Kept cards', '遗物保留的牌', '遺物保留的牌')),
        input: L(w('cards a relic keeps\n(Pael’s Tooth’s)', '遗物保留的牌\n（佩尔之牙的）', '遺物保留的牌\n（佩爾之牙的）')),
        items: [
          {
            kind: 'block',
            label: L(w('Card vector\nE[c]: 64', '牌向量\nE[c]：64', '牌向量\nE[c]：64')),
            tone: 'embed',
            info: L(w('E[c], from the deck’s card table: the id alone, 64 numbers per card.', 'E[c]，来自牌组的牌表：只有 id，每张牌 64 个数。', 'E[c]，來自牌組的牌表：只有 id，每張牌 64 個數。')),
          },
          {
            kind: 'block',
            label: L(w('The deck’s φ\n64 → 256 → 256', '牌组的 φ\n64 → 256 → 256', '牌組的 φ\n64 → 256 → 256')),
            tone: 'ff',
            info: L(w('The same two layers as the deck’s cards, with the same weights, each card on its own.', '和牌组的牌同样的两层、同样的权重，每张牌各自独立。', '和牌組的牌同樣的兩層、同樣的權重，每張牌各自獨立。')),
          },
          {
            kind: 'block',
            label: L(w('Sum', '求和', '加總')),
            tone: 'pool',
            info: L(w('The kept cards added up, padding left out: 256 numbers, apart from the deck’s; zero when no relic keeps a card.', '保留的牌相加（不含补齐位置）：256 个数，和牌组的分开；没有遗物保留牌时为零。', '保留的牌相加（不含補齊位置）：256 個數，和牌組的分開；沒有遺物保留牌時為零。')),
          },
        ],
      },
    ],
    merge: {
      kind: 'block',
      label: L(w('Concatenate: 512 + 64 + 64 + 64 + 64 + 9 + 256 = 1,033', '拼接：512 + 64 + 64 + 64 + 64 + 9 + 256 = 1,033', '串接：512 + 64 + 64 + 64 + 64 + 9 + 256 = 1,033')),
      tone: 'norm',
      info: L(w('One vector of 1,033 numbers. The parts meet only from here on.', '一个 1,033 个数的向量。各部分从这里才开始相遇。', '一個 1,033 個數的向量。各部分從這裡才開始相遇。')),
    },
    top: [
      {
        kind: 'repeat',
        times: 'MLP',
        info: L(w('The body: every relation between the parts is worked out here. A second head, used only in training, reads its output too: Linear 512 → 6, not in the net file.', '主干：各部分之间的关系都在这里算出来。训练时另有第二个输出头也读它的输出：线性层 512 → 6，不进网络文件。', '主幹：各部分之間的關係都在這裡算出來。訓練時另有第二個輸出頭也讀它的輸出：線性層 512 → 6，不進網路檔案。')),
        items: body(lang, 1033),
      },
      { kind: 'block', label: L(w('Hazard head\nLinear 512 → 64', '风险值输出头\n线性层 512 → 64', '風險值輸出頭\n線性層 512 → 64')), tone: 'head', info: L(w('One logit per floor, 0 to 63.', '每层一个 logit，0 到 63。', '每層一個 logit，0 到 63。')) },
      {
        kind: 'block',
        label: L(w('Sigmoid: 64 h_g', 'Sigmoid：64 个 h_g', 'Sigmoid：64 個 h_g')),
        tone: 'head',
        info: L(w('Per floor g, the chance h_g that the run ends there if still going.', '对每一层 g：还在进行时在该层结束的机率 h_g。', '對每一層 g：還在進行時在該層結束的機率 h_g。')),
      },
    ],
    output: L(w('P(win) = ∏ (1 − h_g), from the current floor on', 'P(win) = ∏ (1 − h_g)，从当前层算起', 'P(win) = ∏ (1 − h_g)，從目前樓層算起')),
    links: [],
  };
}

/** The combat net as its net file's graph has it (docs: combat-brain/network §1). */
export function combatNet(lang: Lang): Diagram {
  const L = (x: W) => x[lang];
  const sum = (info: W): Block => ({ kind: 'block', label: L(w('Sum', '求和', '加總')), tone: 'pool', info: L(info) });
  return {
    columns: [
      {
        title: L(w('Cards', '卡牌', '卡牌')),
        input: L(w('hand · draw · discard ·\nexhaust piles', '手牌 · 抽牌堆 ·\n弃牌堆 · 消耗堆', '手牌 · 抽牌堆 ·\n棄牌堆 · 消耗堆')),
        items: [
          {
            kind: 'block',
            label: L(w('Card embeddings', '卡牌嵌入', '卡牌嵌入')),
            tone: 'embed',
            info: L(
              w(
                'Its id, upgrade level, modifier tags (enchantment, affliction, keywords, Tinker, spent) and amounts, pile, current energy and star costs, damage, block, calculated number, replays and gathered damage; 64 numbers per card.',
                '它的 id、升级等级、修饰标记（附魔、诅咒、关键词、Tinker、已用）和数值、所在牌堆、当前能量与星辰费用、伤害、格挡、计算出的数字、额外打出次数和附魔累积的伤害；每张牌 64 个数。',
                '它的 id、升級等級、修飾標記（附魔、詛咒、關鍵字、Tinker、已用）和數值、所在牌堆、目前能量與星辰費用、傷害、格擋、計算出的數字、額外打出次數和附魔累積的傷害；每張牌 64 個數。',
              ),
            ),
          },
          {
            kind: 'block',
            label: L(w('φ: Linear + ReLU\n64 → 128 → 128', 'φ：线性层 + ReLU\n64 → 128 → 128', 'φ：線性層 + ReLU\n64 → 128 → 128')),
            tone: 'ff',
            info: L(w('The card network, applied to each card on its own.', '卡牌网络，对每张牌单独作用。', '卡牌網路，對每張牌單獨作用。')),
          },
          {
            kind: 'block',
            label: L(w('Sum per pile × 1/8', '每堆求和 × 1/8', '每堆加總 × 1/8')),
            tone: 'pool',
            info: L(w('Each pile’s cards summed, times 1/8: 4 piles × 128 = 512 numbers.', '每一堆的牌相加，乘 1/8：4 堆 × 128 = 512 个数。', '每一堆的牌相加，乘 1/8：4 堆 × 128 = 512 個數。')),
          },
        ],
      },
      {
        title: L(w('Enemies', '敌人', '敵人')),
        input: L(w('id · HP · block · powers ·\nintents now and 3 rounds back', 'id · 生命 · 格挡 · 能力 ·\n现在与前 3 回合的意图', 'id · 生命 · 格擋 · 能力 ·\n現在與前 3 回合的意圖')),
        items: [
          {
            kind: 'block',
            label: L(w('64 + 128 + 4 × 64 = 448', '64 + 128 + 4 × 64 = 448', '64 + 128 + 4 × 64 = 448')),
            tone: 'embed',
            info: L(
              w(
                'Per enemy: its id and numbers, 64; its powers through the powers’ φ and summed, 128; 4 intent slots, its intents now and in its last 3 rounds, 64 each.',
                '每个敌人：id 和数值，64；它的能力经能力的 φ 后求和，128；4 个意图槽，当前和最近 3 回合的意图，各 64。',
                '每個敵人：id 和數值，64；它的能力經能力的 φ 後加總，128；4 個意圖槽，目前和最近 3 回合的意圖，各 64。',
              ),
            ),
          },
          {
            kind: 'block',
            label: L(w('φ: Linear + ReLU\n448 → 256 → 256', 'φ：线性层 + ReLU\n448 → 256 → 256', 'φ：線性層 + ReLU\n448 → 256 → 256')),
            tone: 'ff',
            info: L(w('The enemy network, applied to each enemy on its own.', '敌人网络，对每个敌人单独作用。', '敵人網路，對每個敵人單獨作用。')),
          },
          sum(w('The enemies summed: 256 numbers.', '敌人相加：256 个数。', '敵人相加：256 個數。')),
        ],
      },
      {
        title: L(w('Powers', '能力', '能力')),
        input: L(w('the player’s\npowers', '玩家的\n能力', '玩家的\n能力')),
        narrow: true,
        items: [
          {
            kind: 'block',
            label: L(w('Embeddings', '嵌入', '嵌入')),
            tone: 'embed',
            info: L(w('Id, amount, stacks, moved number and kept or stolen card: 64 numbers per power.', 'id、层数、堆叠数、偏移量和保留或偷走的牌：每个能力 64 个数。', 'id、層數、堆疊數、移動的數字和保留或偷走的牌：每個能力 64 個數。')),
          },
          {
            kind: 'block',
            label: L(w('φ: Linear + ReLU\n64 → 128', 'φ：线性层 + ReLU\n64 → 128', 'φ：線性層 + ReLU\n64 → 128')),
            tone: 'ff',
            info: L(w('The powers’ network; the same weights read each enemy’s powers.', '能力网络；每个敌人的能力也由同一组权重读取。', '能力網路；每個敵人的能力也由同一組權重讀取。')),
          },
          sum(w('The powers summed: 128 numbers.', '能力相加：128 个数。', '能力相加：128 個數。')),
        ],
      },
      {
        title: L(w('Player', '玩家', '玩家')),
        input: L(w('HP · block · energy ·\nstars · round', '生命 · 格挡 · 能量 ·\n星 · 回合', '生命 · 格擋 · 能量 ·\n星辰 · 回合')),
        narrow: true,
        items: [
          {
            kind: 'block',
            label: L(w('Scale: 8', '缩放：8', '縮放：8')),
            tone: 'norm',
            info: L(w('8 numbers, scaled to about one.', '8 个数，缩放到 1 左右。', '8 個數，縮放到 1 左右。')),
          },
        ],
      },
      {
        title: L(w('The rest', '其余', '其餘')),
        input: L(w('relics · potions · encounter ·\norbs · allies · 4 numbers', '遗物 · 药水 · 遭遇 ·\n充能球 · 盟友 · 4 个数', '遺物 · 藥水 · 遭遇 ·\n充能球 · 盟友 · 4 個數')),
        narrow: true,
        items: [
          {
            kind: 'block',
            label: L(w('4 × 64 + 128 + 4', '4 × 64 + 128 + 4', '4 × 64 + 128 + 4')),
            tone: 'embed',
            info: L(
              w(
                'Relics (id, counter, melted and status), potions, the context (the encounter and the character; act and room read as unknown) and orbs (id, passive, evoke and place in the queue), 64 each; allies through their own φ, 128; the ascension, orb slots, gold and potion slots, 4.',
                '遗物（id、计数、熔化和状态）、药水、上下文（遭遇和角色；幕和房间读作未知）和充能球（id、被动值、激发值和队列中的位置），各 64；盟友经自己的 φ，128；进阶、充能球栏位、金币和药水栏位，4。',
                '遺物（id、計數、熔化和狀態）、藥水、上下文（遭遇和角色；幕和房間讀作未知）和充能球（id、被動值、激發值和佇列中的位置），各 64；盟友經自己的 φ，128；晉階、充能球欄位、金幣和藥水欄位，4。',
              ),
            ),
          },
        ],
      },
    ],
    merge: {
      kind: 'block',
      label: L(w('Concatenate: 512 + 256 + 128 + 8 + 388 = 1,292', '拼接：512 + 256 + 128 + 8 + 388 = 1,292', '串接：512 + 256 + 128 + 8 + 388 = 1,292')),
      tone: 'norm',
      info: L(w('One vector of 1,292 numbers.', '一个 1,292 个数的向量。', '一個 1,292 個數的向量。')),
    },
    top: [
      ...body(lang, 1292),
      {
        kind: 'block',
        label: L(w('Endings head\n512 → 51', '结局输出头\n512 → 51', '結局輸出頭\n512 → 51')),
        tone: 'head',
        info: L(
          w(
            'One logit for a death and one for each of 50 bands of the share of max HP still to be lost if the fight is won, from −0.25 to 1.',
            '死亡一个 logit，若赢下战斗、还会失去的最大 HP 比例的 50 个区间（−0.25 到 1）各一个 logit。',
            '死亡一個 logit，若贏下戰鬥、還會失去的最大 HP 比例的 50 個區間（−0.25 到 1）各一個 logit。',
          ),
        ),
      },
      {
        kind: 'block',
        label: L(w('Mask + softmax', '遮罩 + softmax', '遮罩 + softmax')),
        tone: 'norm',
        info: L(
          w(
            'The bands the HP held rules out are closed inside the graph: a win ends with an HP share in (0, 1]. A closed band’s chance is exactly 0.',
            '现有 HP 排除的区间在图里就被关掉：获胜时结束的 HP 比例在 (0, 1] 之间。被关掉的区间机率正好是 0。',
            '現有 HP 排除的區間在圖裡就被關掉：獲勝時結束的 HP 比例在 (0, 1] 之間。被關掉的區間機率正好是 0。',
          ),
        ),
      },
      {
        kind: 'block',
        label: L(w('Potion head\n512 → 5 × 66, sigmoid', '药水输出头\n512 → 5 × 66，sigmoid', '藥水輸出頭\n512 → 5 × 66，sigmoid')),
        tone: 'head',
        info: L(
          w(
            'Beside the endings, from the same 512 numbers: for each of 5 groups of 10 bands in a row, one chance per potion id of being gone after a win that ends in the group. Only the potions held are read.',
            '和结局并列，读同样的 512 个数：50 个区间每 10 个一组，共 5 组，每组每个药水 id 一个机率，表示在这一组结束的胜利之后它已不在。只读现在持有的药水。',
            '和結局並列，讀同樣的 512 個數：50 個區間每 10 個一組，共 5 組，每組每個藥水 id 一個機率，表示在這一組結束的勝利之後它已不在。只讀現在持有的藥水。',
          ),
        ),
      },
    ],
    output: L(w('endings: 51 chances · potions: 5 × 66 chances\n→ handed to V to price', '收场：51 个机率 · 药水：5 × 66 个机率\n→ 交给 V 估价', '收場：51 個機率 · 藥水：5 × 66 個機率\n→ 交給 V 估價')),
    links: [],
  };
}

export const DIAGRAMS = { value: valueNet, combat: combatNet };
