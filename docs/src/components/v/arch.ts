// A network drawn as stacked blocks, in the manner of the Transformer paper's figure 1 (Vaswani et al., 2017):
// columns of blocks from the inputs up, frames for repeated blocks, ⊕ for residual sums and side links for skip
// connections; the columns join in one block, then one trunk goes up to the output. `layout` places it all;
// ArchDiagram.svelte draws it. The two nets of the macro brain chapter (docs: macro-brain/network) and the combat net
// (docs: combat-brain/leaf) are at the end.
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
  norm: w('Norm', '归一化', '正規化'),
  normInfo: w(
    'Layer normalization: each element’s 64 numbers shifted and scaled to mean 0 and variance 1.',
    '层归一化：把每个元素的 64 个数平移、缩放到均值 0、方差 1。',
    '層正規化：把每個元素的 64 個數平移、縮放到平均 0、變異數 1。',
  ),
  add: w('Residual: the block’s input added back to its output.', '残差：把这一块的输入加回到它的输出上。', '殘差：把這一塊的輸入加回到它的輸出上。'),
  numbersTitle: w('Six numbers', '六个数字', '六個數字'),
  numbersInput: w('floor · HP · max HP ·\ngold · ascension ·\npotion slots', '楼层 · 生命 · 最大生命 ·\n金币 · 进阶 ·\n药水栏', '樓層 · 生命 · 最大生命 ·\n金幣 · 晉階 ·\n藥水欄'),
  standardize: w('Standardize', '标准化', '標準化'),
  standardizeInfo: w(
    '(x − μ)/σ, with the mean and standard deviation of the training data that the net file keeps: 6 numbers.',
    '(x − μ)/σ，用网络文件里存的训练数据均值和标准差：6 个数。',
    '(x − μ)/σ，用網路檔案裡存的訓練資料平均和標準差：6 個數。',
  ),
  body1: (n: number) => w(`Linear + ReLU\n${n} → 256`, `线性层 + ReLU\n${n} → 256`, `線性層 + ReLU\n${n} → 256`),
  body1Info: w('The body’s first layer: 256 numbers.', '主干第一层：256 个数。', '主幹第一層：256 個數。'),
  body2: w('Linear + ReLU\n256 → 256', '线性层 + ReLU\n256 → 256', '線性層 + ReLU\n256 → 256'),
  body2Info: w('The body’s second layer: 256 numbers, which the heads read.', '主干第二层：256 个数，输出头读它。', '主幹第二層：256 個數，輸出頭讀它。'),
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

export function pureV(lang: Lang): Diagram {
  const L = (x: W) => x[lang];
  return {
    columns: [
      {
        title: L(w('Deck', '牌组', '牌組')),
        input: L(w('cards: id, upgrade,\nmodifiers', '卡牌：id、升级、\n修饰', '卡牌：id、升級、\n修飾')),
        items: [
          {
            kind: 'block',
            label: L(w('Card embeddings', '卡牌嵌入', '卡牌嵌入')),
            tone: 'embed',
            info: L(w('x = E[c] + u·U[c] + Σ M[m]: 32 numbers per card.', 'x = E[c] + u·U[c] + Σ M[m]：每张牌 32 个数。', 'x = E[c] + u·U[c] + Σ M[m]：每張牌 32 個數。')),
          },
          {
            kind: 'block',
            label: L(w('φ: Linear + ReLU\n32 → 128 → 128', 'φ：线性层 + ReLU\n32 → 128 → 128', 'φ：線性層 + ReLU\n32 → 128 → 128')),
            tone: 'ff',
            info: L(w('The card network, applied to each card on its own.', '卡牌网络，对每张牌单独作用。', '卡牌網路，對每張牌單獨作用。')),
          },
          {
            kind: 'block',
            label: L(w('Mean + sum ÷ 16', '均值 + 和 ÷ 16', '平均 + 總和 ÷ 16')),
            tone: 'pool',
            info: L(w('The deck pooled two ways: 128 + 128 = 256 numbers.', '牌组用两种方式汇总：128 + 128 = 256 个数。', '牌組用兩種方式匯總：128 + 128 = 256 個數。')),
          },
        ],
      },
      {
        title: L(w('Bags', '词袋', '詞元袋')),
        input: L(w('relics · potions ·\ncontext · map', '遗物 · 药水 ·\n上下文 · 地图', '遺物 · 藥水 ·\n上下文 · 地圖')),
        items: [
          {
            kind: 'block',
            label: L(w('Embeddings', '嵌入', '嵌入')),
            tone: 'embed',
            info: L(w('One 32-number vector per token, a table per bag.', '每个词元一个 32 个数的向量，每个袋一张表。', '每個詞元一個 32 個數的向量，每個袋一張表。')),
          },
          {
            kind: 'block',
            label: L(w('Sum per bag', '每个袋求和', '每個袋加總')),
            tone: 'pool',
            info: L(w('Each bag’s vectors summed: 4 × 32 = 128 numbers.', '每个袋的向量相加：4 × 32 = 128 个数。', '每個袋的向量相加：4 × 32 = 128 個數。')),
          },
        ],
      },
      numbers(lang),
    ],
    merge: {
      kind: 'block',
      label: L(w('Concatenate: 256 + 128 + 6 = 390', '拼接：256 + 128 + 6 = 390', '串接：256 + 128 + 6 = 390')),
      tone: 'norm',
      info: L(w('One vector z of 390 numbers.', '一个 390 个数的向量 z。', '一個 390 個數的向量 z。')),
    },
    top: [
      ...body(lang, 390),
      {
        kind: 'block',
        label: L(w('Hazard + share heads\n256 → 64, σ', '风险值 + 比例输出头\n256 → 64，σ', '風險值 + 比例輸出頭\n256 → 64，σ')),
        tone: 'head',
        info: L(
          w(
            'Per floor 0–63, the chance the run ends there if still going, and the share of the enemies’ HP taken there.',
            '对 0–63 的每一层：还在进行时在该层结束的机率，以及在那里打掉的敌人生命比例。',
            '對 0–63 的每一層：還在進行時在該層結束的機率，以及在那裡打掉的敵人生命比例。',
          ),
        ),
      },
    ],
    output: L(w('hazard and share per floor\n→ expected progress', '每层的风险值和比例\n→ 期望进度', '每層的風險值和比例\n→ 期望進度')),
    links: [],
  };
}

export function encodersNet(lang: Lang): Diagram {
  const L = (x: W) => x[lang];
  const norm = (id?: string): Block => ({ kind: 'block', id, label: L(T.norm), tone: 'norm', info: L(T.normInfo) });
  return {
    columns: [
      {
        title: L(w('Build encoder', '构筑编码器', '構築編碼器')),
        input: L(w('cards · relics ·\npotions: one set', '卡牌 · 遗物 ·\n药水：一个集合', '卡牌 · 遺物 ·\n藥水：一個集合')),
        items: [
          {
            kind: 'block',
            label: L(w('Embeddings\n+ kind vectors', '嵌入\n+ 种类向量', '嵌入\n+ 種類向量')),
            tone: 'embed',
            info: L(
              w(
                'Each card, relic and potion is one element of 64 numbers: E[c] + u·U[c] + Σ M[m] + v_card, R[r] + v_relic, P[p] + v_potion.',
                '每张牌、每件遗物、每瓶药水都是一个 64 个数的元素：E[c] + u·U[c] + Σ M[m] + v_card、R[r] + v_relic、P[p] + v_potion。',
                '每張牌、每件遺物、每瓶藥水都是一個 64 個數的元素：E[c] + u·U[c] + Σ M[m] + v_card、R[r] + v_relic、P[p] + v_potion。',
              ),
            ),
          },
          {
            kind: 'repeat',
            times: '×2',
            info: L(w('Two blocks, each with its own weights.', '两个块，各有自己的权重。', '兩個區塊，各有自己的權重。')),
            items: [
              norm('b-n1'),
              {
                kind: 'block',
                label: L(w('Masked Multi-Head\nAttention', '带遮罩的\n多头注意力', '遮罩\n多頭注意力')),
                tone: 'attn',
                info: L(
                  w(
                    '4 heads of 16 numbers: each element takes in a weighted mix of every present element; padding is masked out.',
                    '4 个头，每个 16 个数：每个元素按权重混入所有存在的元素；补齐的位置被遮掉。',
                    '4 個頭，每個 16 個數：每個元素按權重混入所有存在的元素；補齊的位置被遮掉。',
                  ),
                ),
              },
              { kind: 'add', id: 'b-a1', info: L(T.add) },
              norm('b-n2'),
              {
                kind: 'block',
                label: L(w('Feed Forward', '前馈', '前饋')),
                tone: 'ff',
                info: L(w('64 → 128 → 64 with ReLU, each element on its own.', '64 → 128 → 64，ReLU，对每个元素单独作用。', '64 → 128 → 64，ReLU，對每個元素單獨作用。')),
              },
              { kind: 'add', id: 'b-a2', info: L(T.add) },
            ],
          },
          norm(),
          {
            kind: 'block',
            label: L(w('Mean + sum ÷ 16', '均值 + 和 ÷ 16', '平均 + 總和 ÷ 16')),
            tone: 'pool',
            info: L(w('The set pooled two ways, padding left out: 64 + 64 = 128 numbers.', '集合用两种方式汇总（不含补齐位置）：64 + 64 = 128 个数。', '集合用兩種方式匯總（不含補齊位置）：64 + 64 = 128 個數。')),
          },
        ],
      },
      {
        title: L(w('Route encoder', '路线编码器', '路線編碼器')),
        input: L(w('player’s point ·\nreachable points', '玩家所在的点 ·\n可到达的点', '玩家所在的點 ·\n可到達的點')),
        items: [
          {
            kind: 'block',
            id: 'r-s',
            label: L(w('Type + floors ahead\n+ quest: start s', '类型 + 前方层数\n+ 任务：起点 s', '類型 + 前方層數\n+ 任務：起點 s')),
            tone: 'embed',
            info: L(w('s_i = T[type] + A[ahead] + q·v_quest: 32 numbers per node.', 's_i = T[类型] + A[前方层数] + q·v_quest：每个节点 32 个数。', 's_i = T[類型] + A[前方層數] + q·v_quest：每個節點 32 個數。')),
          },
          {
            kind: 'repeat',
            times: '×16',
            info: L(w('16 rounds with the same weights: one per row of an act’s map.', '16 轮，共用同一组权重：一幕地图的每一行一轮。', '16 輪，共用同一組權重：一幕地圖的每一列一輪。')),
            items: [
              {
                kind: 'block',
                label: L(w('Max · mean over\nnext nodes', '对下一步节点\n取最大值 · 均值', '對下一步節點\n取最大值 · 平均')),
                tone: 'msg',
                info: L(
                  w(
                    'For each node, the number-by-number max and the mean of the vectors of the nodes it leads to (0 for the boss).',
                    '对每个节点，取它通向的节点的向量的逐数最大值和均值（Boss 为 0）。',
                    '對每個節點，取它通往的節點的向量的逐數最大值和平均（頭目為 0）。',
                  ),
                ),
              },
              {
                kind: 'block',
                id: 'r-u',
                label: L(w('[s ; max ; mean]\nLinear + ReLU', '[s ; 最大 ; 均值]\n线性层 + ReLU', '[s ; 最大 ; 平均]\n線性層 + ReLU')),
                tone: 'linear',
                info: L(w('h_i = ReLU(W[s_i ; max ; mean] + b): 96 → 32, the same W every round.', 'h_i = ReLU(W[s_i ; 最大 ; 均值] + b)：96 → 32，每轮同一个 W。', 'h_i = ReLU(W[s_i ; 最大 ; 平均] + b)：96 → 32，每輪同一個 W。')),
              },
            ],
          },
          {
            kind: 'block',
            label: L(w('Player’s node\n+ mean of all', '玩家的节点\n+ 全部的均值', '玩家的節點\n+ 全部的平均')),
            tone: 'pool',
            info: L(w('h_0 and the mean over all nodes: 32 + 32 = 64 numbers.', 'h_0 和所有节点的均值：32 + 32 = 64 个数。', 'h_0 和所有節點的平均：32 + 32 = 64 個數。')),
          },
        ],
      },
      {
        title: L(w('Context', '上下文', '上下文')),
        narrow: true,
        input: L(w('character · act ·\nbosses · ancient', '角色 · 幕 ·\nBoss · 先古之民', '角色 · 幕 ·\n頭目 · 遠古之民')),
        items: [
          {
            kind: 'block',
            label: L(w('Embeddings', '嵌入', '嵌入')),
            tone: 'embed',
            info: L(w('One 64-number vector per token.', '每个词元一个 64 个数的向量。', '每個詞元一個 64 個數的向量。')),
          },
          {
            kind: 'block',
            label: L(w('Sum', '求和', '加總')),
            tone: 'pool',
            info: L(w('The bag’s vectors summed: 64 numbers.', '袋里的向量相加：64 个数。', '袋裡的向量相加：64 個數。')),
          },
        ],
      },
      numbers(lang),
    ],
    merge: {
      kind: 'block',
      label: L(w('Concatenate: 128 + 64 + 64 + 6 = 262', '拼接：128 + 64 + 64 + 6 = 262', '串接：128 + 64 + 64 + 6 = 262')),
      tone: 'norm',
      info: L(w('One vector of 262 numbers.', '一个 262 个数的向量。', '一個 262 個數的向量。')),
    },
    top: [
      ...body(lang, 262),
      {
        kind: 'block',
        label: L(w('Hazard head\n256 → 64, σ', '风险值输出头\n256 → 64，σ', '風險值輸出頭\n256 → 64，σ')),
        tone: 'head',
        info: L(
          w(
            'Per floor 0–63, the chance the run ends there if still going; with the objective win, the value is P(win) = S₆₃.',
            '对 0–63 的每一层：还在进行时在该层结束的机率；目标为 win 时，价值是 P(win) = S₆₃。',
            '對 0–63 的每一層：還在進行時在該層結束的機率；目標為 win 時，價值是 P(win) = S₆₃。',
          ),
        ),
      },
    ],
    output: L(w('hazard per floor → P(win)', '每层的风险值 → P(win)', '每層的風險值 → P(win)')),
    links: [
      { from: 'b-n1', at: 'below', to: 'b-a1', side: 'left' },
      { from: 'b-n2', at: 'below', to: 'b-a2', side: 'left' },
      { from: 'r-s', at: 'above', to: 'r-u', side: 'right' },
    ],
  };
}

/** The combat net c2 as its net file's graph has it (docs: combat-brain/leaf §3). */
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
                'x = E[c] + u·U[c] + cost·v_cost + Σ M[m] + P[pile]: its id, upgrade level, cost, enchantment and affliction, and pile; 32 numbers per card.',
                'x = E[c] + u·U[c] + 费用·v_cost + Σ M[m] + P[牌堆]：它的 id、升级等级、费用、附魔与诅咒、所在牌堆；每张牌 32 个数。',
                'x = E[c] + u·U[c] + 費用·v_cost + Σ M[m] + P[牌堆]：它的 id、升級等級、費用、附魔與詛咒、所在牌堆；每張牌 32 個數。',
              ),
            ),
          },
          {
            kind: 'block',
            label: L(w('φ: Linear + ReLU\n32 → 64 → 64', 'φ：线性层 + ReLU\n32 → 64 → 64', 'φ：線性層 + ReLU\n32 → 64 → 64')),
            tone: 'ff',
            info: L(w('The card network, applied to each card on its own.', '卡牌网络，对每张牌单独作用。', '卡牌網路，對每張牌單獨作用。')),
          },
          {
            kind: 'block',
            label: L(w('Sum per pile × 1/8', '每堆求和 × 1/8', '每堆加總 × 1/8')),
            tone: 'pool',
            info: L(w('Each pile’s cards summed, times 1/8: 4 piles × 64 = 256 numbers.', '每一堆的牌相加，乘 1/8：4 堆 × 64 = 256 个数。', '每一堆的牌相加，乘 1/8：4 堆 × 64 = 256 個數。')),
          },
        ],
      },
      {
        title: L(w('Enemies', '敌人', '敵人')),
        input: L(w('id · HP · block · powers ·\nintents now and 3 rounds back', 'id · 生命 · 格挡 · 能力 ·\n现在与前 3 回合的意图', 'id · 生命 · 格擋 · 能力 ·\n現在與前 3 回合的意圖')),
        items: [
          {
            kind: 'block',
            label: L(w('Id + numbers, powers,\n4 intent slots: 224', 'id + 数值、能力、\n4 个意图槽：224', 'id + 數值、能力、\n4 個意圖槽：224')),
            tone: 'embed',
            info: L(
              w(
                'Per living enemy: E[e] + W·(HP share, HP / 100, max HP / 100, block / 20, rounds shown / 10) + b, 32 numbers; its powers through the powers’ φ and summed, 64; 4 intent slots (now, then the last 3 rounds), each Σ (I[t] + d·D[t] + s·T[t]) + S[k] over its intents, d = damage / 10, s = damage × hits / 20, 4 × 32 = 128.',
                '每个活着的敌人：E[e] + W·(生命比例, 生命 / 100, 最大生命 / 100, 格挡 / 20, 已显示回合数 / 10) + b，32 个数；它的能力经能力的 φ 后求和，64 个；4 个意图槽（现在，再往前 3 回合），每个是 Σ (I[t] + d·D[t] + s·T[t]) + S[k]，对槽里的意图求和，d = 伤害 / 10，s = 伤害 × 次数 / 20，4 × 32 = 128。',
                '每個活著的敵人：E[e] + W·(生命比例, 生命 / 100, 最大生命 / 100, 格擋 / 20, 已顯示回合數 / 10) + b，32 個數；它的能力經能力的 φ 後加總，64 個；4 個意圖槽（現在，再往前 3 回合），每個是 Σ (I[t] + d·D[t] + s·T[t]) + S[k]，對槽裡的意圖加總，d = 傷害 / 10，s = 傷害 × 次數 / 20，4 × 32 = 128。',
              ),
            ),
          },
          {
            kind: 'block',
            label: L(w('φ: Linear + ReLU\n224 → 128 → 128', 'φ：线性层 + ReLU\n224 → 128 → 128', 'φ：線性層 + ReLU\n224 → 128 → 128')),
            tone: 'ff',
            info: L(w('The enemy network, applied to each enemy on its own.', '敌人网络，对每个敌人单独作用。', '敵人網路，對每個敵人單獨作用。')),
          },
          sum(w('The enemies summed: 128 numbers.', '敌人相加：128 个数。', '敵人相加：128 個數。')),
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
            info: L(w('P[p] + a·A[p], a = amount / 10: 32 numbers per power.', 'P[p] + a·A[p]，a = 层数 / 10：每个能力 32 个数。', 'P[p] + a·A[p]，a = 層數 / 10：每個能力 32 個數。')),
          },
          {
            kind: 'block',
            label: L(w('φ: Linear + ReLU\n32 → 64', 'φ：线性层 + ReLU\n32 → 64', 'φ：線性層 + ReLU\n32 → 64')),
            tone: 'ff',
            info: L(w('The powers’ network; the same weights read each enemy’s powers.', '能力网络；每个敌人的能力也由同一组权重读取。', '能力網路；每個敵人的能力也由同一組權重讀取。')),
          },
          sum(w('The powers summed: 64 numbers.', '能力相加：64 个数。', '能力相加：64 個數。')),
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
            info: L(
              w(
                'HP share, HP / 100, max HP / 100, block / 20, energy / 3, max energy / 3, stars / 5, round / 10, each kept within ±10: 8 numbers.',
                '生命比例、生命 / 100、最大生命 / 100、格挡 / 20、能量 / 3、最大能量 / 3、星 / 5、回合 / 10，各自限制在 ±10 内：8 个数。',
                '生命比例、生命 / 100、最大生命 / 100、格擋 / 20、能量 / 3、最大能量 / 3、星辰 / 5、回合 / 10，各自限制在 ±10 內：8 個數。',
              ),
            ),
          },
        ],
      },
      {
        title: L(w('Bags', '词袋', '詞元袋')),
        input: L(w('relics · potions ·\ncontext', '遗物 · 药水 ·\n上下文', '遺物 · 藥水 ·\n上下文')),
        narrow: true,
        items: [
          {
            kind: 'block',
            label: L(w('Embeddings', '嵌入', '嵌入')),
            tone: 'embed',
            info: L(
              w(
                'One 32-number vector per token, a table per bag; the context is the character, encounter, room and act.',
                '每个词元一个 32 个数的向量，每个袋一张表；上下文是角色、遭遇、房间和幕。',
                '每個詞元一個 32 個數的向量，每個袋一張表；上下文是角色、遭遇、房間和幕。',
              ),
            ),
          },
          {
            kind: 'block',
            label: L(w('Sum per bag', '每个袋求和', '每個袋加總')),
            tone: 'pool',
            info: L(w('Each bag’s vectors summed: 3 × 32 = 96 numbers.', '每个袋的向量相加：3 × 32 = 96 个数。', '每個袋的向量相加：3 × 32 = 96 個數。')),
          },
        ],
      },
    ],
    merge: {
      kind: 'block',
      label: L(w('Concatenate: 256 + 128 + 64 + 8 + 96 = 552', '拼接：256 + 128 + 64 + 8 + 96 = 552', '串接：256 + 128 + 64 + 8 + 96 = 552')),
      tone: 'norm',
      info: L(w('One vector of 552 numbers.', '一个 552 个数的向量。', '一個 552 個數的向量。')),
    },
    top: [
      ...body(lang, 552),
      {
        kind: 'block',
        label: L(w('Outcome head\n256 → 51, softmax', '结局输出头\n256 → 51，softmax', '結局輸出頭\n256 → 51，softmax')),
        tone: 'head',
        info: L(
          w(
            'The chance of a death and of each of 50 buckets of the share of max HP still to be lost if the fight is won, from −0.25 to 1. A second head, 256 → 1, gives the potions still to be used after a win.',
            '死亡的机率，以及若赢下战斗、还会失去的最大生命比例落在 50 个区间（−0.25 到 1）里每一个的机率。第二个输出头 256 → 1 给出获胜后还会用掉的药水数。',
            '死亡的機率，以及若贏下戰鬥、還會失去的最大生命比例落在 50 個區間（−0.25 到 1）裡每一個的機率。第二個輸出頭 256 → 1 給出獲勝後還會用掉的藥水數。',
          ),
        ),
      },
    ],
    output: L(w('P(death) · 50 buckets of HP still to lose\n→ the leaf’s value', 'P(死亡) · 还会失去的生命的 50 个区间\n→ 叶子估值', 'P(死亡) · 還會失去的生命的 50 個區間\n→ 葉節點估值')),
    links: [],
  };
}

export const DIAGRAMS = { pure: pureV, encoders: encodersNet, combat: combatNet };
