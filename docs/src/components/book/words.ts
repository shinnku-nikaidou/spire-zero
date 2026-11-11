// The book chapters' figure words (environments, simulator, protocol, …), in the site's three languages.
import type { Lang } from '../v/names.ts';

export type Words = Record<Lang, string>;
export const w = (en: string, cn: string, tw: string): Words => ({ en, 'zh-CN': cn, 'zh-TW': tw });

const WORDS = {
  illustrative: w('Illustrative values, not from a run.', '示意数值，不是来自真实对局。', '示意數值，不是來自真實對局。'),
  floor: w('Floor', '楼层', '樓層'),
  node: w('Node', '节点', '房間'),
  step: w('Step', '步骤', '步驟'),
  next: w('Next', '下一步', '下一步'),
  back: w('Back', '上一步', '上一步'),
  again: w('Draw again', '重新抽', '重新抽'),
  worlds: w('worlds', '个世界', '個世界'),
  world: w('World', '世界', '世界'),
  hand: w('Hand', '手牌', '手牌'),
  drawPile: w('Draw pile', '抽牌堆', '抽牌堆'),
  known: w('known', '知道', '知道'),
  partly: w('partly', '部分', '部分'),
  unknown: w('not known', '不知道', '不知道'),
  never: w('never', '永远不', '永遠不'),
  gameSave: w('game save', '游戏存档', '遊戲存檔'),
  nodeSpan: w('the node: arrival → travel', '节点：抵达 → 移动', '房間：抵達 → 移動'),
  value: w('Value', '价值', '價值'),
  pimc: w('PIMC (mean over worlds)', 'PIMC（各世界平均）', '各世界平均'),
  truth: w('Real value (choose before the draw)', '真实价值（抽牌前就得选）', '真實價值（抽牌前就得選）'),
  pick: w('picks', '选', '選'),
} as const;

export type Word = keyof typeof WORDS;
export const words = (lang: Lang) => (key: Word) => WORDS[key][lang];
export const say = (lang: Lang, r: Words) => r[lang];
