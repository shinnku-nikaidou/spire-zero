# Protocol

Status: combat part EXISTS (`src/Sts2Ai.Protocol`); run-level screens, envelopes, events, world API PLANNED.
Project refs: framework only. This assembly IS the whitelist: a field exists here only if the UI shows it (or it's inferable) and has a test.

## 1. Existing types

- `CombatObservation` (encounter id, act, ascension, round, `PlayerView`, hand with uids, draw sorted by card key, discard, exhaust, potions per slot, relics, allies, enemies with `IntentView{type, damage per hit, hits}`).
- `CardView{id, uid?, up, cost|x_cost, stars, enchant, affliction, keywords sorted, vars sorted}`; `ProtocolJson.CardKey` = serialization without uid = player-visible identity.
- `CombatAction` text form `play:C[:T]`, `potion:S[:T]`, `end_turn`; uids: hand cards = `NetCombatCardDb` id, creatures = `CombatId`.
- `CardChoice{options canonical-sorted by card key, ties by hidden-state-free seed; min; max}`, `Accepts(picks)`.
- `ProtocolJson`: snake_case, nulls omitted.

## 2. Wire

- JSON lines, UTF-8. mod↔brain: Unix domain socket (no TCP, nothing reachable off-host). brain↔worker: stdin/stdout pipes of child processes (switch to a binary codec only if profiling says JSON dominates).
- Every message: `{"type": ..., "v": PROTOCOL_VERSION, ...}`. Version bump on any breaking change; logs carry it.

```
host → brain   hello{v, game_version, env: "NoSL"|"SL", role: "mod"|"worker", run_header}
host → brain   decision{decision_id, obs_hash, surface, obs, legal[], prompt?, events[]}
brain → host   recommendation{decision_id, obs_hash, action, plan[], candidates[], facts[], degraded?}
host → brain   executed{decision_id, action, ok, error?}
host → brain   cancel{decision_id}            // state moved on; brain drops the search
host → brain   run_end{won, floor, reason}
// SL only (env-gated, mod.md §6; a NoSL log containing these is invalid)
host → brain   node_checkpoint{node_index, save_json}      // RunManager.ToSave(null) at node entry
host → brain   node_action{node_index, seq, action}  // every executed action + prompt answer
```

- `obs_hash` = FNV-1a 64 over the canonical JSON of `obs`. Host drops a recommendation whose hash ≠ current.
- `decision_id` strictly increasing per run; brain answers only the latest.

## 3. Observation (planned union)

```csharp
// Planned. One per decision; the screen part says which surface is asking.
public sealed record Observation
{
    public required RunView Run { get; init; }       // always present
    public required ScreenView Screen { get; init; } // exactly one variant
}

public sealed record RunView
{
    public required string Character { get; init; }
    public required int Ascension { get; init; }
    public required int Act { get; init; }            // index from 0
    public required int Floor { get; init; }          // total floor, as the top bar shows
    public required int Hp { get; init; }
    public required int MaxHp { get; init; }
    public required int Gold { get; init; }
    public required IReadOnlyList<CardView> Deck { get; init; }   // deck view order (public)
    public required IReadOnlyList<RelicView> Relics { get; init; }
    public required IReadOnlyList<string?> Potions { get; init; }
    public required MapView Map { get; init; }
}
```

Screen variants (surface names = routing keys, harness.md §4):

| surface | payload | legal action forms |
|---|---|---|
| `combat` | `CombatObservation` | `play:C[:T]` `potion:S[:T]` `end_turn` |
| `card_choice` | `CardChoice` + `ChoiceContext` | `pick:i,j,…` (canonical indexes; empty allowed iff min=0) |
| `map` | travelable coords (current map is in `RunView.Map`) | `map:col,row` |
| `rewards` | rewards in UI order `{kind, id/amount}` | `reward:i`, `rewards_done` |
| `card_reward` | cards + alternatives (skip/reroll/hook-added, ≤2) | `card:i`, `card_alt:<id>` |
| `bundle` | bundles of cards | `bundle:i` |
| `event` | event id, page key, options `{index, key, locked, tips}` | `event:i`, `proceed` |
| `shop` | entries `{slot, kind, id, cost, stocked, affordable}`, removal `{cost, used}` | `buy:<slot>`, `remove`, `leave` |
| `rest` | options `{id, enabled}` | `rest:<id>`, `leave` (only after one success or none left) |
| `treasure` | relic choices | `relic:i`, `relic_skip` |
| `crystal_sphere` | 11x11 grid: each cell hidden or what it shows; divinations left | `big:X,Y` (3x3), `small:X,Y` (only if a hidden cell is uncovered) |
| `game_over` | won, floor | — |

Every macro surface (not `combat`, not `card_choice`) also lists the belt: `potion:S` (any-time potion), `discard_potion:S`. Full belt: potion rewards/purchases are left out of legal until a slot frees (no `potion_full` surface). Event `proceed` = leave to the map (finished event, or an ancient's PROCEED button).
Implemented: legal + apply for every surface with actions (macro ones as `MacroDecision(surface, legal)`, constants `Surfaces` in `MacroDecision.cs`; `card_choice` still through `IChoicePolicy`); payloads not yet.

- Indexes into game-provided lists are fine when the UI order is public (rewards, events, shop slots). Card lists whose order may reflect hidden state (piles) always go through canonical sort (`CardChoice`).
- `MapView`: points `{col,row,type}` (`?` stays `unknown`), edges, visited coords, current coord, boss id if shown. Never future `?` outcomes.
- Text: event/option ids as loc keys, never resolved strings with hidden values. Hover tips (cards/relics shown in a tip) are visible → included.

## 4. ChoiceContext (fixes gap: CardChoice has no context)

```csharp
// Planned. Why the game is asking, so a policy can answer a lone prompt in the real game.
public sealed record ChoiceContext
{
    public required string Purpose { get; init; }   // discard, exhaust, upgrade, transform, enchant, remove,
                                                    // choose, put_on_draw_top, retain, … (from the CardSelectCmd entry)
    public string? Source { get; init; }            // card/relic/power/event id that asked
    public required bool Cancelable { get; init; }  // CardSelectorPrefs.Cancelable (needs a prefix to expose, simulator.md §5)
    public string? PromptKey { get; init; }         // the prompt's loc key
}
```

## 5. Events (public history stream)

Per decision, events since the previous one. Minimal set = what belief needs (harness.md §3):

```
draw{card}  play{card_uid, target?}  move{card, from, to, pos: top|bottom|random|null}
generate{card, to}  shuffle{}  intents{creature, intents[]}  move_done{creature, effects…}
reward_seen{…}  shop_seen{…}  event_page{…}
```

Card identity in events = `CardView` (+ uid while in hand). Source on the game side: GameBridge subscribes to hooks/events (gamebridge.md §8). Grow only when a belief feature needs it.

## 6. Recommendation

```csharp
// Planned.
public sealed record Recommendation
{
    public required long DecisionId { get; init; }
    public required string ObsHash { get; init; }
    public required string Action { get; init; }                 // one of legal[]
    public required IReadOnlyList<string> Plan { get; init; }    // rest of the turn/screen, for highlight + narration
    public required IReadOnlyList<Candidate> Candidates { get; init; }
    public required IReadOnlyList<Fact> Facts { get; init; }     // the only source for explanations
    public bool Degraded { get; init; }                          // worker loss etc.; see harness.md §7
}
public sealed record Candidate(string Action, double Value, double Ci, int Samples);
public sealed record Fact(string Kind, IReadOnlyDictionary<string, double> Values, bool Exact); // Exact only in SL
```

## 7. World API (search ↔ simulator)

Lives in Protocol (types) so Brain can use it without game refs; implemented in SimWorker (in-proc or over pipes).

```csharp
public enum PlayEnvironment { NoSL, SL }

// The only way search touches a game. Given by Harness per decision.
public interface IWorldSource
{
    PlayEnvironment Environment { get; }
    // NoSL: k worlds sampled from the public record, seeded from searchSeed.
    // SL: one clone of the current node; k and searchSeed are ignored.
    IReadOnlyList<ISimWorld> Worlds(int k, ulong searchSeed);
}

public interface ISimWorld : IDisposable
{
    Observation Observe();
    IReadOnlyList<string> Legal();
    void Execute(string action);       // throws if illegal, or if AtHorizon
    bool AtHorizon { get; }            // SL: the last action left the node. NoSL: always false.
    WorldSnapshot Snapshot();          // opaque, serializable recipe (worker-side replay)
    void Restore(WorldSnapshot snapshot);
    // Batched to amortize IPC: run each branch from the snapshot, return the leaf observations.
    IReadOnlyList<BranchResult> RunBranches(WorldSnapshot from, IReadOnlyList<Branch> branches);
}
public sealed record Branch(IReadOnlyList<string> Actions, string? RolloutPolicy, int MaxActions);
public sealed record BranchResult(Observation Leaf, bool AtHorizon, bool Ended, int ActionsRun);
```

- `RunBranches` exists because per-step IPC (~tens of µs) is comparable to a sim step (~0.1 ms); rollouts run inside the worker with Brain's rollout policies (SimWorker already references Brain).
- `WorldSnapshot` survives worker crashes (any worker can restore it by replay).

## 8. Invariants / tests

- Round trip: every type serializes/deserializes to equal records [exists for combat].
- Whitelist: per-field test that the field is UI-visible; reviewers add a row to fair-play table for any new field.
- No field carries seeds, RNG counters, move ids, hidden pile order, save paths.
- Canonical ordering wherever the game's order could reflect hidden state.

## 9. Open

1. Event stream granularity (start minimal, grow on demand).
2. `RunView.Deck` order: the deck view sorts? Use the public UI order; verify in game.
3. Whether worker pipes need a binary codec (measure).
