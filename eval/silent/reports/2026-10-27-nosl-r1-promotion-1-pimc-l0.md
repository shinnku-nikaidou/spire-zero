# Evaluation 2026-10-27: NoSL R1, promotion-1

**Void**: 2523 random decisions, 2 failed runs, 4 game errors.

| | |
|---|---|
| game | v0.111.0 |
| commit | `18fdb078afe5f86993a376bd98a355947817b619` |
| champion | combat pimc 8x300, combat prompts random, macro l0, macro prompts l0 |
| environment, rung | NoSL, R1 |
| seed set | promotion-1: 200 of 200 runs, SILENT A0 |

## Main

- win rate: 0.0 % (0/200, 0.0 %–1.9 %)
- floor reached: mean 18.50, median 17.0
- act 1 boss: reached in 175 of 200 runs, entering with 66.0 HP (93 % of max) on average
- act 2 boss: reached in 15 of 200 runs, entering with 64.8 HP (83 % of max) on average

## Must be zero

- random decisions (placeholders): 2523
- failed runs: 2; truncated runs: 0
- game errors: 4
  - 1x InvalidOperationException: world 0: InvalidOperationException: not built as observed: .player.orbs[0 GLASS_ORB].passive: want 3, got 4; .player.orbs[0 GLASS_ORB].evoke: want 6, got 8
  - 1x InvalidOperationException: world 0: InvalidOperationException: illegal play play:0:1 (PREDATOR)

## Auxiliary

- elites fought per run: 0.29; card rewards skipped: 42.8 % (708/1655, 40.4 %–45.2 %); gold at the end: 194 on average
- compute: 49.7 s per run, 190.2 ms per decision
- surfaces: combat 35897, rewards 4894, map 3501, card_choice 2958, event 1859, rest 1346, card_reward 947, shop 620, treasure 242, crystal_sphere 12, bundle 10

## Against 2026-10-27 (combat pimc 8x300, combat prompts random, macro random, macro prompts random, commit `18fdb078afe5f86993a376bd98a355947817b619`)

| | then | now |
|---|---|---|
| win rate | 0.0 % (0/200, 0.0 %–1.9 %) | 0.0 % (0/200, 0.0 %–1.9 %) |
| floor mean | 13.68 | 18.50 |
| act 1 boss reached | 73 | 175 |
| act 2 boss reached | 1 | 15 |
| random decisions | 11723 | 2523 |
| failed | 3 | 2 |
