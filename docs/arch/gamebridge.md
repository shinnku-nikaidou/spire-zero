# GameBridge

Status: combat observe / legal / start EXISTS (`src/Sts2Ai.GameBridge`); run-level surfaces, prompts, events PLANNED.
Refs: `D/` = `tmp/decompiled/sts2/MegaCrit.Sts2.Core.`; `[V]` read in source, `[I]` inferred.

## 1. Role and rules

- The only code that turns game objects → protocol (observe, legal) and protocol action → game call (start). Shared by SimWorker and Mod → one code path R1–R4.
- Refs: game DLLs + Protocol. Never Brain. Never Godot nodes (`N*` types) → node-side work lives in host adapters (Mod: UI adapters, mod.md §4; Sim: copied node logic, simulator.md §5.2).
- Observe reads only what the UI shows (fair-play whitelist, protocol.md §8). Each field mapped from the property the UI binds to (`DisplayAmount`, `IsVisible`, `ShowCounter`, intents via `GetSingleDamage`), never from internal state.
- Start = same entry point the UI uses; never state edits, never commands that skip validation (`CardCmd.AutoPlay`, raw `new PlayCardAction`, `PlayerCmd.EndTurn`).
- Settling (when the next decision point is reached) is host-specific; GameBridge provides logic-only predicates (§6).

## 2. Existing API

| type | member | notes |
|---|---|---|
| `Observe` | `Combat(st, player)` | hand with uids; draw sorted by card key; powers `IsVisible` + `DisplayAmount`; intents of `NextMove` aimed at player |
| | `Card(card, withUid)`, `CardKey(card)`, `Intents(move, owner, targets)` | `Intents` reused by CombatBuilder (intent matching) |
| `CombatActions` | `WaitingForInput(player)` | player side, `Phase==Play`, executor idle, actions enabled. Sufficient headless; NOT in-game (mod.md §3) |
| | `Legal(st, player)` | hand order, then potions by slot, then `end_turn`; `CanPlay()` once per card |
| | `Start(st, player, action)` | `TryManualPlay(target)` / `EnqueueManualUse(target)` / `RequestEnqueue(EndPlayerTurnAction(player, TurnNumber))` |
| | `HandCard(pcs, uid)`, `Creature(st, uid)` | throw on unknown uid |
| `CardChoices` | `Canonical(options, min, max, tieBreak)` | card-key sort, ties by caller-seeded `Random` (never game order: may reflect pile depth) → `(CardChoice, gameIndex[])` |

Gap: `EndPlayerTurnAction` path doesn't check `CanTurnBeEnded` [V, agent] → add to `Legal` before the mod uses it.

## 3. Observation sources (planned)

| part | source [V unless marked] |
|---|---|
| `RunView` char/asc/act/floor | `player.Character`, `RunState.AscensionLevel`, `CurrentActIndex`, `TotalFloor` |
| hp/gold | `player.Creature.CurrentHp/MaxHp`, `player.Gold` |
| deck | `player.Deck.Cards` → `Observe.Card` (order: deck view order [I], protocol.md §9 Q2) |
| relics, potions | `player.Relics`, `player.PotionSlots` (existing mappers) |
| map | `RunState.Map` (points: `MapPoint.PointType`, children), `VisitedMapCoords`, `CurrentMapCoord`; `?` stays `unknown`; boss id only if the UI shows it [I] |
| travelable | copied `NMapScreen.RecalculateTravelability` rules (`NMapScreen.cs:874-921`) as a logic function here |
| rewards | current `RewardsSet` rewards in UI order, `!SuccessfullySelected` [I: field names per reward type] |
| card reward | offered cards + alternatives (computed once per screen, `CardReward.cs:321-330`) |
| event | `EventSynchronizer.GetLocalEvent()`: id, page, `CurrentOptions` (key, `IsLocked`, tips) |
| shop | `MerchantRoom.GetLocalInventory().AllEntries` (slot order, `IsStocked`, `Cost`, `EnoughGold`), removal entry |
| rest | `RestSiteSynchronizer.GetLocalOptions()` (`Id`, `IsEnabled`) |
| treasure | `TreasureRoomRelicSynchronizer.CurrentRelics` |
| card prompt | options + `CardSelectorPrefs` (`MinSelect`, `MaxSelect`, `Cancelable`, `Prompt` loc key) via `ChoiceContext` (§5) |

Combat during a run: same `Observe.Combat` (+ `RunView`).

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
| shop | `entry.OnTryPurchaseWrapper(inv)` (+ removal `SetUsed`) | same call (`ForceClick` on merchant nodes does nothing [V, agent]) |
| rest | `ChooseLocalOption(i)` | option button |
| treasure | `PickRelicLocally(i)` / `SkipRelicLocally()` | chest + relic buttons |

Rule: whenever the mod path is a node handler, the sim path must equal what that handler calls minus pure-UI effects; each such pair gets an R3-vs-R4 equivalence check (same seed, same decisions → same obs stream, mod.md §8).

## 5. Choice context

One Harmony prefix per `CardSelectCmd.From*` entry (`FromChooseACardScreen`, `FromSimpleGrid[ForRewards]`, `FromCombatPile`, `FromDeckForUpgrade/Transformation/Enchantment`, `FromDeckGeneric`, `FromHand[ForDiscard|ForUpgrade]`, `FromChooseABundleScreen` [V `D/Commands/CardSelectCmd.cs:252-965`]) sets a static current `ChoiceContext{Purpose = entry + prompt key, Source = model on the stack of the current action [I: from `ActionExecutor` current action], Cancelable, PromptKey}`; postfix clears. Shared patch → same context in both hosts. Patched method list re-checked on game update.

## 6. Decision-point predicates

GameBridge: logic-only `PendingSurface(runState) → surface?`: combat `WaitingForInput`; prompt context set and awaiting; rewards set offered; event options present and no pending option task; shop/rest/treasure room active and not leaving; map: room finished and travel allowed.
Host adds: Sim = `Pump.RunUntil(PendingSurface != null || ended)`. Mod = predicate + node idle checks held ~3 frames (mod.md §3).

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

1. Event `Source` attribution for prompts (current action owner may be indirect: power → hook → cmd).
2. Card-reward alternatives added by hooks: id naming.
