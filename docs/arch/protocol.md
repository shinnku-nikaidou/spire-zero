# Protocol

Status: combat + run-level observation, screens, `ChoiceContext` EXIST (`src/Sts2Ai.Protocol`); envelopes, events, world API PLANNED.
Project refs: framework only. This assembly IS the whitelist: a field exists here only if the UI shows it (or it's inferable) and has a test.

## 1. Existing types

- `CombatObservation` (encounter id, act, ascension, round, `PlayerView`, hand with uids, draw sorted by card key, discard, exhaust, potions per slot, relics, allies, enemies with `IntentView{type, damage per hit, hits}`).
- `CardView{id, uid?, up, cost|x_cost, stars, enchant, affliction, keywords sorted, vars sorted}`; `ProtocolJson.CardKey` = serialization without uid = player-visible identity.
- `CombatAction` text form `play:C[:T]`, `potion:S[:T]`, `end_turn`; uids: hand cards = `NetCombatCardDb` id, creatures = `CombatId`.
- `CardChoice{options canonical-sorted by card key, ties by hidden-state-free seed; min; max; context}`, `Accepts(picks)`.
- `Observation{run, screen}` (§3), `ChoiceContext` (§4), `Surfaces` constants (`Surfaces.cs`).
- `ProtocolJson`: snake_case, nulls omitted; `Hash(obs)` = `obs_hash`.

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

- `obs_hash` = FNV-1a 64 over the canonical JSON of `obs` (`ProtocolJson.Hash`). Host drops a recommendation whose hash ≠ current.
- `decision_id` strictly increasing per run; brain answers only the latest.

## 3. Observation

`Observation.cs`, `Screens.cs`. One per decision; built by GameBridge (gamebridge.md §3).

```
Observation{run: RunView, screen: ScreenView}           // screen JSON carries "surface": <Surfaces constant>
RunView{character, ascension, act (from 0), act_id, floor (run total), hp, max_hp, gold,
        deck (sorted by card key), relics, potions (per slot, null = empty), map}
MapView{points (sorted row,col: start row 0 .. boss/second boss), visited (this act, last = current),
        boss, second_boss?, ancient?}
MapPointView{col, row, type (icon; "?" stays Unknown after the visit), next (sorted), quest?}
```

Screen variants (surface names = routing keys, harness.md §4):

| surface | `*Screen` payload | legal action forms |
|---|---|---|
| `combat` | `combat: CombatObservation` | `play:C[:T]` `potion:S[:T]` `end_turn` |
| `card_choice` | `choice: CardChoice` (+ `context`, §4), `combat?` while in combat | `pick:i,j,…` (canonical indexes; empty allowed iff min=0) |
| `map` | `travelable` coords, legal order (the map itself is `run.map`) | `map:col,row` |
| `rewards` | `rewards[{index, kind: gold\|potion\|relic\|card\|special_card\|remove_card, id?, amount?, card?}]`, unclaimed only | `reward:i`, `rewards_done` |
| `card_reward` | `cards`, `alternatives` (button ids: Skip, reroll, hook-added) | `card:i`, `card_alt:<id>` |
| `bundle` | `bundles[[CardView]]` | `bundle:i` |
| `event` | `id`, `page?` (loc key), `options[{index, key, locked?, relic?, tips?, kills?}]` (empty once finished), `finished` | `event:i`, `proceed` |
| `shop` | `entries[{slot, kind: card\|relic\|potion, card?\|id?, cost, on_sale?}]` (stocked only), `removal_cost?` (null = used) | `buy:<slot>`, `remove`, `leave` |
| `rest` | `options[{id, enabled}]` | `rest:<id>`, `leave` (only after one success or none left) |
| `treasure` | `relics` (ids) | `relic:i`, `relic_skip` |
| `crystal_sphere` | `rows[y][x]`: `#` hidden, `.` empty, `R` relic `P` potion `C` card reward `G` gold `X` curse; `divinations_left` | `big:X,Y` (3x3), `small:X,Y` (only if a hidden cell is uncovered) |

Run end is the wire's `run_end`, not a surface.
Every macro surface (not `combat`, not `card_choice`) also lists the belt: `potion:S` (any-time potion), `discard_potion:S`. Full belt: potion rewards/purchases are left out of legal until a slot frees (no `potion_full` surface). Event `proceed` = leave to the map (finished event, or an ancient's PROCEED button).
Macro surfaces reach the policy as `MacroDecision(surface, legal, observation)` (Brain); `card_choice` through `IChoicePolicy`.

- Indexes into game-provided lists are fine when the UI order is public (rewards, events, shop slots). Card lists whose order may reflect hidden state (piles) always go through canonical sort (`CardChoice`).
- Deck sorted by card key: the same deck reached in another order is the same observation (was open Q2).
- Map: never future `?` outcomes; boss and ancient ids are drawn on the map.
- Text: loc keys (`table.key`; event options by their text key), never resolved strings. Event vars NOT included yet: some events keep values among them the page doesn't show → per-event whitelist when a policy needs them. Hover tips are visible → `tips` = their model ids (`+` = upgraded card).
- Built lazily: only when read (`MacroDecision.Observation`, `CombatDecision.Observation`, RunSession's `observed` callback).

## 4. ChoiceContext

```
ChoiceContext{
  purpose      // CardSelectCmd entry minus "From", snake_case: deck_for_upgrade, deck_for_transformation,
               // deck_for_enchantment, deck_for_removal, deck_generic, hand, hand_for_discard, hand_for_upgrade,
               // choose_a_card_screen, simple_grid, simple_grid_for_rewards; combat pile prompt = "<pile>_pile"
  source?      // id of the card/relic/power/event that asks: the entry's `source` arg, else the
               // PlayerChoiceContext's LastInvolvedModel
  cancelable   // prefs.Cancelable, or canSkip (choose_a_card_screen)
  prompt_key?  // prefs.Prompt, "table.key"
  enchantment? // deck_for_enchantment: {id, amount}
}
```

- Cancel/skip = the empty answer: `min` = 0 when cancelable. `choose_a_card_screen` without skip asks the selector for 0..1 and enforces the pick itself → `min` 1 (`ChoiceContexts.Bounds`).
- Nested entries (removal → deck_generic): the outermost one names the purpose. Capture: gamebridge.md §5.

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

- Round trip: every type serializes/deserializes to equal JSON [combat: selftest; run level: every observation of `run --check`].
- Whitelist: per-field test that the field is UI-visible; reviewers add a row to fair-play table for any new field.
- No field carries seeds, RNG counters, move ids, hidden pile order, save paths.
- Canonical ordering wherever the game's order could reflect hidden state.

## 9. Open

1. Event stream granularity (start minimal, grow on demand).
2. Whether worker pipes need a binary codec (measure).
3. Event vars whitelist; card-reward rarity hint; transform preview (what the UI shows before confirm).
