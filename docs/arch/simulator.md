# Simulator (SimWorker)

Status: headless host + single-combat sessions + Determinize + CombatBuilder EXIST; full-run session, SL clone, worker pool/IPC PLANNED.
Refs: `D/` = `tmp/decompiled/sts2/MegaCrit.Sts2.Core.` (node files: find by name, unique). `[V]` read in source (me or research agent), `[I]` inferred.

Roles of a worker process: (a) host of the "real" run in R1–R3; (b) search worlds (NoSL samples, SL clones); (c) bench/selftest. One live run/combat per process (game singletons `RunManager`, `CombatManager`).

## 1. Headless host [exists]

- Boot (`HeadlessGame.Boot`): `GodotNativeStub.Install` → `HeadlessPatches.Apply` → `TestMode.TurnOnInternal` → `ModManager.State=Skipped` → `AssemblyInfo.Init`, `ModelDb.Init`, `ModelIdSerializationCache.Init`, `ModelDb.InitIds` → `LocalContext.NetId=1` → settings/prefs for test, `FastMode=Instant`. Log.Error + unobserved task faults collected; selftest asserts zero.
- Patch groups (`HeadlessPatches.cs`): engine shells (OS, Time, singletons, resources, loc, player name); persistence off (`SaveRun`, progress, replay, any disk IO → throw); rule fidelity (RollingBoulder one-by-one; `ShippedBranch` transpiler TestMode→shipped for MerchantPotionEntry, Cauldron, CallingBell); speed (`FastCreatures/PlayerCreatures/Players`, `FastHookListeners` with reverse-patch equivalence check); choices (`ChooseACardSkip` → `PolicySelector.MustPickOne`).
- JIT: `TieredCompilation=false` in csproj (tiered rejit silently dropped a Harmony patch after ~thousands of combats); launch with `DOTNET_ReadyToRun=0` (~20% faster).
- `Pump`: single-threaded `SynchronizationContext`; all game async continuations run on the driver thread → determinism. `RunUntil(cond)`, `Drain()`.
- Run env: `HOME` → scratch dir, `DOTNET_ROOT=~/.dotnet`, binary `src/Sts2Ai.SimWorker/bin/Release/net9.0/Sts2Ai.SimWorker`.
- CLI: `list | combat --encounter ID | combat --from OBS.json | bench | selftest | forkbench` + loadout opts (`Cli.cs` header).

## 2. Combat session [exists]

- `CombatSetup(Character, Encounter, ActIndex, Seed, Ascension, Loadout?, From?)`. Start: `Player.CreateForNewRun(ch, UnlockState.all)` → loadout → `RunState.CreateForTest` with `MockSinglePointActMap` → `RunManager.SetUpTest` → `EnterRoomDebug(Monster, encounter)`.
  - NOT the real travel path: skips the `?` roll/odds and `GenerateRooms` [V, agent] → fine for isolated combats, wrong for runs (§5).
- Snapshot = `(Setup, Trace)`; trace items `Action | Choice(game indexes) | Determinize(seed)`. Restore = new start + replay. Same setup+trace → same state (all randomness from seeded streams; `Rng.Chaotic` (clock) only feeds cosmetics [V, agent: skins, shake, dialogue, Trial entrant number, YummyCookie icon, card-add anim timing]).
- `Determinize(seed)` → `Resample(new Rng(seed))`: reseed every `RunRngType`, `PlayerRngType`, monster `Rng`; draw pile → card-key order → Fisher–Yates; renumber non-hand cards (`CombatBuilder.NumberCards`, else ids leak draw order); each enemy's `NextMove` resampled uniformly among moves showing identical intents (fixing `StateLog[^1]`).
- `Execute` uses `CombatActions.Start` (UI entry points) then `Settle` (pump until `WaitingForInput` or over).
- uids: hand = `NetCombatCardDb` id; creatures = `CombatId`; replay deterministic because numbering is.

## 3. CombatBuilder: build from observation [exists]

`CombatBuilder.Setup(obs, seed)` → start encounter with obs deck/relics → `Apply` overwrite visible state (round, enemies add/remove/order, HP/block, energy, gold, potions, relic counters, powers, 4 piles + per-card mods, intents) → `Resample(StableHash(seed))` → `BuildDifferences` (JSON-path diff obs vs rebuilt obs; empty = exact).
- Knob solver: shown numbers derive from internal ints differently per power/relic → try ints (amount, dynamic vars, fields) until display matches.
- Intents: choose among moves showing them; stunned → synthesize stun move like `Creature.StunInternal` (`FollowUpStateId = StateLog.Last().Id`); hidden enum switches in powers (e.g. `SurroundedPower._facing`) searched to maximize matching enemies.
- uids aligned with obs; other cards numbered above.
- Approximations (gap): per-turn history and internal counters; cost-modifier durations; player debuff decay; real stun move follow-ups; deck = all cards seen incl. generated; orbs/non-player allies not built; never validated on real-game observations.
- Selftest matrix (2026-10-26): SILENT 60/60, IRONCLAD 58/58, SILENT acts 0/1/2 elite+boss 57/58/57 — all checks pass.

## 4. Performance [measured 2026-10-26, Release, ReadyToRun=0]

- `bench` act0 weak+regular, `play` policy: SILENT 5000 → 319 combats/s, 10.3k actions/s, 3.1 ms/combat; IRONCLAD 2000 → 379/s. Short runs (n=200) ~6.7 ms/combat = boot/JIT warmup dominated.
- `forkbench`: restore = 0.28 ms + 0.073 ms × replayed actions; branch (restore + determinize + rollout, mean root depth 15.8, rollout 16.6) median 2.78 ms, p90 3.97 → 329 branches/s/core.
- Multi-process (bench n=3000 each, wall incl. ~1.2 s boot): P=1 275/s, 4 1060, 8 1860, 12 1879, 16 2019 combats/s. 8 physical cores; SMT ≈ +8%. Default pool = 8.
- Memory ~160 MB/worker.
- Levers, in order: avoid replay depth (DFS prefix reuse; node checkpoints §5.4), per-hook listener filtering (needs `perf`, not installed), object-graph deep copy (removes replay cost; needed for AlphaZero-style search).

## 5. Run session [planned]

Goal: play whole runs through semantic actions with zero fallback, real world setup, same code path as the mod.

### 5.1 Start (copy `NGame.StartRun`, which exists only in a node [V, agent])

`RunState.CreateForNewRun([Player.CreateForNewRun(ch, unlock, 1)], acts, mods, GameMode.Standard, asc, seed)` → `RunManager.SetUpNewSingleplayer` → `FinalizeStartingRelics` → `Launch` → `EnterAct(0, false)` (`D/Nodes/NGame.cs:1138-1145,1176-1187`; `D/Runs/RunManager.cs:295-312,711-738`).
- Not `SetUpTest` (skips `GenerateRooms` `RunManager.cs:743-771`: ancients, double boss).
- Acts: `ActModel.GetRandomList` with `Rng(hash(seed),"act_selection")` + Act1 override (`D/Multiplayer.Game.Lobby/StartRunLobby.cs:469-514`); TestMode skips forced-undiscovered act (`ActModel.cs:550`) — fine for a fully discovered save.
- `UnlockState`: build from the target profile; `StartedWithNeow = IsEpochRevealed<NeowEpoch>()`, else act-1 start is a Monster room (`RunManager.cs:579-582,799-831`). Default target = the user's profile (all discovered) → mirror it; verify vs R4.
- Ascension clamp to character max (`StartRunLobby.cs:485-491`).

### 5.2 Surfaces (enumerate / apply / wait) [V, agent; spot-check during impl]

| surface | enumerate | apply | wait | node-only logic to copy / patch |
|---|---|---|---|---|
| map | copy `NMapScreen.RecalculateTravelability` (`NMapScreen.cs:874-921`: start point / second boss / boss / `MapTravel.GetTravelablePointsFrom`, free travel) | `RequestEnqueue(VoteForMapCoordAction(me, MapLocation(cur, act), MapVote{coord, MapSelectionSynchronizer.MapGenerationCount}))` (`NMapScreen.cs:935-948`) → `MoveToMapCoordAction` | `RunManager.RoomEntered` | travel rules |
| combat end → rewards | `CombatManager.CombatWon` | `room.OfferRoomEndRewards()` (`CombatRoom.cs:261-270`), or `ProceedFromTerminalRewardsScreen` if `!Encounter.ShouldGiveRewards` | — | `NCombatUi.OnCombatWon` is the only caller |
| rewards set | inside `RewardsSet.testSelector`: rewards with `!SuccessfullySelected` | `RewardsSetSynchronizer.SelectLocalReward(r)` / `SkipLocalRewardsSet()` | `Offer` awaits selector then TCS (`RewardsSet.cs:167-199`) | install `testSelector` (static); `ThrowInTestIfRewardsNotTaken=false`; **every set left incomplete must be ended with `SkipLocalRewardsSet()` or awaited `OfferCustom` deadlocks** |
| potion belt full | `PotionReward` returns false (`PotionReward.cs:76-93`) | `DiscardPotionGameAction(owner, slot, false)` or `EnqueueManualUse` (AnyTime), retry | — | |
| leave rewards / act change | — | non-boss terminal: `ProceedFromTerminalRewardsScreen` (resumes parent event if needed); boss: `ActChangeSynchronizer.SetLocalPlayerReady()` → `MoveToNextAct`; after last act → `EventRoom(TheArchitect)` | `RoomEntered` | branching from `NRewardsScreen.OnProceedButtonPressed` (`:565-611`) |
| card reward | `ICardSelector.GetSelectedCardReward(cards, alts)`; alts = Skip/Reroll + hook, ≤2 | return card or alternative | sync loop; reroll loops (`CardReward.cs:321-330`) | check `CanReroll` before reroll (alts computed once); `PolicySelector` currently always skips |
| deck prompts (upgrade/transform/enchant/remove/grid) | `GetSelectedCards(options, min, max)` | return cards | awaited | auto-picks when `count ≤ min && !RequireManualConfirmation`; prefix to expose `CardSelectorPrefs.Cancelable` (`[ThreadStatic]`, like `MustPickOne`) |
| choose-a-card | `GetSelectedCards(cards,0,1)` | | | `ChooseACardSkip` prefix exists |
| choose-a-bundle | `FromChooseABundleScreen` (`CardSelectCmd.cs:965-1005`) | | | TestMode forces `bundles[0]` (:978) → prefix asks policy |
| choose-a-relic | `RelicSelectCmd.FromChooseARelicScreen` | | | no callers in v0.111.0; optional defensive prefix |
| event / ancient | `EventSynchronizer.GetLocalEvent().CurrentOptions` minus `IsLocked` | `ChooseLocalOption(i)` | `AwaitPendingOptionTasks`, `StateChanged`, `IsFinished` | never `Chosen()` an `IsProceed` option; finished/proceed = go to map (`NEventRoom.Proceed` NREs headless). Neow: `BeforeEventStarted` sets HP 0 then heals. Event combats resume via `ProceedFromTerminalRewardsScreen` |
| shop | `MerchantRoom.GetLocalInventory().AllEntries` (`IsStocked`, `EnoughGold`, `Cost`) | `entry.OnTryPurchaseWrapper(inv)`; removal `(inv,false,true)` then `SetUsed()` | awaited `Task<bool>` | `Used` set only via `NRun…OnCardRemovalUsed`; `MerchantRoom.Exit` returns early in TestMode → no history (cosmetic) |
| rest | `RestSiteSynchronizer.GetLocalOptions()` where `IsEnabled` | `ChooseLocalOption(i)` → `Task<bool>` | awaited | leave only after a success or no options (`NRestSiteRoom.cs:333,571-596`) |
| treasure | `TreasureRoomRelicSynchronizer.CurrentRelics` | `await room.DoNormalRewards()`, `DoExtraRewardsIfNeeded()`, then `PickRelicLocally(i)`/`SkipRelicLocally()`, then `ProceedFromTerminalRewardsScreen` | `RelicsAwarded` | chest open logic in `NTreasureRoom.OpenChest`; empty chest → `CompleteWithNoRelics` [I] |
| game over | all players `Creature.IsDead` | — | — | TestMode skips `OnEnded(false)`; detect ourselves |
| victory | TheArchitect option PROCEED (`IsProceed=false`) → `WinRun` | | `OnEnded(true)` then players killed | record win before the kill (postfix `WinRun`) |

Driver: one async loop keyed by current room: map → wait `RoomEntered`; combat → `CombatSession`-style loop then rewards; event/rest/shop/treasure → options until leave → map. All reward prompts via `testSelector`; all card prompts via pushed `ICardSelector` (checked before `PlayerChoiceSynchronizer`, `CardSelectCmd.cs:264,335`).

### 5.3 Determinism risks for runs

- `MapPoint.Children` is a set ordered by object identity; `MapCoord` tuple hash randomized per process [V, agent] → future act map generation may differ across processes [I]. Test: same seeds, all acts' maps in 2 processes, compare; patch ordering if needed.
- Remaining TestMode rule branches for runs (testmode-audit): RewardsSet auto-claim (→ testSelector), ChooseABundle (→ prefix), ActModel act-1 (profile-dependent), RunManager newbie tweaks (UnlockState-dependent). Cosmetic: `MerchantRoom.Exit`.
- `NonInteractiveMode` inline execution vs per-frame yielding: fire-and-forget ordering may differ (R2/R3 diff list).

### 5.4 Run snapshot = node checkpoint + node trace

Full-run replay from start would cost 0.073 ms × ~thousands of actions. Instead: at every node entry store `RunManager.ToSave(null)` (the game's own serializer); run snapshot = `(checkpoint, node trace)`; restore = load checkpoint (§7 path) + replay node trace. Same mechanism as the SL clone → one code path, tested by both. Measure load cost.

## 6. NoSL worlds [planned]

- Input: public record (protocol types) + search seed. Output: sampled state consistent with it.
- Combat worlds: `CombatBuilder.Setup(latest combat obs, seed)` (+ record-derived knobs: turn history, move posterior — gaps) then `Resample` per sample; build once per decision, snapshot, then each sample = restore + `Determinize(seed_k)`.
- Run worlds (macro search beyond a node): build a `SerializableRun` from `RunView` (deck, relics, potions, HP, gold, act, floor, map, visited) with hidden parts sampled: every RNG stream, `RoomSet` contents/counters (from rules: act pools, minus seen), grab bags, odds (inferred from reward history where inferable, else prior). Load it like a save (§7). Planned; exactness tested by obs round trip.
- R1 uses the same Build path as R4 (no Determinize-on-true-state shortcut for decisions) → identical behavior across rungs; `Determinize`-on-truth stays for tests/bench.

## 7. SL node clone [planned]

Load path (= Continue) [V, agent]:
1. `SaveManager.FromJson<SerializableRun>(json)` (`D/Saves/SaveManager.cs:786`) (or migration manager `RunSaveManager.cs:115`); optional `RunManager.CanonicalizeSave(save,1)`.
2. `state = RunState.FromSerializable(save)`.
3. `await RunManager.Instance.SetUpSavedSingleplayer(state, save)` (needs `State==null` → `CleanUp()` between sessions). Increments `NumReloads` in-process only (headless never writes; metrics-only field [V, agent]).
4. `Launch()`; `await GenerateMap()` (saved map).
5. `await LoadIntoLatestMapCoord(AbstractRoom.FromSerializable(save.PreFinishedRoom, state))` → `EnterMapPointInternal(saveGame:false)`: same `?` roll, encounter, monster HP (Niche), opening shuffle, moves.
6. After an in-harness combat win call `room.OfferRoomEndRewards()` (UI does it live).
- Template: `D/Nodes.Debug.Multiplayer/NMultiplayerTest.cs:535-600` (also fast-forwards action/hook/choice/reward ids not in the save, :552-556 — needed for uid alignment with the live game).
- Then replay the node action log (mod.md §6).
- Seal: world wrapper intercepts the travel action (and run end) → `AtHorizon=true`; later `Execute` throws.
- Horizon counterfactual helpers: perturb RoomSet tails, UnknownMapPoint, TreasureRoomRelics, grab-bag tail, future map seeds (environments.md §7); test each is unread inside the node.

## 8. Worker pool + IPC [planned]

- N processes (default 8), each: boot once (~1.2 s), serve requests over stdin/stdout JSON lines (protocol.md §2, §7).
- Requests: `open_world{kind: build|clone|run_host, payload}` → world id; `snapshot`, `restore`, `execute`, `legal`, `observe`, `run_branches{snapshot, branches[]}`; `close`.
- Affinity: keep a world's live state in its worker; branches from that world go there first (restore from scratch costs replay). Snapshots portable → any worker can take over.
- Rollout policies run in-worker (Brain code, protocol types only).
- Crash: restart process, re-open worlds from snapshots; Harness marks decision degraded.
- Determinism: results keyed and merged by (world idx, branch idx), never completion order.

## 9. Tests

Exists (`selftest`): determinism; restore + hidden state byte-equal; determinize keeps obs, replays, plays on; build shows obs, restores, plays on; fast hook listeners = game's; brain reaches no game assembly (+ GameBridge negative control); protocol round trip; rolling boulder path hit; no game errors.
Planned: run determinism (same seed, 2 processes, all decisions + final state); map generation cross-process; surface coverage (every surface hit, fallback 0) over N random-policy runs; SL seal; horizon counterfactual fields; R3-vs-R4 same-seed diff once the mod logs runs.

## 10. Open

1. Load cost of a checkpoint (dominates run-level branching).
2. Deep copy vs replay: decide after search exists and profiles say restore dominates.
3. Run-world build from `RunView`: how much of `RoomSet`/odds is inferable vs prior.
