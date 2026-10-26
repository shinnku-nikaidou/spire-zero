# Harness

Status: PLANNED. Today: in-process calls inside SimWorker (`ICombatPolicy`, `IChoicePolicy`); no Harness.
Role: the decision process's dispatcher. Host (mod or run-host worker) ↔ Harness ↔ brains + worker pool. No strategy, no game objects (Protocol types only).

## 1. Responsibilities

Does:
1. Protocol messages; `decision_id` ordering; drop stale (protocol.md §2).
2. Public record per run (§3) = NoSL belief input; SL: node checkpoint + node trace.
3. Worlds per env via `IWorldSource` (environments.md §5, protocol.md §7).
4. Route surface → brain; no fallback (§4).
5. Budget (§5); determinism of search seeds.
6. Decision log (§6).

Doesn't: read/execute game (GameBridge in host); build/determinize states (worker, needs game code); judge actions (combat-brain.md, macro-brain.md).

## 2. Processes and flow

```
host --decision{obs, legal, events}--> Harness
  Harness: append record, check stale, route
  NoSL: pool.open_world(build, record, seed_k) × K      SL: pool.open_world(clone, checkpoint + node trace)
  brain.Decide(request, worlds, budget) --run_branches--> pool
  Harness: log --> host: recommendation
```

- Host = mod (R4) or the run-host worker (R1–R3); Harness can't tell them apart ("swap the world, not the brain").
- Search workers ≠ host process. Self-play: the host worker only holds the true run; search runs on other workers.
- Single-process mode (bench/selftest): Harness + worker in one process, same interfaces, no IPC.

## 3. Public record (NoSL belief)

No mirror game kept in sync with true RNG (spire-mentor's structural bug). Every decision rebuilds worlds from the record:

```csharp
// Planned. Protocol types only.
public sealed class PublicRecord
{
    public RunHeader Header { get; }                    // character, ascension, game version, env
    public IReadOnlyList<RecordEntry> Entries { get; }  // observations, events, own decisions, in order
    public Observation Latest { get; }
}
```

- Sole input of NoSL worlds: worker gets (record, search seed) → samples a consistent state with game code (rules: monster state machines, pools, reward weights live in the worker).
- Inferences the record must support (priority): (1) draw multiset + known-position cards (needs `move{pos}` events); (2) per-monster move posterior from intent + effect history (today: uniform among intent-identical moves); (3) this turn's history → internal counters of powers/relics; (4) run-level: seen rewards/shops/events → hidden odds counters.
- Persisted with the run log; crash recovery = replay the log. No game save needed.
- SL: record still kept (logs, narration); search worlds come from the node clone (simulator.md §7). Harness stores the latest node checkpoint + node trace; the trace is reset at each node entry.

## 4. Surface routing

| surface | brain |
|---|---|
| `combat` | combat |
| `card_choice` in combat (discard, exhaust, choose, put on top, …) | combat (with `ChoiceContext`) |
| `card_choice` out of combat (upgrade, transform, enchant, remove) | macro |
| `map`, `rewards`, `card_reward`, `bundle`, `event`, `shop`, `rest`, `treasure`, `potion_full` | macro |

- No route → error "uncovered surface"; host pauses, asks for manual play, logs coverage bug. Never random fallback. Eval requires fallback count = 0 (evaluation.md §3).
- Surface list and game entry points: simulator.md §5.2, gamebridge.md §4.

## 5. Budget and determinism

| mode | unit | why |
|---|---|---|
| self-play, eval | fixed branches | reproducible: same request + brain seed + budget → same recommendation |
| assist | wall clock, anytime | human waits; send best-so-far, refine |
| auto (R4) | wall-clock cap + branch floor | don't stall, don't go shallow |

- Parallel results merged in fixed order (world idx, candidate idx), never completion order.
- Brain RNG from Harness: `brainSeed = Hash(runId, decisionIndex, championVersion)`; never derived from the game seed.
- Allocation: stop when top-2 separated beyond CI; else add samples/depth (combat-brain.md §5).

## 6. Decision log

One JSON line per decision:

```json
{"decision_id": 412, "env": "NoSL", "surface": "combat", "obs_hash": "…",
 "budget": {"branches": 4000}, "worlds": 16,
 "candidates": [{"plan": ["play:7:3", "play:9", "end_turn"], "value": 0.412, "ci": 0.018, "n": 1600}],
 "chosen": "play:7:3", "elapsed_ms": 812, "brain": "champion-0007", "degraded": false}
```

Consumers: learning (value targets, ExIt labels), evaluation (fallbacks, compute per decision, conservatism), output (narration only from candidates + facts).

## 7. Failures

| case | handling |
|---|---|
| stale request (newer `decision_id`) | cancel search, no reply |
| worker crash / timeout | reopen world on another worker from snapshot; answer with finished branches; `degraded=true` |
| build from observation fails | report `BuildDifferences`; no search; host pauses; bug logged. Never random |
| host rejects action as illegal | host re-requests; 2 in a row → pause |

## 8. Tests

1. Routing coverage: N sim runs hit every surface; fallback 0.
2. Reproducibility: replaying a decision log yields identical recommendations.
3. NoSL counterfactual at Harness level (same record, different hidden state → same recommendation) (environments.md §7).
4. Stale handling: reordered/duplicated requests → only latest answered.

## 9. Open

1. Event granularity in the record (start with §3 inferences, grow on demand).
2. Reusing built worlds across decisions within a turn (incremental: snapshot after build, extend trace with the executed action) vs rebuild each time.
