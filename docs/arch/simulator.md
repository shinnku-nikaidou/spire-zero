# Simulator (SimWorker)

Status: headless host + single-combat sessions + Determinize + CombatBuilder + full-run session EXIST; SL clone, worker pool/IPC PLANNED.
Refs: `D/` = `tmp/decompiled/sts2/MegaCrit.Sts2.Core.` (node files: find by name, unique). `[V]` read in source (me or research agent), `[I]` inferred.

Roles of a worker process: (a) host of the "real" run in R1–R3; (b) search worlds (NoSL samples, SL clones); (c) bench/selftest. One live run/combat per process (game singletons `RunManager`, `CombatManager`).

## 1. Headless host [exists]

- Boot (`HeadlessGame.Boot`): `GodotNativeStub.Install` → `HeadlessPatches.Apply` → `TestMode.TurnOnInternal` → `ModManager.State=Skipped` → `AssemblyInfo.Init`, `ModelDb.Init`, `ModelIdSerializationCache.Init`, `ModelDb.InitIds` → `LocalContext.NetId=1` → settings/prefs for test, `FastMode=Instant`. Log.Error + unobserved task faults collected; selftest asserts zero.
- Patch groups (`HeadlessPatches.cs`): engine shells (OS, Time, singletons, resources, loc, player name); asset preloads off; presentation in model code without the TestMode guard (Kaiser Crab arms, SoulNexus, `EventPresentation` transpiler, §5.2); persistence off (`SaveRun`, progress, replay, any disk IO → throw); rule fidelity (RollingBoulder one-by-one; `ShippedBranch` transpiler TestMode→shipped for MerchantPotionEntry, Cauldron, CallingBell); speed (`FastCreatures/PlayerCreatures/Players`, `FastHookListeners` with reverse-patch equivalence check); choices (`ChooseABundle`, `CrystalSphereScreen`, `CardRemovalUsed`); plus GameBridge's `ChoiceContexts` (gamebridge.md §5).
- JIT: `TieredCompilation=false` in csproj (tiered rejit silently dropped a Harmony patch after ~thousands of combats); launch with `DOTNET_ReadyToRun=0` (~20% faster).
- `Pump`: single-threaded `SynchronizationContext`; all game async continuations run on the driver thread → determinism. `RunUntil(cond)`, `Drain()`.
- Run env: `HOME` → scratch dir, `DOTNET_ROOT=~/.dotnet`, binary `src/Sts2Ai.SimWorker/bin/Release/net9.0/Sts2Ai.SimWorker`.
- CLI: `list | combat --encounter ID | combat --from OBS.json | run | bench | selftest | forkbench` + loadout opts (`Cli.cs` header).

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
- `run` (whole runs, SILENT A0, 9999 HP, random macro + `play` combat; long random-play combats dominate): ~1,800 decisions/s/process, ~0.4 s/run; 8 processes × 50 runs in 22 s wall.
- Levers, in order: avoid replay depth (DFS prefix reuse; node checkpoints §5.4), per-hook listener filtering (needs `perf`, not installed), object-graph deep copy (removes replay cost; needed for AlphaZero-style search).

## 5. Run session [exists]

`RunSession` (`RunSession.cs`): whole runs, real world setup, every decision through GameBridge entry points (same calls as the mod). The driver plays the UI's part: which screen shows, what its buttons do. Per-room screen memory = `RoomProgress` (left, rewards offered, rest succeeded, chest opened, relic picked), reset when `CurrentRoom` changes (also when an event combat resumes its event).
- `Step()` = settle, then `Decide()`: one decision, or one automatic UI step (offer combat rewards, open chest, leave treasure); >20 automatic steps in a row → throw.
- Settle: in combat pump until `WaitingForInput` (after EndTurn: a later turn); elsewhere until pump queue empty and executor idle. Then collect driver-started game tasks (`_running`: purchase, rest option, reward, divination…); a faulted one throws.
- Decide order: newest open rewards screen → a driver task still running with no screen = throw → map (MapRoom or room left) → by room: combat / event (crystal sphere minigame first) / shop / rest / treasure; other room = throw.
- Policies: combat `ICombatPolicy`, card prompts `IChoicePolicy` via `PolicySelector` (+ card reward and bundle handlers), every other screen `IMacroPolicy` (`MacroDecision(surface, legal, observation)`); placeholder `RandomMacro`. Prompt bounds from the context: cancelable → min 0 (`ChoiceContexts.Bounds`).
- Output: trace (every decision as "surface action", prompts answered inside other actions included), map hash per act, `Summary()` (act, floor, room, hp, gold, deck, relics, potions); optional `observed` callback gets each decision's `Observation` before its action applies. `IsOver = RunState.IsGameOver`; won = over in the victory room.
- CLI: `run [--n] [--start] [--seed] [--asc] [--policy] [--max-decisions] [--check] [--trace] [--obs]` + loadout opts. `--check` also observes every decision (JSON round trip must be exact, hashes compared on replay, per-run digest `obs` in the result line); `--obs` prints each observation.

### 5.1 Start (copy of `NGame.StartRun`, which exists only in a node [V])

`RunSetup(Character, Seed, Ascension, Loadout?)` → `Player.CreateForNewRun(ch, UnlockState.all, NetId)` → loadout → acts `ActModel.GetRandomList(Rng(hash(seed),"act_selection"), UnlockState.all, false)` (`StartRunLobby.BeginRunLocally`) → `RunState.CreateForNewRun` → `SetUpNewSingleplayer(run, shouldSave:false)` → `FinalizeStartingRelics` → `Launch` → `EnterAct(0, false)` → settle; first decision = Neow (`D/Nodes/NGame.cs:1138-1145,1176-1187`; `D/Runs/RunManager.cs:295-312,711-738`).
- Not `SetUpTest` (skips `GenerateRooms` `RunManager.cs:743-771`: ancients, double boss).
- `UnlockState.all`: all discovered, `NumberOfRuns` huge → no newbie tweaks, Neow start. Not done: mirroring the user's real profile (verify vs R4); ascension clamp to character max (`StartRunLobby.cs:485-491`), callers pass valid values.

### 5.2 Surfaces (as implemented; grammar protocol.md §3)

| surface | legal (GameBridge) | apply | notes |
|---|---|---|---|
| map | `MapActions.Travelable` = `NMapScreen.RecalculateTravelability` rules, sorted (row, col) | `RequestEnqueue(VoteForMapCoordAction(…))` | |
| combat | `CombatActions.Legal` | `CombatActions.Start` | won → `room.OfferRoomEndRewards()`, or `ProceedFromTerminalRewardsScreen` if `!Encounter.ShouldGiveRewards` (auto; `NCombatUi.OnCombatWon`) |
| rewards | untaken rewards (potion only with a free slot), `rewards_done` unless `DisallowSkipping` | `SelectLocalReward` | sets arrive in `RewardsSet.testSelector` (`ThrowInTestIfRewardsNotTaken=false`); non-terminal set closes once `IsRewardsSetCompleted`. Done: non-terminal → `SkipLocalRewardsSet`; boss/victory (not between double bosses) → `ActChangeSynchronizer.SetLocalPlayerReady`; else `ProceedFromTerminalRewardsScreen` (`NRewardsScreen`) |
| card_reward | cards + alternatives; reroll once per screen (button stays listed after use) | `PolicySelector.GetSelectedCardReward` handler | |
| card_choice | `IChoicePolicy` via `PolicySelector`; bounds by `ChoiceContexts.Bounds` | answer = picks; empty = cancel/skip | context read inside the `CardSelectCmd.From*` entry; choose-a-card without skip: min 1 (game enforces it after the selector) |
| bundle | `bundle:i` | prefix on `CardSelectCmd.FromChooseABundleScreen`: reserve choice id, ask, `SyncLocalChoice` | TestMode forced `bundles[0]`; only caller ScrollBoxes |
| event | options minus locked | `EventSynchronizer.ChooseLocalOption(i)` | `IsProceed` options are UI buttons: ancient "PROCEED" = `proceed`, Trial DOUBLE_DOWN (abandon popup) never legal. Finished → only `proceed` = open map (`NEventRoom.Proceed` NREs headless) |
| crystal_sphere | `big:X,Y` (3x3) / `small:X,Y` (one cell), only if it uncovers a hidden cell | `SetTool` + `CellClicked` | CRYSTAL_SPHERE minigame; `NCrystalSphereScreen.ShowScreen` prefix hands it to the driver (`HeadlessPatches.CrystalSphereShown`); after the last divination its rewards arrive as a rewards set. Small tool exists to avoid completing the curse |
| shop | `buy:i` stocked + affordable (potion needs a free slot), `remove`, `leave` | `OnTryPurchaseWrapper(inv)` | removal marked used by `CardRemovalUsed` (the removal node's job; also after Lord's Parasol's free removal); `MerchantRoom.Exit` returns early in TestMode → no history (cosmetic) |
| rest | enabled options; `leave` after a success or none enabled | `RestSiteSynchronizer.ChooseLocalOption` → `Task<bool>` | `NRestSiteRoom.cs:333,571-596` |
| treasure | `relic:i`, `relic_skip` | chest opens automatically (`DoNormalRewards` + `DoExtraRewardsIfNeeded`); pick/skip once; then `ProceedFromTerminalRewardsScreen` | single player: skip leaves `CurrentRelics` non-null → `RelicPicked` flag |
| potion belt | `potion:S` (any-time), `discard_potion:S`, appended to every macro surface | `EnqueueManualUse` / `DiscardPotionGameAction` | full belt: potion reward/purchase hidden until a slot frees |
| game over / victory | — | — | `RunState.IsGameOver`; victory room = won (game then kills the players) |

Patches the run needed (§1): asset preloads; presentation in model code that skips the TestMode guard: Kaiser Crab arms `BeforeDeath`, SoulNexus `AfterDeath`, `EventPresentation` transpiler (in every `EventModel` subclass incl. lambdas/state machines: call into or returning a Godot object → dropped, default result; `Task.Delay` → finished; hits Amalgamator, DenseVegetation, DollRoom, JungleMazeAdventure, PunchOff, Trial); `CrystalSphereScreen`; `ChooseABundle`; `CardRemovalUsed`.
Legit game behavior seen with a 9999-HP loadout: kill mechanics (THE_INSATIABLE Sandpit `Kill`, THE_LOST_AND_FORGOTTEN str/dex steal ~156 turns); PunchOff's "event not finished" warning (combat with `shouldResumeAfterCombat:false`, same in the real game).

### 5.3 Determinism for runs

- Measured (2026-10-26, 200 seeds SILENT A0, 9999 HP, random macro + `play` combat): `--check` 200/200 identical on replay in-process (trace, summary, map hashes); the same 200 in another process partition (8×25 vs 4×50) identical line for line (outcome, summary, decision count, map hashes) → `MapPoint.Children` order risk not observed; `MapActions` sorts anyway.
- IRONCLAD, DEFECT, REGENT, NECROBINDER (25 runs each, `--check`): all identical on replay, 0 failures, 0 game errors.
- With observations (2026-10-26, same setup): every observation round-trips exactly; 200/200 identical on replay incl. per-decision obs hashes; 8×25 vs 4×50 per-run lines identical incl. obs digest; other 4 characters ×25 identical; 0 failures, 0 game errors, 0 idle waits. Every surface reached (bundle rarest: 9 in 200 runs).
- TestMode rule branches for runs (testmode-audit): RewardsSet auto-claim → testSelector; ChooseABundle → prefix; ActModel act-1 and newbie tweaks → none with `UnlockState.all`; `MerchantRoom.Exit` cosmetic.
- Real-time waits: model code must not use `Task.Delay` (breaks quiescence). Amalgamator → patched. Others unreached headless: `ActionExecutor.WaitForUnpause` (50 ms poll while paused), `CombatState.GodotTimerTask` (timeout of a net-id creature lookup), rest-site hover messages (UI). Guard: `pump idle waits` = 0 over the 200-run batch.
- `NonInteractiveMode` inline execution vs per-frame yielding: fire-and-forget ordering may differ from the real game (R2/R3 diff list).

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
Exists (`run`): `--check` replays each run in-process and compares trace, summary, map hashes, observation hashes (each observation must survive a JSON round trip); report lists surfaces never reached, driver failures (no fallback: an unknown screen throws), game Log.Error lines, pump idle waits (real-time waits); exit 1 on any. Cross-process = same seeds in another process partition, diff the per-run lines. Result 2026-10-26: §5.3.
Planned: SL seal; horizon counterfactual fields; R3-vs-R4 same-seed diff once the mod logs runs.

## 10. Open

1. Load cost of a checkpoint (dominates run-level branching).
2. Deep copy vs replay: decide after search exists and profiles say restore dominates.
3. Run-world build from `RunView`: how much of `RoomSet`/odds is inferable vs prior.
