# Architecture (index)

STS2 AI mod: assist (highlight play order + why) and full auto-play, one brain. Strategy self-improves from zero; not built from the user's play. Silent first, A0 first. Single-player only.
Game pinned: v0.111.0 (Godot 4.5.1, .NET 9.0.7, Harmony). Manual written for Claude: terse; code comments English.
Conventions: `D/` = `tmp/decompiled/sts2/MegaCrit.Sts2.Core.` (gitignored); `[V]` read in source, `[I]` inferred → verify before relying on it.

## 1. Constraints

1. No cheating beyond the env's allowance (§2). Decision process has no game refs (compile-time).
2. Online decision = search + learned value; one scale: P(win run). LLM never decides, never tie-breaks; offline patches + narration polish only, cheap gateway models.
3. Same code path training ↔ real game: same Protocol, GameBridge, Harness, brains.
4. Simulator = the real `sts2.dll` headless; no engine rewrite.
5. No fallback: an uncovered decision pauses, never random.
6. The mod never writes saves; nothing reloads the real game (`NumReloads == 0`).
7. Low coupling, simple, clear: each project one job, dependencies one way (§4); each surface/component behind one small interface; no mechanism without a current consumer; game bugs are not worked around (e.g. save load ≠ continuous play is accepted, environments.md §6).

## 2. Environments (environments.md)

- NoSL: public history only; search on K determinized samples, averaged.
- SL: NoSL + true outcomes inside the current node (map point, arrival → travel action); one sealed exact clone. Later nodes hidden in both.
- Separate values (V_NoSL, V_SL), champions, leaderboards; report SL−NoSL delta.

## 3. Processes

```
game process: STS2 + Mod ── GameBridge (observe / legal / start) ── overlay, executor
      │ JSON lines over Unix socket
decision process (no game refs): Harness (record, routing, budget, log) → combat / macro brain
      │ JSON lines over child pipes
SimWorker × N (real sts2.dll headless): worlds (build / clone / run host), branches, rollouts
offline (separate): self-play logs → fit V → ExIt → SPSA → LLM patches → gate
```

- Self-play: the run host is a SimWorker instead of the game; Harness and brains unchanged ("swap the world, not the brain").
- Game singletons → one live run per process → search must be multi-process; aligns with "decision process loads no game".

## 4. Code (src/, solution `Sts2Ai.sln`)

| project | refs | content |
|---|---|---|
| `Sts2Ai.Protocol` | framework | wire types = observation whitelist |
| `Sts2Ai.GameBridge` | game + Protocol | observe, legal, start (UI entry points) |
| `Sts2Ai.Brain` | Protocol | policies (placeholders today) |
| `Sts2Ai.SimWorker` | game + all above | headless host, combat + run sessions, CombatBuilder, CLI |
| `Sts2Ai.Mod` (planned) | game + GameBridge + Protocol | in-game host |
| Harness (planned) | Protocol + Brain | dispatcher process |

## 5. Doc map

| doc | scope | status |
|---|---|---|
| [environments.md](arch/environments.md) | NoSL/SL definitions, node boundary, information table, guards | decided |
| [protocol.md](arch/protocol.md) | wire, observation union, choice context, events, recommendation, world API | combat exists |
| [gamebridge.md](arch/gamebridge.md) | observe sources, actions per surface, prompt context, decision points, uids | combat exists |
| [simulator.md](arch/simulator.md) | headless host, combat session, CombatBuilder, perf, run session, NoSL worlds, SL clone, pool | combat exists |
| [mod.md](arch/mod.md) | loading, main-thread loop, idle detection, adapters, SL export, auto-run driver, safety | planned |
| [harness.md](arch/harness.md) | record/belief, routing, budget, decision log, failures | planned |
| [combat-brain.md](arch/combat-brain.md) | PIMC (NoSL), deterministic planning (SL), leaf value, pruning | planned |
| [macro-brain.md](arch/macro-brain.md) | afterstate + V, rollout check, map | planned |
| [learning.md](arch/learning.md) | V definition, self-play loop, credit, privileged training, local optima, infra | planned |
| [evaluation.md](arch/evaluation.md) | R1–R4 ladder, leaderboards, metrics, seeds, gates, baselines | planned |
| [output.md](arch/output.md) | panel, highlight anchors, auto loop, narration | planned |

Background notes (gitignored, Chinese): `tmp/notes/plan.md` (plan v1, M0–M5), `fair-play-spec.md`, `output-design.md`, `testmode-audit.md`, `mod-research.md`.

## 6. Status (2026-10-26)

| component | state |
|---|---|
| headless host, patches, Pump, determinism | done; selftest green |
| combat session, snapshot/replay, Determinize | done; 319 combats/s/core (SILENT), branch 2.8 ms |
| CombatBuilder (obs → combat) | done; approximations listed in simulator.md §3 |
| multi-process scaling | measured: 8 procs ≈ 1,860 combats/s |
| run session (whole runs headless) | done: all surfaces, random placeholders; 200/200 runs deterministic in- and cross-process, 0 failures/game errors (simulator.md §5) |
| SL clone (load save + replay + seal) | planned, path researched (simulator.md §7) |
| protocol beyond combat, Harness, worker IPC | planned |
| combat brain, macro brain, value | placeholders only |
| mod | probe builds, not installed |

## 7. Main gaps / risks

1. CombatBuilder approximations: turn history, internal counters, cost-modifier durations, stun follow-ups; never validated on real-game observations (simulator.md §3).
2. Monster move resampling uniform among intent-identical moves; needs posterior from history (harness.md §3).
3. Card prompts lack context → `ChoiceContext` (protocol.md §4, gamebridge.md §5).
4. Run start uses `UnlockState.all`, not the user's profile; ascension not clamped (simulator.md §5.1).
5. New game code that calls nodes without the TestMode guard outside events (where the transpiler covers it) shows up only as game errors in `run` batches (simulator.md §5.2).
6. Harmony patches in the real game vs tiered JIT (mod.md §10).

## 8. Proposed implementation order

1. [done 2026-10-26] Run session + all surfaces headless with placeholder macro policy; coverage + determinism + map cross-process tests (simulator.md §5, §9).
2. Protocol run-level types + `ChoiceContext` + GameBridge observe/legal for every surface.
3. Worker pool + IPC + world API (`RunBranches`).
4. Harness (record, routing, logs) + combat brain v0 (PIMC, v0 leaf) → combat panel gate vs placeholders.
5. Macro brain L0 + v0 value → R1/R2 full runs; evaluation pipeline.
6. Mod: load, observe, panel with placeholder brain → R4 pipeline; then executor + auto-run driver; R3≡R4 equivalence test.
7. Node checkpoint + SL clone + seal → SL leaderboard.
8. Learning loop (fit V, ExIt, SPSA, gates), then LLM patch loop.
