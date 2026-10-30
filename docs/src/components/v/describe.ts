// What an option of a logged decision does, read from its afterstate's changes (`sim afterstates`), in words.
import type { Card, RunView } from '../../lib/v/encode.ts';
import type { Lang, Names } from './names.ts';

const cardKey = (c: Card) => `${c.id}+${c.up ?? 0}+${c.enchant?.id ?? ''}+${c.affliction?.id ?? ''}`;

/** The cards of a less those of b, each counted once per copy. */
function minus(a: Card[], b: Card[]): Card[] {
  const left = new Map<string, number>();
  for (const c of b) left.set(cardKey(c), (left.get(cardKey(c)) ?? 0) + 1);
  const out: Card[] = [];
  for (const c of a) {
    const k = cardKey(c);
    if ((left.get(k) ?? 0) > 0) left.set(k, left.get(k)! - 1);
    else out.push(c);
  }
  return out;
}

const T = {
  take: { en: 'take', 'zh-CN': '拿', 'zh-TW': '拿' },
  skip: { en: 'skip', 'zh-CN': '略过', 'zh-TW': '略過' },
  heal: { en: 'rest', 'zh-CN': '休息', 'zh-TW': '休息' },
  smith: { en: 'smith', 'zh-CN': '锻造', 'zh-TW': '鍛造' },
  go: { en: 'go to', 'zh-CN': '前往', 'zh-TW': '前往' },
  buy: { en: 'buy', 'zh-CN': '买', 'zh-TW': '買' },
  removeCard: { en: 'remove', 'zh-CN': '删除', 'zh-TW': '刪除' },
  leave: { en: 'leave', 'zh-CN': '离开', 'zh-TW': '離開' },
  gold: { en: 'gold', 'zh-CN': '金币', 'zh-TW': '金幣' },
  row: { en: 'row', 'zh-CN': '第', 'zh-TW': '第' },
} as const;

/** One line per option: what it does, from the view before it and after it. */
export function describe(lang: Lang, names: Names, action: string, before: RunView, after: RunView): string {
  const t = (k: keyof typeof T) => T[k][lang];
  const added = minus(after.deck, before.deck);
  const gone = minus(before.deck, after.deck);
  const goldDelta = after.gold - before.gold;
  const gold = goldDelta ? ` (${goldDelta > 0 ? '+' : '−'}${Math.abs(goldDelta)} ${t('gold')})` : '';
  const sep = lang === 'en' ? ' ' : '';
  const cards = (cs: Card[]) => cs.map((c) => names.card(c.id, c.up ?? 0)).join(', ');
  const [verb, arg] = action.split(':');
  if (verb === 'card') return t('take') + sep + cards(added);
  if (verb === 'card_alt') return t('skip');
  if (verb === 'rest' && arg === 'HEAL') return `${t('heal')} (HP ${before.hp} → ${after.hp})`;
  if (verb === 'rest' && arg === 'SMITH') return `${t('smith')}: ${cards(gone)} → ${cards(added)}`;
  if (verb === 'map') {
    const [col, row] = arg.split(',').map(Number);
    const p = before.map.points.find((q) => q.col === col && q.row === row);
    const where = lang === 'en' ? `${t('row')} ${row}` : `${t('row')} ${row} 行`;
    return `${t('go')}${sep}${p ? names.name('map', p.type) : arg} (${where})`;
  }
  if (verb === 'buy') {
    const relics = after.relics.slice(before.relics.length).map((r) => names.full('relic', r.id));
    const potions = after.potions.filter((p, i) => p && before.potions[i] !== p).map((p) => names.full('potion', p!));
    return t('buy') + sep + [cards(added), ...relics, ...potions].filter(Boolean).join(', ') + gold;
  }
  if (verb === 'remove') return t('removeCard') + sep + cards(gone) + gold;
  if (verb === 'leave') return t('leave');
  return action;
}
