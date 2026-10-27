# GameBridge

Status: observe / legal / start for every surface and choice context EXIST (`src/Sts2Ai.GameBridge`); event stream PLANNED.
Refs: `D/` = `tmp/decompiled/sts2/MegaCrit.Sts2.Core.`; `[V]` read in source, `[I]` inferred.

## 1. Role and rules

- The only code that turns game objects → protocol (observe, legal) and protocol action → game call (start). Shared by SimWorker and Mod → one code path R1–R4.
- Refs: game DLLs + Protocol + Harmony (the game's `0Harmony.dll`, for §5). Never Brain. Never Godot nodes (`N*` types) → node-side work lives in host adapters (Mod: UI adapters, mod.md §4; Sim: copied node logic, simulator.md §5.2).
- Observe reads only what the UI shows (fair-play whitelist, protocol.md §8). Each field mapped from the property the UI binds to (`DisplayAmount`, `IsVisible`, `ShowCounter`, intents via `GetSingleDamage`), never from internal state.
- Start = same entry point the UI uses; never state edits, never commands that skip validation (`CardCmd.AutoPlay`, raw `new PlayCardAction`, `PlayerCmd.EndTurn`).
- Settling (when the next decision point is reached) is host-specific; GameBridge provides logic-only predicates (§6).

## 2. Existing API

| type | member | notes |
|---|---|---|
| `Observe` | `Combat(st, player)` | hand with uids; draw sorted by card key; powers `IsVisible` + `DisplayAmount`; intents of `NextMove` aimed at player |
| | `Of(run, player, screen)`, `Run(run, player)` | `Observation` = `RunView` + the surface's screen (§3) |
| | `Card(card, withUid)`, `CardKey(card)`, `Relic`, `Coord`, `Intents(move, owner, targets)` | `Intents` reused by CombatBuilder (intent matching) |
| `CombatActions` | `WaitingForInput(player)` | player side, `Phase==Play`, executor idle, actions enabled. Sufficient headless; NOT in-game (mod.md §3) |
| | `Legal(st, player)` | hand order, then potions by slot, then `end_turn`; `CanPlay()` once per card |
| | `Start(st, player, action)` | `TryManualPlay(target)` / `EnqueueManualUse(target)` / `RequestEnqueue(EndPlayerTurnAction(player, TurnNumber))` |
| | `HandCard(pcs, uid)`, `Creature(st, uid)` | throw on unknown uid |
| `MapActions`, `RewardActions`, `BundleActions`, `EventActions`, `CrystalSphereActions`, `ShopActions`, `RestActions`, `TreasureActions`, `PotionActions` | `Screen(…)` → the surface's `ScreenView`, `Legal(…)` → action texts, `Start(…, action)` (throws if not legal; returns the game task where the UI awaits one) | one static class per surface (potions: belt actions only, no screen); grammar protocol.md §3; `ActionText` parses |
| `CardChoices` | `Canonical(options, min, max, context, tieBreak)` | card-key sort, ties by caller-seeded `Random` (never game order: may reflect pile depth) → `(CardChoice, gameIndex[])` |
| `ChoiceContexts` | `Patch(harmony)`, `Current`, `Bounds(context, minSelect, maxSelect, n)` | §5 |

Gap: `EndPlayerTurnAction` path doesn't check `CanTurnBeEnded` [V, agent] → add to `Legal` before the mod uses it.

## 3. Observation sources

| part | source [V] |
|---|---|
| `RunView` char/asc/act/floor | `player.Character`, `RunState.AscensionLevel`, `CurrentActIndex` + `Act.Id`, `TotalFloor` |
| hp/gold | `player.Creature.CurrentHp/MaxHp`, `player.Gold` |
| deck | `player.Deck.Cards` → `Observe.Card`, sorted by card key |
| relics, potions | `player.Relics`, `player.PotionSlots` (combat mappers) |
| map | `RunState.Map` points (+ start, boss, second boss), `PointType`, `Children`, `Quests`; `VisitedMapCoords`; `Act.BossEncounter`, map's second boss, `Act.Ancient` |
| map screen | `MapActions` travelable rules (copied from `NMapScreen.RecalculateTravelability`) |
| rewards | `RewardsSet.Rewards` with `!SuccessfullySelected`; special card from its `CardHoverTip` (the reward's card field is private) |
| card reward | offered cards + alternative ids (computed once per screen, `CardReward.cs:321-330`) |
| bundle | bundles → `Observe.Card` |
| event | `EventSynchronizer.GetLocalEvent()`: id, `Description` key, `CurrentOptions` (`TextKey`, `IsLocked`, `Relic`, hover tips, `WillKillPlayer`) |
| shop | `MerchantInventory.AllEntries` minus removal (slot order, `IsStocked`, `Cost`, `IsOnSale`), `CardRemovalEntry` |
| rest | `RestSiteSynchronizer.GetLocalOptions()` (`Id`, `IsEnabled`) |
| treasure | `TreasureRoomRelicSynchronizer.CurrentRelics` |
| crystal sphere | minigame cells: `IsHidden`, the item under each uncovered cell (`Item.ToSerializable().type`), divinations left |
| card prompt | options + `CardSelectorPrefs` via `ChoiceContexts` (§5) |

Combat during a run: `Observe.Combat` inside the screen (`combat`, or `card_choice.combat`).

## 4. Actions per surface

Action grammar: protocol.md §3. Apply path per host:

| surface | logic entry (GameBridge, used by Sim) | Mod path |
|---|---|---|
| combat | `CombatActions.Start` | same |
| potion discard | `DiscardPotionGameAction` | same |
| map | `RequestEnqueue(VoteForMapCoordAction(…))` | `NMapScreen.OnMapPointSelectedLocally` (node does lock/anim) |
| rewards | `RewardsSetSynchronizer.SelectLocalReward` / `SkipLocalRewardsSet` | reward button handler |
| leave rewards | `ProceedFromTerminalRewardsScreen` / `ActChangeSynchronizer.SetLocalPlayerReady` (simulator.md §5.2) | proceed button |
| card reward | answer `GetSelectedCardReward` (sim selector) | card holder / skip / reroll buttons |
| card prompts | answer `CardSelectCmd.Selector` (sim) | screen holders + confirm (UI path: records `PlayerChoice` for replay; `LocalSelector` skips `SyncLocalChoice` [V `CardSelectCmd.cs:264-300`] → would break SL replay) |
| event | `ChooseLocalOption(i)` | option button |
| shop | `entry.OnTryPurchaseWrapper(inv)`; removal marked used by the removal node (Sim: headless patch, simulator.md §5) | same call (`ForceClick` on merchant nodes does nothing [V, agent]) |
| rest | `ChooseLocalOption(i)` | option button |
| treasure | `PickRelicLocally(i)` / `SkipRelicLocally()` | chest + relic buttons |
| crystal sphere | `SetTool` + `CellClicked(cell)` | divination buttons + cell click |
| potion use (out of combat) | `EnqueueManualUse(null)` | potion popup |

Rule: whenever the mod path is a node handler, the sim path must equal what that handler calls minus pure-UI effects; each such pair gets an R3-vs-R4 equivalence check (same seed, same decisions → same obs stream, mod.md §8).

## 5. Choice context

`ChoiceContexts`: one Harmony prefix + finalizer on every public static `CardSelectCmd.From*` except `FromChooseABundleScreen` (own surface) [V `D/Commands/CardSelectCmd.cs`]. The prefix reads the entry's arguments (`CardSelectorPrefs`, `source` / `PlayerChoiceContext.LastInvolvedModel`, `CardPile`, `EnchantmentModel` + `amount`, `canSkip`) into `Current`; the finalizer clears it. Depth counter: the outermost entry wins (removal calls `FromDeckGeneric`). `[ThreadStatic]`.
- Read `Current` synchronously inside the entry (the selector call does): it is gone after the entry's first await returns. The mod's UI adapter must do the same.
- Hosts call `ChoiceContexts.Patch(harmony)` once, before any other patch (SimWorker: `HeadlessPatches.Apply`): a caller compiled earlier, e.g. while another patch compiles its replacement, can inline an entry's async stub and bypass the prefix. A prompt without a context throws in the sim selector.
- Target list is computed by reflection → new entries are picked up; their arguments still need review on game update.

## 6. Decision-point predicates

Which screen is pending is host-side: it depends on what the UI would show, which only the host tracks.
- Sim: `RunSession.Decide` (simulator.md §5): open rewards sets (from `testSelector`), crystal sphere minigame, then the room kind + per-room `RoomProgress`; settle = combat `WaitingForInput`, else pump empty and executor idle.
- Mod: the visible screen/overlay + node idle checks held ~3 frames (mod.md §3).
GameBridge keeps only logic predicates: `CombatActions.WaitingForInput`, `RewardActions.IsTerminal`, legal lists.

## 7. Uids and indexes

- Hand card = `NetCombatCardDb` id; creature = `CombatId` (combat only).
- Deck/grid prompts: canonical index (`CardChoices.Canonical`), never uid (no uid outside combat).
- UI-ordered lists (rewards, event options, shop slots, rest options, treasure relics): game index (order public).
- Map: `col,row`.
- Mod: SL clones must give the same numbers → id fast-forward on load (simulator.md §7).

## 8. Event stream (planned)

Source per event (protocol.md §5): `CardPile.CardAdded/CardRemoved` on hand/draw/discard/exhaust (`D/Entities.Cards/CardPile.cs:35-37`) for draw/move/generate (+ position for draw-pile inserts [I]); `ActionExecutor.AfterActionExecuted` (`D/GameActions/ActionExecutor.cs:56`) for plays; `CombatManager.TurnStarted` + intent snapshot; reshuffle = draw pile refilled from discard [I: find the shuffle cmd]. Only public-visible effects; random-position inserts logged without position.

## 9. Version adaptation

All game-API knowledge lives here + SimWorker patches + Mod node adapters. Game update: re-decompile → rebuild → selftest + surface-coverage test (simulator.md §9) → diff list of touched signatures.

## 10. Tests

- Whitelist: per-field "UI shows it" table, reviewed per new field.
- Legal completeness (mod debug mode): every enabled UI control ↔ one legal action.
- Start round trip: each legal action executes without exception and changes `obs_hash` (or ends the surface).
- Sim≡mod: same state → same obs (R3 vs R4 logs).

## 11. Open

1. `Source` when the entry has no `source` arg: `LastInvolvedModel` may be indirect (power → hook → cmd).
2. Card-reward alternatives added by hooks: id naming.
