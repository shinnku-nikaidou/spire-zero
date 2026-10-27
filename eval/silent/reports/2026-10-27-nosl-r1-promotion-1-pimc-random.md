# Evaluation 2026-10-27: NoSL R1, promotion-1

**Void** (docs: evaluation §3): 11723 random decisions, 3 failed runs.

| | |
|---|---|
| game | v0.111.0 |
| commit | `18fdb078afe5f86993a376bd98a355947817b619` |
| champion | combat pimc 8x300, combat prompts random, macro random, macro prompts random |
| environment, rung | NoSL, R1 |
| seed set | promotion-1: 200 of 200 runs, SILENT A0 |

## Main

- win rate: 0.0 % (0/200, 0.0 %–1.9 %)
- floor reached: mean 13.68, median 14.0
- act 1 boss: reached in 73 of 200 runs, entering with 41.2 HP (58 % of max) on average
- act 2 boss: reached in 1 of 200 runs, entering with 41.0 HP (59 % of max) on average

## Must be zero

- random decisions (placeholders): 11723
- failed runs: 3; truncated runs: 0
- game errors: 0
  - 3x InvalidOperationException: world 0: InvalidOperationException: not built as observed: .relics[1 SILVER_CRUCIBLE].counter: want none, got 3

## Auxiliary

- elites fought per run: 0.94; card rewards skipped: 58.1 % (789/1359, 55.4 %–60.7 %); gold at the end: 149 on average
- compute: 41.3 s per run, 174.8 ms per decision
- surfaces: combat 35544, rewards 3421, map 2666, card_choice 2215, event 1236, card_reward 784, rest 727, shop 488, treasure 179, bundle 7

## Against 2026-10-27 (combat play, combat prompts random, macro random, macro prompts random, commit `18fdb078afe5f86993a376bd98a355947817b619`)

| | then | now |
|---|---|---|
| win rate | 0.0 % (0/200, 0.0 %–1.9 %) | 0.0 % (0/200, 0.0 %–1.9 %) |
| floor mean | 8.09 | 13.68 |
| act 1 boss reached | 5 | 73 |
| act 2 boss reached | 0 | 1 |
| random decisions | 6792 | 11723 |
| failed | 0 | 3 |
