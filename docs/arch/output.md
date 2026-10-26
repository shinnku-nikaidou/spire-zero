# Output layer (panel / highlight / auto)

Status: PLANNED. Source notes: `tmp/notes/output-design.md` (node names there verified in source).
Refs: `D/` = `tmp/decompiled/sts2/MegaCrit.Sts2.Core.`; `[V]`, `[I]` as elsewhere. Node files: find by name (unique).

## 1. Contract

Three consumers of one `Recommendation` (protocol.md §6); the brain never knows which mode is on.

| mode | consumer | needs |
|---|---|---|
| panel (assist, simple) | one text panel for all surfaces | `Plan`, `Candidates`, `Facts` |
| highlight (assist, advanced) | per-surface `ISurfaceAdapter.AnchorFor` (mod.md §4) | action → node |
| auto | `ISurfaceAdapter.Execute(plan[0])`, then re-observe | action → entry point |
| semi-auto | highlight + Space executes `plan[0]` | both |

Stale guard: render/execute only if `obs_hash` == current; else clear. While waiting: "thinking…"; anytime updates in assist (harness.md §5) replace the content.

## 2. Panel

- Own `CanvasLayer` (high layer) → `PanelContainer` + `RichTextLabel`; draggable corner; F8 toggles.
- Content per surface, all from `Recommendation`:
  - combat: numbered plan with targets; 1–2 facts (expected HP loss vs alternative, lethal next turn); top alternative with ΔP(win).
  - card reward / choices: options with ΔP(win) vs skip/baseline.
  - map: next point + path preview.
  - event/rest/shop: same format.
- Fallback for every surface without a highlight adapter.
- Optional zero-UI variant: brain serves a local page on the second screen (earliest testing only; Unix socket stays the game link).

## 3. Highlight

- Follow overlay: our nodes live only in our `CanvasLayer`; each frame read target `GetGlobalRect()` and follow; target gone/hidden → hide. Never write into game nodes: `NHandCardHolder.UpdateCard()` resets `CardHighlight` on every state change (`D/Nodes.Cards.Holders/NHandCardHolder.cs:613-640` [V]) and cyan/gold/red glows already mean things (`NCardHighlight`).
- Style: order badges ①②③ + an outline color the game doesn't use (purple).
- Anchors [V, notes §3.2]:

| surface | node | mark |
|---|---|---|
| hand | `NPlayerHand.Instance.GetCardHolder(card)` | badge + outline; hover = reason |
| target | `NCombatRoom.Instance.GetCreatureNode(creature)` | crosshair with card's number |
| potion | `NPotionHolder` | badge |
| end turn | `NEndTurnButton` | pulse when plan done |
| in-combat card select | hand select mode, `NCombatPileCardSelectScreen` | check marks |
| card reward | `NCardRewardSelectionScreen` holders + skip | ΔP per card, best outlined |
| rewards | `NRewardButton` | claim order / skip |
| map | `NMapPoint` in `NMapScreen` + own `Line2D` path (not `NMapDrawings`: multiplayer-shared) | next point + path |
| event | `NEventOptionButton` | best outlined, ΔP on others |
| rest | `NRestSiteButton` | same |
| shop | `NMerchantCard/Relic/Potion/CardRemoval` | buy order |
| grid select | `NCardGridSelectionScreen` family, `NCardGrid` | check marks |

- Order of work: hand + targets → card reward → map → event/rest/shop → grid prompts.
- Pitfalls: holders pooled/re-parented + animations (follow overlay handles it); resolution/UI scale/controller (only global rects); re-plan after every action renumbers badges.

## 4. Auto

Loop (mod.md §3): idle → observe → recommendation → validate hash + legality → execute `plan[0]` only → wait for hash change / timeout → repeat. Re-plan after every action (draws and random results feed the next decision). Speed via FastMode Instant + optional per-step delay (mod.md §7).

## 5. Narration

- Only from `Facts` + `Candidates` (no free reasoning about state). NoSL facts are probabilities/expectations; SL facts may be exact ("A then B → draws X").
- Templates first (per fact kind). Optional cheap-LLM polish offline or async (gateway, cheap models only): input = facts JSON + template text; output checked: every number in the text must equal a fact value (string match after formatting) → else show the template text.
- LLM never sees game state beyond facts and never affects the action.

## 6. Fair play in output

Executor accepts only `legal[]` actions; no state-edit, console or reload interfaces; `NumReloads` logged; mod never exports anything beyond protocol messages (SL export env-gated, mod.md §6).

## 7. Tests

- Anchor resolution per surface: every legal action of an adapted surface resolves to a visible node (debug overlay mode).
- Stale: delayed recommendations never render after a state change.
- Narration number check.

## 8. Open

1. Highlight for multi-target plans when targets die mid-plan (renumber vs keep).
2. Showing NoSL uncertainty (CI) without clutter.
