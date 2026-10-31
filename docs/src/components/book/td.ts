// TD(λ) targets for a run's decisions (docs: learning/loop §4), the forward view: the reward comes only at the run's
// end, so decision k's λ-return mixes the values of the decisions after it with the outcome,
//   G_k = (1 − λ) Σ_{n=1}^{N−1−k} λ^{n−1} V_{k+n} + λ^{N−1−k} G,
// computed backwards as G_{N−1} = G, G_k = (1 − λ) V_{k+1} + λ G_{k+1}. λ = 1 is the Monte Carlo target (G for every
// decision); λ = 0 is one step of bootstrapping (V_{k+1}). Nothing in learn/ trains with these: V's labels are MC.

/** Each decision's λ-return, given V's value at every decision and the run's outcome, in V's units. */
export function lambdaReturns(values: readonly number[], outcome: number, lambda: number): number[] {
  const n = values.length;
  const out = new Array<number>(n);
  if (n === 0) return out;
  out[n - 1] = outcome;
  for (let k = n - 2; k >= 0; k--) out[k] = (1 - lambda) * values[k + 1] + lambda * out[k + 1];
  return out;
}
