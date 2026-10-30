// The game's own names of the ids the figures show (src/data/names/, from scripts/game-names.mjs), each followed by
// its id in brackets: "Dagger Throw (DAGGER_THROW)", "投擲匕首（DAGGER_THROW）".
export type Lang = 'en' | 'zh-CN' | 'zh-TW';
export type Kind = 'card' | 'relic' | 'potion' | 'enchant' | 'affliction' | 'act' | 'encounter' | 'ancient' | 'character' | 'map';
type Table = Record<Kind, Record<string, string>>;

const loaders: Record<Lang, () => Promise<{ default: Table }>> = {
  en: () => import('../../data/names/en.json'),
  'zh-CN': () => import('../../data/names/zh-CN.json'),
  'zh-TW': () => import('../../data/names/zh-TW.json'),
};

export class Names {
  readonly table: Table;
  readonly lang: Lang;
  constructor(table: Table, lang: Lang) {
    this.table = table;
    this.lang = lang;
  }

  /** The game's name, or the id when the game has none. */
  name(kind: Kind, id: string): string {
    return this.table[kind]?.[id] ?? id;
  }

  /** The id in brackets after a name. */
  bracket(id: string): string {
    return this.lang === 'en' ? ` (${id})` : `（${id}）`;
  }

  /** "Name (ID)". */
  full(kind: Kind, id: string): string {
    const name = this.name(kind, id);
    return name === id ? id : name + this.bracket(id);
  }

  /** A card with its upgrade level: "Dagger Throw+ (DAGGER_THROW)". */
  card(id: string, up = 0): string {
    const plus = up === 0 ? '' : up === 1 ? '+' : `+${up}`;
    const name = this.name('card', id);
    return name === id ? id + plus : name + plus + this.bracket(id);
  }

  /** A token of the net's bags: "relic", "potion", "context" ("act:HIVE") or "map" ("Elite+3"). */
  token(table: string, token: string): string {
    if (table === 'relics') {
      const used = token.endsWith(':used');
      const id = token.replace(/:used$/, '');
      return this.full('relic', id) + (used ? SUFFIX.used[this.lang] : '');
    }
    if (table === 'potions') return this.full('potion', token);
    if (table === 'context') {
      const [kind, id] = token.split(':');
      const k = ({ character: 'character', act: 'act', boss: 'encounter', ancient: 'ancient' } as const)[kind as 'act'];
      return k ? this.full(k, id) : token;
    }
    if (table === 'map') {
      const m = /^(\w+)\+(\d+)(:quest)?$/.exec(token);
      if (!m) return token;
      return `${this.name('map', m[1])} +${m[2]}${m[3] ? SUFFIX.quest[this.lang] : ''}`;
    }
    return token;
  }
}

const SUFFIX: Record<'used' | 'quest', Record<Lang, string>> = {
  used: { en: ', used up', 'zh-CN': '，已用完', 'zh-TW': '，已用完' },
  quest: { en: ', quest', 'zh-CN': '，任务', 'zh-TW': '，任務' },
};

const cache = new Map<Lang, Promise<Names>>();

export function loadNames(lang: Lang): Promise<Names> {
  if (!cache.has(lang)) cache.set(lang, loaders[lang]().then((m) => new Names(m.default, lang)));
  return cache.get(lang)!;
}
