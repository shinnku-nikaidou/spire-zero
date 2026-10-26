# Mod (in-game host)

Status: PLANNED. Probe mod builds (`tmp/mods/StsAiProbe`, manifest `affects_gameplay:false`), never installed. Installing into the game is allowed (user, 2026-10-26).
Refs: `D/` = `tmp/decompiled/sts2/MegaCrit.Sts2.Core.`; `[V]` read in source (me or research agent), `[I]` inferred. Prior research: `tmp/notes/mod-research.md`, `tmp/notes/output-design.md`.

## 1. Role

Thin host in the real game: observe (GameBridge), ask Harness over a socket, render (output.md) and/or execute, log. No search, no learning, no strategy in-process. Single-player only; disabled in multiplayer.

Project `src/Sts2Ai.Mod`: refs game DLLs (+ GodotSharp), GameBridge, Protocol. Not Brain.

## 2. Loading [V]

- `<game>/mods/Sts2Ai/{Sts2Ai.json, Sts2Ai.dll}` (dir scanned recursively for manifests; DLL named `<id>.dll`; `D/Modding/ModManager.cs`). Game dir here: `~/.local/share/Steam/steamapps/common/Slay the Spire 2`.
- Manifest: `id`, `version`, `has_dll:true`, `has_pck:false`, `affects_gameplay:false` (only matters for multiplayer handshake; we refuse multiplayer anyway), `min_game_version:"0.111.0"`.
- Consent: `PlayerAgreedToModLoading` stored in `settings.save` `mods_enabled`; user clicks once in game. Loaded at startup only.
- Modded runs use a separate `modded/` profile dir (copied from vanilla on first use).
- Entry `[ModInitializer(nameof(Init))]` static; runs before `ModelDb.Init` → defer model-touching work to the first frame / `RunStarted`.
- Version guard: refuse to activate if game version ≠ pinned; show reason in panel.
- GameBridge + Protocol DLLs ship next to `Sts2Ai.dll` [I: verify the game's ALC resolves sibling DLLs; else ILMerge-style single DLL].

## 3. Main thread, decision loop, idle detection

- `GetTree().ProcessFrame += Tick` (plain C# event; no Godot source generators). Every game read/write happens inside `Tick`. Socket IO on a background thread → concurrent queue → drained in `Tick` (STS2MCP pattern, MIT).
- Loop per frame: if idle(surface) held ≥3 frames and `obs_hash` changed → send `decision`; on `recommendation` with matching hash → render / execute (§4); on hash change → `cancel`.
- Idle (in addition to GameBridge `PendingSurface`, gamebridge.md §6) [V, agent]:
  - combat: `CombatActions.WaitingForInput` + `ActionQueueSet.IsEmpty` + `Hand.CurrentMode == Play && !InCardPlay` + `NOverlayStack.ScreenCount == 0` + `!NMapScreen.IsOpen`.
  - hand-select prompt: `Hand.CurrentMode ∈ {SimpleSelect, UpgradeSelect}`.
  - overlay prompts / screens: type of `NOverlayStack.Peek()` (card reward, grid select, choose-a-card, bundle, rewards).
  - rooms: event/rest/shop/treasure root node active and its buttons enabled.
- Unknown surface (no adapter, or predicate true but no legal actions) → pause auto, panel says "manual needed", log coverage bug. Never random fallback.

## 4. Executor adapters

```csharp
// One adapter per surface; highlight and auto-play share the action → node mapping.
interface ISurfaceAdapter
{
    string Surface { get; }
    bool Matches();                       // current screen/room is this surface
    Control? AnchorFor(string action);    // node to highlight; null = panel only
    bool Execute(string action);          // same entry point a human click reaches
}
```

- Combat: `CombatActions.Start` (GameBridge); end turn only if `CanTurnBeEnded`.
- Buttons: call the handler only after `IsEnabled && IsVisibleInTree()` (`ForceClick` checks neither [V, agent]).
- Shop: `entry.OnTryPurchaseWrapper(inv)` (`ForceClick` on merchant nodes does nothing).
- Map: `NMapScreen.OnMapPointSelectedLocally(point)`.
- Card prompts: click holders + confirm (UI path keeps `PlayerChoice` in the replay record; gamebridge.md §4).
- After `Execute`: expect hash change within timeout (default 10 s at Fast, 3 s at Instant) → else re-observe; 3 consecutive failures → pause + log.
- Per-surface mod path vs sim path pairs are listed in gamebridge.md §4.

## 5. Brain link

- Unix socket `$XDG_RUNTIME_DIR/sts2ai/brain.sock`, JSON lines (protocol.md §2). Mod = client; Harness = server (started by user or spawned by the mod from a configured path; mod never runs search itself).
- `hello` carries env (NoSL/SL) chosen in mod config at run start; fixed for the run; logged.
- Brain unreachable → assist: panel "no brain"; auto: pause.

## 6. SL export (env-gated, SL only)

- Node entry (`RunManager.RoomEntered` after a map travel, plus run start): `RunManager.ToSave(null)` in memory (`D/Runs/RunManager.cs:670-709`) → send `node_checkpoint{json}` to Harness. Never write `current_run.save` (cloud-synced) or any file under the profile.
- Node action log: every executed or human action as a protocol action + all prompt answers, in order → `node_action` messages. Human actions in assist mode are captured by diffing pre/post state against the legal set [I: or hook `ActionExecutor.BeforeActionExecuted`, `D/GameActions/ActionExecutor.cs:52`].
- NoSL: none of the above runs (code path compiled in, gated at `hello`); any `node_checkpoint` in a NoSL log invalidates the run.

## 7. Auto-run driver (R4 eval)

- Seeded start: `NGame.StartNewSingleplayerRun(character, shouldSave:false, acts, modifiers, seed, GameMode.Standard, asc)` (`D/Nodes/NGame.cs:1137`) with the lobby's act selection (simulator.md §5.1), or set `NGame.DebugSeedOverride` before the Confirm button (AutoSlayer.cs:500-502). `shouldSave:false` → `current_run.save` untouched [V: param doc `NGame.cs:1130`; I: history/progress writes still happen → modded profile only].
- Run end: detect death (all players dead) / `WinRun`; record `run_end`; return to main menu; next seed from the pre-registered list.
- Speed: `PrefsSave.FastMode = Instant` only while auto-run is active; Instant is downgraded to Fast at startup in non-editor builds and prefs are saved on quit → restore the user's value on stop and on quit.
- Abandon/crash: seed recorded as `aborted` with reason; never re-run a seed to pick a better result.

## 8. Logs and R3≡R4

- Per decision: obs, legal, recommendation, executed action, latency, env, game version, mod version, `NumReloads` (must be 0).
- Equivalence test: same seed + same decision sequence in sim (R3) and game (R4) → identical obs stream. Divergence = bridge/sim bug; first-divergence report.

## 9. Controls and safety

- F8 panel toggle; F9 pause/resume auto; step mode (one action per key); semi-auto (highlight + Space executes `plan[0]`).
- Auto-play only in single-player runs; any multiplayer lobby → disable.
- Never: state edits, console, save writes, reloads, reading seed/RNG into messages (RunView excludes seed).

## 10. Patches and JIT

- Prefer events (`CombatManager.*`, `RunManager.RoomEntered`, `ActionExecutor.*`, `CardPile.*`) over Harmony.
- Unavoidable patches: `CardSelectCmd.From*` context prefixes (gamebridge.md §5). The game runs with tiered compilation → a rejit can drop a patch silently (seen headless). Mitigation: heartbeat — each patch bumps a counter; on a prompt without a context → log + panel warning; recommend launching with `DOTNET_TieredCompilation=0` [I: honored by the game's hosted runtime; verify].

## 11. Prior art (STS2MCP, MIT)

Copy: main-thread queue drained on `ProcessFrame`; per-screen action code as reference.
Avoid: raw `new PlayCardAction` (skips `CanPlayTargeting` + play effects), direct end turn, exposing seed/path, HTTP with CORS `*` (any web page could drive the game) → we use a Unix socket only.

## 12. Tests

- Load: mod initializes, version guard, panel shows.
- Surface coverage in real game: scripted R4 runs hit every surface; fallback/manual count = 0.
- Idle predicate: no decision sent mid-animation (log decisions with frame index; check none overlap queued actions).
- SL export round trip: checkpoint + log → worker replay completes, every logged action legal.
- Instant/Fast restore on stop/quit.

## 13. Open

1. Whether the game's ALC finds sibling DLLs (GameBridge/Protocol).
2. Human-action capture in assist mode (event hook vs diff).
4. Game env var for tiered compilation honored or not.
