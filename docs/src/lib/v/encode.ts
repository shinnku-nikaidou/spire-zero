// A run view as pure V reads it: a port of learn/v/encode.py, the one spec RunNet follows (docs: macro-brain/network §1).
// Only what the run is and its raw state: each card's id, upgrade level and modifiers' ids; the relics and potions;
// the character, act, boss(es) and ancient; the map points the player can still reach, each as its type and how many
// floors ahead it is; and six numbers. The encoders' net reads the same points as a graph instead (route).

export interface Point {
  col: number;
  row: number;
  type: string;
  next: { col: number; row: number }[];
  quest?: boolean;
}

export interface Card {
  id: string;
  up?: number;
  enchant?: { id: string } | null;
  affliction?: { id: string } | null;
}

export interface RunView {
  character: string;
  ascension?: number;
  act: number;
  act_id: string;
  floor: number;
  hp: number;
  max_hp: number;
  gold: number;
  deck: Card[];
  relics: { id: string; used_up?: boolean }[];
  potions: (string | null)[];
  map: {
    points: Point[];
    visited: { col: number; row: number }[];
    entering?: boolean;
    boss: string;
    second_boss?: string | null;
    ancient?: string | null;
  };
}

export type CardToken = [id: string, up: number, mods: string[]];

export interface Tokens {
  cards: CardToken[];
  relics: string[];
  potions: string[];
  context: string[];
  map: string[];
}

export const MAX_UP = 3; // RunNet.MaxUp: upgrade levels above this read as this one
export const BAGS = ['relics', 'potions', 'context', 'map'] as const;
export const STATE = ['floor', 'hp', 'max_hp', 'gold', 'ascension', 'potion_slots'] as const;

const key = (p: { col: number; row: number }) => `${p.col},${p.row}`;

/** Where the player stands: the last visited point, or the start (lowest row, then column) before any. */
export function here(m: RunView['map']): Point {
  const points = new Map(m.points.map((p) => [key(p), p]));
  if (m.visited.length) return points.get(key(m.visited[m.visited.length - 1]))!;
  return [...m.points].sort((a, b) => a.row - b.row || a.col - b.col)[0];
}

/** The map points a path leads to from where the player is, the player's own point left out, sorted by (row, col). */
export function reachable(m: RunView['map']): Point[] {
  const points = new Map(m.points.map((p) => [key(p), p]));
  const seen = new Set<string>();
  const todo = [here(m)];
  while (todo.length) {
    for (const n of todo.pop()!.next) {
      const k = key(n);
      if (!seen.has(k) && points.has(k)) {
        seen.add(k);
        todo.push(points.get(k)!);
      }
    }
  }
  return [...seen].map((k) => points.get(k)!).sort((a, b) => a.row - b.row || a.col - b.col);
}

/** Each reachable point as its type and how many floors ahead it is ("Elite+3"), ":quest" if a quest marks it. */
export function mapToken(p: Point, row: number): string {
  return `${p.type}+${p.row - row}` + (p.quest ? ':quest' : '');
}

export function mapTokens(m: RunView['map']): string[] {
  const at = here(m);
  return [...(m.entering ? [at] : []), ...reachable(m)].map((p) => mapToken(p, at.row));
}

export const MAX_AHEAD = 16; // floors ahead above this read as this one (an act's map has 16 rows after its start)

export type RouteNode = [type: string, ahead: number, quest: boolean, next: number[]];

/** The route the encoders' net reads instead of the map's bag (encode.route): where the player is, then the reachable
 *  points in reachable's order, each as its type, how many floors ahead of the player it is (at most MAX_AHEAD),
 *  whether a quest marks it, and the indices of the nodes it leads to. */
export function route(m: RunView['map']): RouteNode[] {
  const at = here(m);
  const nodes = [at, ...reachable(m)];
  const index = new Map(nodes.map((p, i) => [key(p), i]));
  return nodes.map((p) => [
    p.type,
    Math.min(p.row - at.row, MAX_AHEAD),
    !!p.quest,
    p.next.flatMap((n) => (index.has(key(n)) ? [index.get(key(n))!] : [])),
  ]);
}

/** The ids the net reads: cards as (id, upgrade level, modifiers), and the bags. */
export function tokens(view: RunView): Tokens {
  const cards: CardToken[] = view.deck.map((c) => {
    const mods = (['enchant', 'affliction'] as const).filter((kind) => c[kind]).map((kind) => `${kind}:${c[kind]!.id}`);
    return [c.id, Math.min(c.up ?? 0, MAX_UP), mods];
  });
  const m = view.map;
  const context = ['character:' + view.character, 'act:' + view.act_id, 'boss:' + m.boss];
  if (m.second_boss) context.push('boss:' + m.second_boss);
  if (m.ancient) context.push('ancient:' + m.ancient);
  return {
    cards,
    relics: view.relics.map((r) => r.id + (r.used_up ? ':used' : '')),
    potions: view.potions.filter((p): p is string => p !== null),
    context,
    map: mapTokens(m),
  };
}

/** The numbers of STATE, raw: the net file standardizes them. */
export function state(view: RunView): number[] {
  return [view.floor, view.hp, view.max_hp, view.gold, view.ascension ?? 0, view.potions.length];
}

/** A view with an afterstate's changes as `sim afterstates` writes them (learn/bosslab.py's merged): the fields that
 * differ, to one level inside an object field; a null removes a field. */
export function merged(view: RunView, after: Record<string, unknown> | null | undefined): RunView {
  const before = view as unknown as Record<string, unknown>;
  const out: Record<string, unknown> = { ...before };
  for (const [k, v] of Object.entries(after ?? {})) {
    const old = before[k];
    if (v === null) delete out[k];
    else if (isObject(v) && isObject(old))
      out[k] = Object.fromEntries(Object.entries({ ...old, ...v }).filter(([, x]) => x !== null));
    else out[k] = v;
  }
  return out as unknown as RunView;
}

const isObject = (x: unknown): x is Record<string, unknown> => typeof x === 'object' && x !== null && !Array.isArray(x);
