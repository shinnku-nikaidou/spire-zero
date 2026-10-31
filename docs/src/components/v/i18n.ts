// The figures' own words, in the site's three languages.
import type { Lang } from './names.ts';

type Words = Record<Lang, string>;
const w = (en: string, cn: string, tw: string): Words => ({ en, 'zh-CN': cn, 'zh-TW': tw });

const WORDS = {
  loading: w('Loading V’s weights (1 MB)…', '正在加载 V 的权重（1 MB）…', '正在載入 V 的權重（1 MB）…'),
  state: w('State', '局面', '局面'),
  floor: w('Floor', '楼层', '樓層'),
  act: w('Act', '幕', '幕'),
  hp: w('HP', 'HP', 'HP'),
  maxHp: w('Max HP', '最大 HP', '最大 HP'),
  gold: w('Gold', '金币', '金幣'),
  ascension: w('Ascension', '进阶', '晉階'),
  potionSlots: w('Potion slots', '药水栏', '藥水欄'),
  deck: w('Deck', '牌组', '牌組'),
  relics: w('Relics', '遗物', '遺物'),
  potions: w('Potions', '药水', '藥水'),
  context: w('Context', '上下文', '上下文'),
  map: w('Map ahead', '前方地图', '前方地圖'),
  numbers: w('Numbers', '数字', '數字'),
  none: w('none', '无', '無'),
  value: w('V: expected progress', 'V：期望进度', 'V：期望進度'),
  progress: w('Expected progress', '期望进度', '期望進度'),
  expectedFloor: w('Expected floor', '期望楼层', '期望樓層'),
  pWin: w('P(win)', 'P(win)', 'P(win)'),
  chosen: w('played', '实际选择', '實際選擇'),
  best: w('V’s best', 'V 的最佳', 'V 的最佳'),
  reset: w('Reset', '还原', '還原'),
  remove: w('remove', '删除', '刪除'),
  upgrade: w('upgrade', '升级', '升級'),
  addCard: w('Add a card…', '加一张牌…', '加一張牌…'),
  search: w('search', '搜索', '搜尋'),
  floors: w('floors', '层', '層'),
  raw: w('raw', '原值', '原值'),
  scaled: w('standardized', '标准化', '標準化'),
  tokens: w('Tokens the net reads', '网络读到的 token', '網路讀到的 token'),
  unknown: w('read as unknown', '当作未知 id', '當作未知 id'),
  heldOut: w('Held-out state', 'held-out 局面', 'held-out 局面'),
  wonRun: w('Won run', '通关的一局', '通關的一局'),
  here: w('you are here', '现在的位置', '現在的位置'),
  visited: w('visited', '走过', '走過'),
  unreachable: w('not reachable', '走不到', '走不到'),
  step: w('Step', '下一步', '下一步'),
  back: w('Back', '上一步', '上一步'),
  start: w('Start over', '重来', '重來'),
  illustrative: w('Illustrative values, not from a run.', '示意数值，不是来自真实对局。', '示意數值，不是來自真實對局。'),
  without: w('V without it', '拿掉后的 V', '拿掉後的 V'),
  gain: w('what it adds to V', '它让 V 多出多少', '它讓 V 多出多少'),
  belowBest: w('floors below the best option', '比最佳选项少几层', '比最佳選項少幾層'),
  hazard: w('Hazard: chance the run ends on the floor, if still going there', '风险值：活到该层时在该层结束的机率', '風險值：活到該層時在該層結束的機率'),
  survive: w('Chance to be alive after the floor', '活过该层的机率', '活過該層的機率'),
  endChance: w('Chance the run ends on the floor', '对局在该层结束的机率', '對局在該層結束的機率'),
  share: w('Share of the enemies’ HP taken if the run ends there', '若在该层结束，打掉敌人血量的比例', '若在該層結束，打掉敵人血量的比例'),
  bossFloor: w('boss', 'Boss', '頭目'),
  died: w('died on floor e', '死在第 e 层', '死在第 e 層'),
  won: w('won', '通关', '通關'),
  censored: w('stopped (censored) before floor e', '在第 e 层前停止（截断）', '在第 e 層前停止（截斷）'),
  decisionFloor: w('decision’s floor s', '决策所在楼层 s', '決策所在樓層 s'),
  endFloor: w('floor e', '楼层 e', '樓層 e'),
  label: w('label', '标签', '標籤'),
  noLabel: w('no label', '没有标签', '沒有標籤'),
  lossTerm: w('loss term', '损失项', '損失項'),
  lossTotal: w('Hazard loss of this sample', '这个样本的风险值损失', '這個樣本的風險值損失'),
  survived: w('survived', '活过', '活過'),
  diedHere: w('died here', '死在这里', '死在這裡'),
  decision: w('decision', '决策', '決策'),
  layer: w('layer', '层', '層'),
  card: w('card', '牌', '牌'),
  mean: w('mean', '平均', '平均'),
  sum: w('sum × 1/16', '总和 × 1/16', '總和 × 1/16'),
  body: w('body', '身体', '身體'),
  heads: w('heads', '输出头', '輸出頭'),
} as const;

export type Word = keyof typeof WORDS;

export const words = (lang: Lang) => (key: Word) => WORDS[key][lang];

export const SURFACES: Record<string, Words> = {
  card_reward: w('card reward', '卡牌奖励', '卡牌獎勵'),
  rest: w('rest site', '休息处', '休息處'),
  map: w('map', '地图', '地圖'),
  shop: w('shop', '商店', '商店'),
  rewards: w('rewards', '奖励界面', '獎勵畫面'),
  event: w('event', '事件', '事件'),
  card_choice: w('card prompt', '卡牌提示', '卡牌提示'),
  treasure: w('treasure', '宝箱', '財寶房'),
  bundle: w('bundle', '卡包', '卡包'),
};

export const surface = (lang: Lang, s: string) => SURFACES[s]?.[lang] ?? s;

/** Numbers as the page's language writes them. */
export const fmt = (lang: Lang, x: number, digits = 2) =>
  x.toLocaleString(lang, { minimumFractionDigits: digits, maximumFractionDigits: digits });
