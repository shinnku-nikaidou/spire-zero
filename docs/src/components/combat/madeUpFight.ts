// The made-up fight the SL planner's figures search (docs: sl-planner/beam): every turn's ends are drawn from a seed of
// the path to them, so every setting of a figure sees the same fight.
export const MAX_HP = 70;
const START = { h: 60, f: 140 };
export const CHILDREN = 6;
const win = (s: number) => (s >= 0 ? 0.2 + 0.6 * s : 0.2 * Math.exp((0.6 * s) / 0.2));

function rng(seed: string) {
  let h = 2166136261;
  for (let i = 0; i < seed.length; i++) h = Math.imul(h ^ seed.charCodeAt(i), 16777619);
  return () => {
    h = Math.imul(h ^ (h >>> 15), 2246822507);
    h = Math.imul(h ^ (h >>> 13), 3266489909);
    h ^= h >>> 16;
    return (h >>> 0) / 4294967296;
  };
}

export type Node = { id: string; turn: number; h: number; f: number; status: 'going' | 'won' | 'lost'; value: number; bound: number; parent: Node | null };
const node = (id: string, turn: number, h: number, f: number, parent: Node | null): Node => {
  const status = f <= 0 ? 'won' : h <= 0 ? 'lost' : 'going';
  const value = status === 'lost' ? 0 : status === 'won' ? win(h / MAX_HP) : win((h - (9 * f) / 17) / MAX_HP);
  return { id, turn, h, f, status, value, bound: status === 'lost' ? 0 : win(h / MAX_HP), parent };
};

/** A turn's ends from a state: more damage dealt costs more HP. */
export function children(n: Node): Node[] {
  const r = rng(n.id);
  return Array.from({ length: CHILDREN }, (_, j) => {
    const a = (j + r()) / CHILDREN;
    const dealt = Math.round(9 + 17 * a + 6 * (r() - 0.5));
    const f = n.f - dealt;
    const taken = f <= 0 ? 0 : Math.max(0, Math.round(1 + 14 * a + 8 * (r() - 0.5)));
    return node(`${n.id}.${j}`, n.turn + 1, n.h - taken, f, n);
  });
}

export const root = node('r', 0, START.h, START.f, null);
