// The 95% interval of a win rate, as the project's reports compute it, and the textbook one beside it.

export type Rate = { hits: number; of: number; value: number; low: number; high: number };

/** A proportion with its Wilson 95% interval, as Rate.Wilson computes it (z = 1.96, clamped to [0, 1]). */
export function wilson(hits: number, of: number): Rate {
  if (of === 0) return { hits: 0, of: 0, value: 0, low: 0, high: 1 };
  const z = 1.96;
  const p = hits / of;
  const z2 = (z * z) / of;
  const center = (p + z2 / 2) / (1 + z2);
  const half = (z * Math.sqrt((p * (1 - p)) / of + z2 / (4 * of))) / (1 + z2);
  return { hits, of, value: p, low: Math.max(0, center - half), high: Math.min(1, center + half) };
}

/** The normal (Wald) interval p ± 1.96·√(p(1−p)/n), for comparison only: no code of the project uses it for a rate. */
export function wald(hits: number, of: number): { low: number; high: number } {
  if (of === 0) return { low: 0, high: 1 };
  const p = hits / of;
  const half = 1.96 * Math.sqrt((p * (1 - p)) / of);
  return { low: p - half, high: p + half };
}
