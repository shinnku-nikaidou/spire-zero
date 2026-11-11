// The figures' own words, in the site's three languages.
import type { Lang } from './names.ts';

type Words = Record<Lang, string>;
const w = (en: string, cn: string, tw: string): Words => ({ en, 'zh-CN': cn, 'zh-TW': tw });

export const SURFACES: Record<string, Words> = {
  card_reward: w('card reward', '卡牌奖励', '卡牌獎勵'),
  rest: w('rest site', '休息处', '休息處'),
  map: w('map', '地图', '地圖'),
  shop: w('shop', '商店', '商店'),
  rewards: w('rewards', '奖励界面', '獎勵畫面'),
  event: w('event', '事件', '事件'),
  card_choice: w('card choice', '选牌', '選牌'),
  treasure: w('treasure', '宝箱', '財寶房'),
  bundle: w('bundle', '卡包', '卡包'),
};

export const surface = (lang: Lang, s: string) => SURFACES[s]?.[lang] ?? s;

/** Numbers as the page's language writes them. */
export const fmt = (lang: Lang, x: number, digits = 2) =>
  x.toLocaleString(lang, { minimumFractionDigits: digits, maximumFractionDigits: digits });
