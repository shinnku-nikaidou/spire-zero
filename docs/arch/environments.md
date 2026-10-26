# Environments: NoSL vs SL

Status: DECIDED 2026-10-26 (user). Every subsystem is parameterized by this.
Refs: `D/` = `tmp/decompiled/sts2/MegaCrit.Sts2.Core.`; `[V]` read in source, `[I]` inferred.

## 1. Definitions

Common: single-player only; pinned game version (v0.111.0); same rule knowledge; same Brain/Harness/Protocol code; same pre-registered seeds; reported separately. Neither env ever reloads the real game: `SerializableRun.NumReloads == 0`.

- **NoSL**: decision = f(public history of this run, rule knowledge, anything learned offline). Public history = everything shown on screen so far + what is inferable from it (card counting, monster move posteriors from intent history, reward-odds counters from reward history). Search only on determinized samples consistent with the public history.
- **SL**: NoSL + the true outcome of any action sequence inside the **current node** ("limited cheating"). Equivalent to unlimited save&quit inside the node with perfect memory.
  - User words: "sl 只能查看当前房间，当前事件，比如桥，接下来分别会删除什么卡之类的".
  - Node = current map point (`MapPoint`), from arrival until the next map point is chosen. Includes all rooms of the point (`MapPointHistoryEntry.Rooms`, e.g. event-started combat), the rewards screen, shop purchases, rest options, chest, every event page.
  - Examples SL may know: draw order, every reshuffle, monster moves, generated cards, random targets/costs/orbs, how action order shifts those (RNG manipulation is legit in-node), event outcomes (Slippery Bridge: card picked after each HOLD_ON — `SlipperyBridge.GetNewRandomCard` uses the event `Rng`, seeded seed+slot+hash(event id) `D/Models/EventModel.cs:234` [V]), this node's rewards incl. their dependence on play, Ancient "random rare card" identity.
  - SL may NOT know anything about later nodes (`?` outcome, encounters, rewards, shop stock, events), even though the sim holds the true streams.
- Rejected: whole-run horizon ("knows this run's whole order"). Do not build it.

## 2. Why node, not the game's save points

Game autosaves [V]: run start; arriving at a map point, before the `?` roll and `CreateRoom` (`D/Runs/RunManager.cs:899-901`, 915, 917); combat win, room marked pre-finished (`D/Combat/CombatManager.cs:1341-1342`); **Ancient** event finish only (`D/Rooms/EventRoom.cs:129-141`). No mid-combat save; reload replays the room identically (same encounter, opening hand, moves). Save stores full RNG state `{counter,s0..s3}` per stream (`D/Saves/SerializableRng.cs:51-63`).
Native S/L is slightly narrower than "node": after a combat win the save moves past the combat, so "does another line change the rewards" can't be probed natively. Node boundary chosen: matches user's words, single seal point. Difference = open Q1.

## 3. Information table

| info | NoSL | SL |
|---|---|---|
| screen history + inferences, rule knowledge, offline learning (privileged info at train time only) | yes | yes |
| this node: draw order, shuffles | multiset only | yes |
| this node: monster next moves | intents + history posterior | yes |
| this node: generated cards, random targets/costs/orbs | distribution | yes |
| this node: event outcomes, chest, rewards | when shown | yes |
| later nodes | no | no |
| seed string, RNG state, save file — read directly | no | no (only via the sealed clone) |
| state edits, console, debug/multiplayer backdoors | no | no |
| real reloads (`NumReloads>0`), seed picking at eval | no | no |

Grey-zone rulings identical for both (from fair-play-spec): exact damage math OK; powers only `IsVisible` with display amount; no `move_id` export; draw pile only as multiset; Crystal Ball event hidden cells never exported.

## 4. Node boundary

- Entry: arrival at map point = game save point → entry state is a `SerializableRun`.
- Exit: the map-travel action (`VoteForMapCoordAction`). The travel action belongs to the current node; everything after belongs to the next.
- Special: run start Ancient (Neow) is node 1; after a boss, the next act's first point is a new node; run end is an exit.
- Search crossing the exit (e.g. "value of this card in the next fight") must use determinized samples seeded by the search, never the true streams.

## 5. One brain, two world kinds

```
NoSL: public record --worker.Build(record, seed_k)--> K sampled worlds --search--> avg over K --> leaf V_NoSL(obs)
SL:   node-entry state + node trace --> 1 exact world, sealed at exit --search--> leaf V_SL(obs)
```

| | NoSL | SL |
|---|---|---|
| world source | public record → Build/Determinize (simulator.md §6) | node clone (simulator.md §7) |
| #worlds | K, averaged (no strategy fusion) | 1, deterministic |
| chance nodes | every reveal | none |
| budget goes to | samples × depth | depth/breadth (combat usually searchable to end) |
| past horizon | already samples | world stops (`AtHorizon`); continue only by re-determinizing from the exit observation |
| leaf | V_NoSL(observation) | V_SL(observation) |

Leaf evaluators take observations only (both envs) → true streams can't leak via evaluation. Search code is env-agnostic; env lives in Harness + world impl. Interface: protocol.md §7 (`IWorldSource`, `ISimWorld`).

## 6. Where the SL clone comes from

- R1–R3 (sim plays the run): host worker holds the true state; at node entry take a checkpoint (`RunManager.ToSave(null)`, `D/Runs/RunManager.cs:670-709` [V]); clone = checkpoint + node trace, restored by the run-replay path (simulator.md §5.4).
- R4 (real game): mod at node entry takes `ToSave(null)` in memory (never touches `current_run.save`, which is cloud-synced) + logs node actions (and `CombatReplayWriter` dump mid-combat, mod.md §6); sends to worker; worker loads via the Continue path and replays.
- Clone may differ from a continuously running game (load re-runs map hooks + `OnRunLoaded`, drops relic fields not marked saved; `D/Runs/RunManager.cs:815-816`, `D/Runs/RunState.cs:330-332`). Game bug → accepted as is (user 2026-10-26): no checksum verification, no fallback, not modeled in training.
- NoSL: channel closed. Env fixed at session start and logged; mod never exports saves; worker never offers clones; any export in a NoSL log = invalid run.
- Decision process never reads saves (Brain has no game refs, compile-time).

## 7. Guards (tests)

Both: (1) Brain/Protocol reference no game assembly [exists, selftest]; (2) search RNG from caller only, never from run seed; (3) fallback decisions == 0; (4) `NumReloads == 0`; (5) observation whitelist field tests.
NoSL: (6) counterfactual: same public record, different hidden state (draw order, all streams, monster moves + private AI fields, generated pools) → identical decision with fixed brain seed; (7) round trip state→obs→Build→obs' identical [exists for combat].
SL: (8) seal: any `Execute` after the exit action throws (in world impl); (9) horizon counterfactual: perturb only state read after the exit → identical decision. Perturbable [V, agent]: `RoomSet` entries past their counters for every act (events, weak/regular/elite encounters, boss, second boss, ancient; fixed at run start by UpFront in `GenerateRooms` `D/Runs/RunManager.cs:743-767`, consumed by counters `D/Rooms/RoomSet.cs:70-108`), UnknownMapPoint stream (consumed only on `?` entry, `D/Odds/UnknownMapPointOdds.cs:140-175`), TreasureRoomRelics stream + shared relic grab bag tail, future act map seeds (`Rng(seed,"act_N_map")`). NOT perturbable (shared with current node): Shuffle, MonsterAi, Niche, the six Combat* streams, player Rewards/Shops/Transformations. Each perturbable field needs a "current node never reads it" test; (10) log clone queries per decision.

## 8. Learning / eval / output split

- V_NoSL and V_SL are different functions (SL policy better at every later node → higher win prob). Separate training targets, champions, gates; may share architecture/init.
- Same seeds, two leaderboards; report SL−NoSL delta = value of node-level information (formal version of plan.md's omniscient-vs-resampled ablation).
- Output: NoSL facts are probabilities/expectations; SL facts may be exact outcomes ("play A then B → draw X").

## 9. Per-subsystem impact

| subsystem | NoSL | SL extra |
|---|---|---|
| protocol | obs, actions, prompts, recommendation, world API | `AtHorizon`; exact-outcome facts |
| gamebridge | whitelist obs, enumerate/execute | none |
| simulator | Build from record, Determinize | node checkpoint, load save + replay, seal |
| mod | observe, execute, overlay | node-entry export + action log (env-gated) |
| harness | record, K worlds | 1 clone |
| combat brain | PIMC expectation search | deterministic planning |
| macro brain | afterstate + V | in-node choices with exact outcomes |
| learning | V_NoSL | V_SL |
| evaluation | board NoSL | board SL + delta |

## 10. Open

1. Rewards after combat win: node rule (current) vs native-save rule. Measure how often rewards depend on in-combat stream use once full runs work (Rewards stream is shared with shops/events [V, agent], so likely rare inside a combat node).
2. SL budget: equal wall clock vs NoSL (fairer than equal branches).
3. Real reloads for clone verification: allowed only in tagged validation runs, never scored.
