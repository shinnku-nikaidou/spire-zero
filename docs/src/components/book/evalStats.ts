// The evaluation chapter's statistics, ported from the project's code so the figures give its numbers (docs:
// evaluation/metrics §4, evaluation/gates §3, §10): Rate.Wilson (src/Sts2Ai.Harness/Reports.cs:10-17), Gate.Sprt
// (src/Sts2Ai.Harness/Gate.cs:60-105) and gate.py's mean_se and paired lower bound (learn/gate.py:32-37, 130-151).

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

/** One pair of a gate: the challenger's floor less the champion's, and who won. */
export type Pair = { d: number; championWon: boolean; challengerWon: boolean };

export type SprtResult = {
  verdict: 'pass' | 'fail' | 'open';
  pairs: number;
  decided: number;
  llr: number;
  lower: number;
  upper: number;
  floorDiffMean: number;
  challengerOnly: number;
  championOnly: number;
  /** The LLR after each pair up to the decided one; null while a floor SPRT has fewer than MinPairs differing pairs. */
  path: (number | null)[];
};

export const MIN_PAIRS = 10;

/** Gate.Sprt: the paired SPRT on floors (Gaussian, variance from the pairs so far) or on wins (discordant pairs only). */
export function sprt(pairs: Pair[], metric: 'floor' | 'win', delta: number, alpha = 0.05, beta = 0.05, notWorse = false): SprtResult {
  const upper = Math.log((1 - beta) / alpha);
  const lower = Math.log(beta / (1 - alpha));
  const shift = notWorse ? delta : 0;
  let sum = 0;
  let sumSq = 0;
  let llr = 0;
  let only = 0;
  let championOnly = 0;
  let differing = 0;
  let decided = pairs.length;
  let verdict: SprtResult['verdict'] = 'open';
  const path: (number | null)[] = [];
  for (let n = 1; n <= pairs.length; n++) {
    const { d, championWon, challengerWon } = pairs[n - 1];
    sum += d;
    sumSq += d * d;
    if (d !== 0) differing++;
    if (challengerWon && !championWon) only++;
    if (championWon && !challengerWon) championOnly++;
    if (metric === 'floor') {
      if (differing < MIN_PAIRS) {
        path.push(null);
        continue;
      }
      const variance = Math.max(1, (sumSq - (sum * sum) / n) / (n - 1));
      llr = (delta / variance) * (sum + n * shift - (n * delta) / 2);
    } else {
      const p0 = 0.5 - shift;
      const p1 = p0 + delta;
      llr = only * Math.log(p1 / p0) + championOnly * Math.log((1 - p1) / (1 - p0));
    }
    path.push(llr);
    if (llr >= upper || llr <= lower) {
      verdict = llr >= upper ? 'pass' : 'fail';
      decided = n;
      break;
    }
  }
  return { verdict, pairs: pairs.length, decided, llr, lower, upper, floorDiffMean: decided === 0 ? 0 : sum / decided, challengerOnly: only, championOnly, path };
}

/** gate.py's mean_se: the mean and its standard error (sample SD / √n); NaN for an empty list, SE NaN for one value. */
export function meanSe(xs: number[]): { mean: number; se: number } {
  const n = xs.length;
  if (n === 0) return { mean: NaN, se: NaN };
  const mean = xs.reduce((a, x) => a + x, 0) / n;
  const se = n > 1 ? Math.sqrt(xs.reduce((a, x) => a + (x - mean) ** 2, 0) / (n - 1) / n) : NaN;
  return { mean, se };
}

/** gate.py's paired verdict: the 95% one-sided lower bound mean − 1.645·SE, promote when above 0. */
export function lowerBound(diffs: number[]): { mean: number; se: number; lower: number; promote: boolean } {
  const { mean, se } = meanSe(diffs);
  const lower = mean - 1.645 * se;
  return { mean, se, lower, promote: lower > 0 };
}

/** The unpaired standard error of a difference of two means over independent samples: √(s²a/na + s²b/nb). */
export function unpairedSe(a: number[], b: number[]): number {
  const v = (xs: number[]) => {
    const m = xs.reduce((s, x) => s + x, 0) / xs.length;
    return xs.reduce((s, x) => s + (x - m) ** 2, 0) / (xs.length - 1);
  };
  return Math.sqrt(v(a) / a.length + v(b) / b.length);
}
