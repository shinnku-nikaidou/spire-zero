# Evaluation

Status: PLANNED. Today: single-combat `bench`, `forkbench`, `selftest`.
Answers: how strong (leaderboards) and did a change help (gates). Two envs reported separately, same pipeline.

## 1. Sim-to-real ladder

Every milestone runs all rungs, reported separately:

| rung | world | measures | today |
|---|---|---|---|
| R1 | sim full run, Harness drives | policy | combats only |
| R2 | R1 + obs round trip (decisions only on worlds built from obs) | obs reconstruction | combat round trip exists |
| R3 | real-DLL full run with real unlocks, lobby act selection, live protocol | world setup + interfaces | none |
| R4 | Steam game + mod | everything | none |

- One new variable per rung → gaps attributable (R1→R2 reconstruction; R2→R3 setup/interfaces; R3→R4 animation, timing, UI state, executor).
- R4 is expensive (real time) → confirmation, not tuning.
- Since R1 decisions already use the Build path (simulator.md §6), R1≈R2 for decisions; R2 adds the obs-only state for the host.

## 2. Two leaderboards

| | NoSL | SL |
|---|---|---|
| seeds | same pre-registered set | same |
| champion | V_NoSL champion | V_SL champion |
| info allowed | environments.md §3 | same table |
| budget | per decision, same tier | same |

- Also report SL − NoSL win-rate delta = value of in-node information (formal version of plan.md's omniscient-vs-resampled ablation). Reference: spire-mentor lost 22.8–25.5 pp without its (whole-run) cheat; ours is node-level → expect smaller.
- Never mix envs in one table or promotion decision.

## 3. Metrics

Main: win rate + Wilson 95%; mean floor reached; HP entering each boss.
Must be zero (else the eval is void): fallback/random decisions (harness.md §4); `NumReloads`; save exports in NoSL runs (environments.md §6).
Aux: surface coverage counts; compute per decision (branches, wall, SL clone queries); macro calibration (Brier < constant); conservatism (elites, skip rate, HP at boss, gold/potion hoarding); hangs/crashes counted as losses or reported separately, never re-run.

## 4. Seeds

- Three disjoint sets: train, promotion, final.
- Final seeds generated and registered in the repo beforehand (method + date); all run, all reported. ≥300 runs for a formal claim.
- Seed = game run seed string; R1–R3 started by the worker, R4 by the mod with the same seed (mod.md §7).
- Train seeds unlimited; promotion seeds rotated to avoid overfitting.

## 5. Gates

Full runs are expensive: at p≈0.3, +5 pp unpaired ≈ 1,300 runs/side; +2 pp ≈ 8,000. Tiered:

| tier | for | method | cost |
|---|---|---|---|
| combat panel | combat search/value | fixed (encounter, deck, seed), CRN, mean HP loss + death rate | s–min |
| act-level | macro | act starts from snapshot pool; act clear rate + HP at act end | min |
| full-run SPRT | stage promotion | paired on promotion seeds, SPRT (H0 Δ≤0, H1 Δ≥δ) | h |

- CRN: challenger and champion share run seed and search-seed derivation; measure actual variance reduction once full runs work.
- Paired tests: McNemar or paired SPRT. A/A test the gate.

## 6. Reports

Per eval: JSON + one-page Markdown: game version, commit, champion, env, rung, seed set, budget, all metrics, must-be-zero metrics, diff vs previous. In `reports/` (to create); raw logs not committed.

## 7. Baselines

| source | result | note |
|---|---|---|
| FableBot | Ironclad A0 20.7% (71/343, Wilson 16.7–25.3) | only fair, real-game, adequately sampled result; no Silent |
| AgenticSTS | Silent A0 ~35% (44/125, mixed versions); A8+ 0/25 | LLM + human Silent guide |
| placeholders | TBD | our floor |

First character Silent, A0 first.

## 8. Open

1. R4 throughput: minutes per run at Instant → how many R4 runs are affordable.
2. δ for promotion: start 2 pp, adjust to throughput.
