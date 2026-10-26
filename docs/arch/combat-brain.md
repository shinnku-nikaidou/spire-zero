# Combat brain

Status: PLANNED. Today only placeholders: `PlayPolicy` (random playable card), `RandomPolicy` (uniform) — bench/tests.
Answers: (a) player's turn → next action (card+target, potion, end turn); (b) prompts during play (discard, exhaust, choose, …). Same search code for both envs; only the worlds differ (environments.md §5).

## 1. Shape

- Turn = action sequence ending with `end_turn`. In-turn uncertainty: draws, random generation. Enemy turn deterministic once moves are fixed.
- Branching: hand 5–10, targeted cards × enemies, + potions; sequence length 3–8 → up to 10^4 sequences/turn, heavy transpositions (A then B ≡ B then A).
- Horizon: combat 3–15 turns. One turn = greedy; full combat = expensive (NoSL crosses many chance nodes) → cut at turn end with a leaf value in P(win run) units (§4).
- Cost (simulator.md §4): restore 0.28 ms + 0.073 ms/action; branch ≈ 2.8 ms median ≈ 330/s/core; 8 workers ≈ 6.8×.

## 2. NoSL: expectation over determinized worlds (PIMC)

1. Worlds: K from Harness (start 8, add when top-2 within CI). Each world: different draw order, streams, monster moves, all consistent with the public record.
2. In-turn DFS over action sequences; every node may end the turn (bottled_ai). Dedup by canonical obs hash within a world.
3. Aggregate by first action: per world, best value reachable after it; mean over K. Never max across worlds (strategy fusion). Execute first action only, re-observe, re-plan.
   - Residual fusion: after the first action each world's plan is clairvoyant ("play X, then see what's drawn" overvalued). If measured as a problem → ISMCTS.
4. Enemy turn simulated for real with the world's sampled moves; leaf taken at next player turn start (value sees post-enemy state).
5. Multi-turn when budget allows: expand top first actions one more turn (cheap policy or beam for turn 2).
6. Potions are ordinary actions (no hard-coded timing).
7. Prompts: in search, the sim's selector is driven by search (options = branches). Real-game lone prompt → evaluate each option with the same search (needs `ChoiceContext`, protocol.md §4).

## 3. SL: planning on one deterministic world

1. In-turn: same enumeration, 1 world → dedup more effective.
2. Across turns: beam per turn (keep top-B end-of-turn states) to combat end or budget. After the win the node continues (rewards) → may keep searching to the node exit (environments.md §10 Q1).
3. RNG manipulation falls out naturally (orders consume streams differently; search sees results).
4. Leaf: V_SL(obs) at combat end / node exit; at turn end if budget runs out.
5. World sealed at node exit (`AtHorizon`).

## 4. Leaf value

One scale: estimated P(win run) (learning.md).
- Input = observation only (both envs) → can't leak hidden state.
- v0: hand features × linear weights; feature list seeded from bottled_ai's 42 (player HP, block, enemy HP, powers, draw pile quality, turns-to-kill …); its lexicographic order → widely spaced linear weights; tuned by SPSA. Must value block, overkill, lethal-next-turn without saturating.
- Later: small net on encoded obs; head = end-of-combat HP distribution + death class (r33hab) → mapped to P(win). Target λ-mix (0.3 search value + 0.7 outcome) (learning.md §2).
- Mid-combat leaf (turn end) and post-combat leaf (won, before rewards) = same function; combat part empty in the latter.

## 5. Budget and pruning

| lever | effect |
|---|---|
| canonical obs-hash dedup | each distinct obs expanded once per world |
| move ordering + early stop | expand high-prior actions first (heuristic → policy net) |
| CI-based allocation over first actions | stop when top-2 separated; else more worlds for them |
| trivial decisions | one legal action or all sequences equivalent → no search |
| restore cost | DFS order reuses prefixes, restore only on backtrack; deep copy later (simulator.md §4) |

Rough: 1–2 s/decision × 8 workers ≈ 5,000 branches; K=16 → ~300 branches/world, enough for deduped in-turn enumeration.

## 6. Interface

```csharp
// Planned.
public interface ICombatBrain
{
    // Player's turn: search the worlds, return the first action and the whole plan.
    CombatDecisionResult Decide(CombatRequest request, IWorldSource worlds, Budget budget);

    // Prompt during play: chosen canonical indexes plus a value per option.
    ChoiceDecisionResult Choose(ChoiceRequest request, IWorldSource worlds, Budget budget);
}

public sealed record CombatDecisionResult(
    string First,                          // one of legal[]
    IReadOnlyList<string> Plan,            // rest of this turn (narration, highlight)
    IReadOnlyList<Candidate> Candidates,   // value, CI, samples per first action
    IReadOnlyList<Fact> Facts);            // expected HP loss, turns to kill, P(draw X), ...
```

- `Facts` = only narration source (output.md §5). NoSL: probabilities/expectations; SL: may be exact.
- `ICombatPolicy` / `IChoicePolicy` stay for placeholders and rollout policies.

## 7. Tests

1. Tactics suite: positions cut from sim with clear answers (take lethal, exact block, vulnerable before attack); record found/not found.
2. Fixed combat panel (encounter, deck, seed), CRN: mean HP loss + death rate; beat placeholders first; version gates per evaluation.md §5.
3. NoSL counterfactual: same record, different hidden state → same recommendation (fixed brain seed).
4. SL seal: any execute past the node exit throws.
5. Reproducible with fixed branch budget.

## 8. Open

1. K start + growth rule (panel: marginal HP-loss gain per K).
2. Fusion severity: PIMC vs ISMCTS panel.
3. Turn-end leaf vs rollout to combat end (NoSL) at equal budget.
4. SL beam width B; equal wall clock vs NoSL (environments.md §10 Q2).
