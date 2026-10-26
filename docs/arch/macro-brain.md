# Macro brain (out-of-combat decisions)

Status: PLANNED. Today `PolicySelector` skips every card reward and answers prompts with placeholder policies; no run-level play exists (simulator.md §5).

## 1. Surfaces and shape

| surface | options | randomness after choice | consequence horizon |
|---|---|---|---|
| map (next point) | 1–4 | `?` roll, encounter, rewards | rest of act |
| rewards (claim order / skip) | ≤6 | none (gold, potion, relic shown) | run |
| card reward | 3–4 + skip/reroll | reroll result | run |
| deck prompts: upgrade / remove / enchant | deck size | none | run |
| transform | deck size | result card | run |
| choose-a-card / bundle | ≤3 | none | run |
| event / ancient option | 1–4 | event outcome, next page | run |
| shop | stock + removal + leave | none (stock visible) | run |
| rest | rest/smith/lift/dig/… | dig etc. | run |
| treasure | relic choice/skip | none | run |
| potion full | discard/use/skip | none | short |

Common: few options, long horizon, cheap to apply, hard to value. Value = P(win run) (learning.md), never HP/score shaping.

## 2. Method

### L0: afterstate + V (default, every decision)

For each option a: afterstate obs s'_a (apply in a world, observe), value V(s'_a). Random afterstates (transform, event outcome, reroll): NoSL → mean over K samples; SL → the one true outcome via the node clone. Pick argmax. Cost: options × (apply + observe + V) ≪ 1 s.
- Multi-step screens (shop: buy several, then leave; rewards: claim several; event pages): search the screen's action sequence like combat in-turn DFS, leaf = V at screen exit. Dedup by obs.

### L1: rollout check (close calls, eval-time budget)

When top-2 L0 values are within CI: for each candidate, simulate forward from the afterstate in sampled run-worlds (simulator.md §6) for H nodes (default: to the next boss) with fast policies (combat: rollout policy or shallow search; macro: L0), CRN-paired across candidates (same world seeds). Value = mean(V at horizon or terminal outcome). Paired difference + CI → choose; log both for learning (counterfactual paired rollouts = training signal for V at key decisions).
- SL: rollouts past the node still use sampled worlds (environments.md §4).

### Map

- Decision = next point only; re-decide each step.
- L0 afterstate = obs at the chosen point before the room (type known; `?` unknown) → V must value position + reachable map. Early V lacks it → add path features to the baseline evaluator: for best k paths to the boss, counts of elite/rest/shop/`?`/monster, act HP budget estimate.
- L1 for close calls: rollouts as above.

## 3. NoSL vs SL

| | NoSL | SL |
|---|---|---|
| in-node random outcomes (event result, transform, reroll, `?` already rolled) | K samples | exact (node clone) |
| in-node rewards depending on combat play | — | known (environments.md Q1) |
| beyond node | sampled worlds | sampled worlds |
| value function | V_NoSL | V_SL |

Neow / Ancient options: SL sees exact outcomes of random options (e.g. "random rare card" identity, environments.md §1).

## 4. Interface

```csharp
// Planned. One entry for every non-combat surface; routing by surface name (harness.md §4).
public interface IMacroBrain
{
    MacroDecisionResult Decide(MacroRequest request, IWorldSource worlds, Budget budget);
}

public sealed record MacroDecisionResult(
    string First,                          // one of legal[]
    IReadOnlyList<string> Plan,            // rest of this screen (shop buys, reward claims)
    IReadOnlyList<Candidate> Candidates,   // value, CI, samples per first action
    IReadOnlyList<Fact> Facts);            // e.g. deck delta, expected HP at boss, P(win) delta
```

- Out-of-combat card prompts (upgrade/transform/remove) come through the same `card_choice` surface with `ChoiceContext.Purpose`; Harness routes by "in combat or not".
- Rollout macro policy = L0 with a smaller K (no recursion into L1).

## 5. Budget

- Self-play: fixed branches; L0 always, L1 only on a sampled subset of close calls (cost control) — those become training data.
- Assist: L0 instantly, L1 refines while the human looks; anytime.
- Auto (R4): L0 + L1 with wall-clock cap.

## 6. Tests

1. Coverage: every macro surface reached in sim runs, fallback 0.
2. Decision panels: fixed (run state, options) cases with ground truth from large paired rollouts; track agreement of L0 vs ground truth per surface (= where V is weakest).
3. NoSL counterfactual: same public record, different hidden state (RoomSet tails, odds, streams) → same decision.
4. Determinism with fixed budget.

## 7. Open

1. Horizon H for L1 (next boss vs next act) and rollout combat policy strength; measure correlation with full-run outcomes.
2. Shop as one decision vs sequence (combinatorics of buy sets).
3. How much path planning V can learn vs explicit path features.
