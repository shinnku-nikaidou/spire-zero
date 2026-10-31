# Evaluation 2026-10-27: NoSL R1, promotion-1

**Void** (docs: evaluation/metrics §2): 5440 random decisions.

| | |
|---|---|
| game | v0.111.0 |
| commit | `dc0105a4a59a93f731c16bdc2b488fb91320d436` |
| champion | combat play, combat prompts p0, macro random, macro prompts random |
| environment, rung | NoSL, R1 |
| seed set | promotion-1: 200 of 200 runs, SILENT A0 |

## Main

- win rate: 0.0 % (0/200, 0.0 %–1.9 %)
- floor reached: mean 8.12, median 7.0
- act 1 boss: reached in 3 of 200 runs, entering with 42.7 HP (61 % of max) on average

## Must be zero

- random decisions (placeholders): 5440
- failed runs: 0; truncated runs: 0
- game errors: 0

## Auxiliary

- elites fought per run: 0.32; card rewards skipped: 57.9 % (469/810, 54.5 %–61.3 %); gold at the end: 135 on average
- compute: 0.2 s per run, 1.5 ms per decision
- surfaces: combat 22160, rewards 1901, card_choice 1502, map 1483, event 951, card_reward 443, shop 250, rest 193, treasure 47, bundle 4

## Against 2026-10-27 (combat play, combat prompts random, macro random, macro prompts random, commit `18fdb078afe5f86993a376bd98a355947817b619`)

| | then | now |
|---|---|---|
| win rate | 0.0 % (0/200, 0.0 %–1.9 %) | 0.0 % (0/200, 0.0 %–1.9 %) |
| floor mean | 8.09 | 8.12 |
| act 1 boss reached | 5 | 3 |
| random decisions | 6792 | 5440 |
| failed | 0 | 0 |
