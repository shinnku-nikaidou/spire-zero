// The combat chapters' figure words, in the site's three languages, and how a figure writes a combat action.
import type { Lang, Names } from '../v/names.ts';

type Words = Record<Lang, string>;
const w = (en: string, cn: string, tw: string): Words => ({ en, 'zh-CN': cn, 'zh-TW': tw });

const WORDS = {
  round: w('Round', '回合', '回合'),
  hp: w('HP', 'HP', 'HP'),
  block: w('Block', '格挡', '格擋'),
  energy: w('Energy', '能量', '能量'),
  hand: w('Hand', '手牌', '手牌'),
  drawPile: w('Draw pile', '抽牌堆', '抽牌堆'),
  intent: w('Intent', '意图', '意圖'),
  endTurn: w('End turn', '结束回合', '結束回合'),
  attack: w('attack', '攻击', '攻擊'),
  searched: w('searched', '搜索', '搜尋'),
  followed: w('followed the plan', '照计划', '照計畫'),
  value: w('Value', '值', '值'),
  world: w('World', '世界', '世界'),
  worlds: w('worlds', '个世界', '個世界'),
  mean: w('mean', '平均', '平均'),
  ci: w('95% interval', '95% 区间', '95% 區間'),
  firstAction: w('First action', '第一动作', '第一步'),
  played: w('played', '实际打出', '實際打出'),
  reset: w('Reset', '还原', '還原'),
  step: w('Next', '下一步', '下一步'),
  back: w('Back', '上一步', '上一步'),
  start: w('Start over', '重来', '重來'),
  illustrative: w('Illustrative: a made-up position, not from a run.', '示意：虚构的局面，不是来自真实对局。', '示意：虛構的局面，不是來自真實對局。'),
  loading: w('Loading…', '正在加载…', '正在載入…'),
  enemy: w('Enemy', '敌人', '敵人'),
  poison: w('Poison', '中毒', '中毒'),
  strength: w('Strength', '力量', '力量'),
  potions: w('Potions held', '持有药水', '持有藥水'),
  hits: w('hits', '次', '次'),
  turns: w('turns', '回合', '回合'),
  nodes: w('nodes', '节点', '步'),
  death: w('death', '死亡', '死亡'),
} as const;

export type Word = keyof typeof WORDS;
export const words = (lang: Lang) => (key: Word) => WORDS[key][lang];

/** Strings a single figure owns: { en, 'zh-CN', 'zh-TW' }. */
export const say = (lang: Lang, s: Words) => s[lang];
export { w };

/** Numbers as the page's language writes them. */
export const fmt = (lang: Lang, x: number, digits = 2) =>
  x.toLocaleString(lang, { minimumFractionDigits: digits, maximumFractionDigits: digits });

export interface CardRef {
  id: string;
  uid?: number;
  up?: number;
}
export interface Creature {
  uid?: number;
  id: string;
  hp: number;
  max_hp: number;
  block?: number;
  intents?: { type: string; damage?: number; hits?: number }[];
  powers?: { id: string; amount: number }[];
}

// Intent types as the observation names them.
const INTENTS: Record<string, Words> = {
  Attack: w('Attack', '攻击', '攻擊'),
  Defend: w('Defend', '防御', '防禦'),
  Buff: w('Buff', '增益', '增益'),
  Debuff: w('Debuff', '减益', '減益'),
  StatusCard: w('Status card', '状态牌', '狀態牌'),
  Stun: w('Stunned', '眩晕', '暈眩'),
  Sleep: w('Asleep', '睡眠', '睡眠'),
  Heal: w('Heal', '治疗', '治療'),
  Summon: w('Summon', '召唤', '召喚'),
  Escape: w('Escape', '逃跑', '逃跑'),
  Unknown: w('Unknown', '未知', '未知'),
};

/** An intent as the game shows it: "Attack 16", "Attack 4×2", "Buff". */
export function intentText(lang: Lang, i: { type: string; damage?: number; hits?: number }): string {
  const type = INTENTS[i.type]?.[lang] ?? i.type;
  if (i.damage == null) return type;
  return `${type} ${i.damage}${i.hits && i.hits > 1 ? `×${i.hits}` : ''}`;
}

/**
 * A combat action as the decision log writes it ("play:UID", "play:UID:TARGET", "end_turn"), with the card's and the
 * target's names. `cards` maps a card uid to its card, `enemies` a creature uid to its monster; `full` adds the card's id.
 */
export function actionText(lang: Lang, names: Names, action: string, cards: Map<number, CardRef>, enemies: Map<number, Creature>,
  full = true): string {
  if (action === 'end_turn') return WORDS.endTurn[lang];
  const m = /^play:(\d+)(?::(\d+))?$/.exec(action);
  if (!m) return action;
  const card = cards.get(Number(m[1]));
  const plus = card?.up ? (card.up === 1 ? '+' : `+${card.up}`) : '';
  const name = !card ? `#${m[1]}` : full ? names.card(card.id, card.up ?? 0) : names.name('card', card.id) + plus;
  if (m[2] == null) return name;
  const target = enemies.get(Number(m[2]));
  return `${name} → ${target ? names.name('monster', target.id) : `#${m[2]}`}`;
}
