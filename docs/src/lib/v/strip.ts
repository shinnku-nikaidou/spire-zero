// A run view stripped to what pure V reads and the figures show (encode.ts's RunView).
import type { RunView } from './encode.ts';

export function strip(v: RunView): RunView {
  const id = (x: { id: string } | null | undefined) => (x ? { id: x.id } : undefined);
  return {
    character: v.character,
    ascension: v.ascension ?? 0,
    act: v.act,
    act_id: v.act_id,
    floor: v.floor,
    hp: v.hp,
    max_hp: v.max_hp,
    gold: v.gold,
    deck: v.deck.map((c) => ({ id: c.id, ...(c.up ? { up: c.up } : {}), ...(c.enchant ? { enchant: id(c.enchant) } : {}), ...(c.affliction ? { affliction: id(c.affliction) } : {}) })),
    relics: v.relics.map((r) => ({ id: r.id, ...(r.used_up ? { used_up: true } : {}) })),
    potions: v.potions,
    map: {
      points: v.map.points.map((p) => ({ col: p.col, row: p.row, type: p.type, next: p.next.map((n) => ({ col: n.col, row: n.row })), ...(p.quest ? { quest: true } : {}) })),
      visited: v.map.visited.map((p) => ({ col: p.col, row: p.row })),
      boss: v.map.boss,
      ...(v.map.second_boss ? { second_boss: v.map.second_boss } : {}),
      ...(v.map.ancient ? { ancient: v.map.ancient } : {}),
    },
  };
}
