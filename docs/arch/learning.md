# Valuation and self-improvement

Status: PLANNED. Nothing learned yet; placeholders only for bench.
From zero, not from the user's play. Online = search + learned value. Improvement loop = Stockfish dev process: self-play → fit value → tune → patch proposals → gate. LLM only offline (patch proposals, failure analysis); never online.

## 1. Value function

V(obs) = P(win this run from here, playing the current champion).
- One scale for combat leaves, macro afterstates, map (combat-brain.md §4, macro-brain.md §2).
- Input = observation only. Hidden state allowed at training time as auxiliary (§4), never as deployed input.
- Two functions: V_NoSL, V_SL (SL plays every later node better → higher P). Separate training targets, champions, gates; may share architecture/init (environments.md §8).

| version | form | training |
|---|---|---|
| v0 | hand features × linear weights (wide-spaced init keeps lexicographic intent) | SPSA |
| v1 | hand features + small MLP | supervised: MC + TD(λ) |
| v2 | obs encoder (card/relic/power embeddings) + multi-head | same + ExIt |

Heads (v1+): main P(win); aux (representation only, not used to decide): next-combat HP loss, floor-reached distribution, per-boss kill prob; combat head: end-of-combat HP distribution + death class (r33hab) for combat leaves.

## 2. Self-play loop

```
champion (per env) → self-play (train seeds, worker pool, real full-run worlds)
  → decision logs (obs, candidates, values, outcome)
  → fit V (MC + TD(λ)) → ExIt (search values as labels) → SPSA (weights, budgets, K, beam, thresholds)
  → failure clusters → LLM patch proposals → compile + unit + fair-play tests
  → challenger → gate (promotion seeds, CRN, SPRT) → promote | discard
```

1. Self-play: champion plays whole runs on train seeds; log format harness.md §6.
2. Fit: target = mix of MC outcome and TD(λ) (decision k → k+1). No wins early → floor-reached aux target; main head stays P(win). Never mix HP and win targets in one backward pass (r33hab).
3. ExIt: search values → labels for V and priors. Teacher = fair search of the same env. No behavior cloning of an omniscient teacher (failed in several projects).
4. SPSA on v0 weights and search params.
5. LLM: input = pre-aggregated failure clusters + death summaries; output = K candidate patches (features, pruning rules, params); filtered by compile/tests/fair-play, then gated. Cheap gateway models only.
6. Gate: evaluation.md §5.

## 3. Reward and credit

Reward = run outcome only. The hard part is credit assignment and variance, not missing signal.
1. Hierarchy shortens it: combat solved by search, its "reward" = post-combat V → run-level credit spans ~50 macro decisions + combat results.
2. TD(λ): gradient at every transition.
3. Aux terms only potential-based F = Φ(s') − Φ(s) (Ng/Harada/Russell 1999): telescopes to Φ(end) − Φ(start), optimal policy unchanged. Raw HP penalties would forbid "bleed in act 1 to win act 3".
4. Aux heads: dense supervision for representation only.
5. Variance: reverse curriculum (start near bosses); CRN-paired forks at key decisions (§5).

## 4. Privileged info at training time

Allowed by fair-play spec as long as deployed policy reads only observations:
- Oracle critic branch sees hidden state (true draw order, monster moves) to reduce target variance; deployed = obs branch only.
- Counterfactual labels: fork the same decision point with the same randomness, play both to the end → unbiased difference label.
- Offline pipeline only; separate assembly/process from the decision process.

## 5. Local vs global optimum

| mechanism | effect |
|---|---|
| value target = run outcome only | HP worth learned from data |
| reverse curriculum | snapshot pool from acts 2/3 and pre-boss first; learn late value, then push earlier |
| counterfactual lab | at sampled key macro decisions, CRN fork both lines to the end → unbiased ΔP(win); tests hypotheses like "bleed in act 1" |
| population/archive | one champion per style descriptor (MAP-Elites) → no collapse to "always conservative" |
| exploration | Thompson or ε on macro decisions in self-play; afterstate MC targets robust to it |
| conservatism monitors | elites taken, skip rate, HP at boss, gold/potion hoarding (evaluation.md §3) |
| contrarian hypotheses | user/LLM ideas → constrained policy variants → sim A/B; results back to discussion |

## 6. Infra

```
scheduler (seeds, champion) → worker pool (8 procs) → logs (JSONL per generation)
  → training (Python via uv, GPU) → weights (format TBD) → Brain loads (C# inference)
```

- Compute: 8 physical cores / 16 threads; 8 procs ≈ 1,860 combats/s vs 275 single (simulator.md §4). GPU (RTX 5070 Ti) for value-net training only.
- Python via uv reading JSONL; C# inference in Brain (small net, CPU; ONNX Runtime or hand-written forward). New packages/runtimes need user approval.
- Reproducibility: per generation record champion, seed sets, hyperparams, commit. Logs per generation dir.
- Storage: ~50 macro + few hundred combat decisions per run; compressed; old generations pruned on demand.

## 7. Tests

1. Calibration on held-out seeds: reliability plot; Brier must beat constant (ytcs's run model failed this).
2. Potential aux terms telescope exactly.
3. Train/deploy input parity: deployed model reads protocol obs only; training reads the same JSON.
4. Gate A/A: champion vs itself must not promote at the SPRT false-positive rate.

## 8. Open

1. v0 feature set: bottled_ai's 42 vs a ~10-feature minimum first.
2. Inference format (ONNX Runtime = new package).
3. One net with env input bit vs separate nets (start separate).
