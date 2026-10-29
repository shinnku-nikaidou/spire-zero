# SILENT ledger

The lineage of the Silent's brain: which brain played which data, and which data trained which weights (docs:
learning §6). The session that runs the training loop keeps it, one row per self-play batch and per weights file. Raw
data is in `data/silent/ENV/` (outside git); evaluations are in `leaderboard.md` and `reports/`.

Champions now: NoSL `champions/nosl.json` (pimc c2 8x300, L0 n2-b0.03). SL `champions/sl.json` (plan c2 at the
default medium effort, L0 pure-1-g), set 2026-10-29 as the first pure V (plan v4 step 4, no gate: a new line, judged by
its 200-run promotion-1 report at `6ce07fc`: floor 25.69, 0 won, 0 failures; +10.40 over the random macro brain's 15.29,
−2.56 ± 0.66 against the V v2 champion n2-b0.03's 28.25). NoSL keeps n2-b0.03 until a NoSL report of pure-1-g exists.
Earlier SL champion: L0 n2-b0.03, set 2026-10-28 by its promotion-1 baseline at `3699fc1`: floor 28.25, 0 won.
The elite gauntlet's champion (plan v6, `run --gauntlet`, judged by progress on the eval seeds 300000–300499 at the low
budget with clone, `learn/gauntlet.py`): pure-1-g, the plan's first. Its reference lines at `b013f95` (2026-10-29, 500
runs each, `sl/eval/2026-10-29-gauntlet-ref`): pure-1-g 34.47 ± 0.33 (acts' bosses beaten 94 % / 24 % / 3 % of those
reached, 2 won, 2.37 elites a run); n2-b0.03 33.71 ± 0.35 (88 % / 19 % / 2 %, 1 won, 0.64 elites), −0.76 ± 0.45
paired against pure-1-g; explore 1.0 with pure-1-g (every macro decision drawn evenly) 21.89 ± 0.35 (40 % / 5 % / 0 %,
0 won, 1.54 elites), −12.58 ± 0.46.
## Self-play batches

Train seeds are `run --seed 1 --start K`. A new batch takes the next free block of 100,000 seeds, entered here before
it starts: shard i starts at block + 1000·i and plays `--n 1000` at most, so no two shards share a seed.

**Next free block: 100000–199999.** 200000–202999 is the boss gauntlet pilot (`run --auto-win`, not self-play: `sl/forks/2026-10-29-gauntlet`, 2026-10-29); the gauntlet keeps 200000–299999; 203000–205399 is its second batch (plan v4 step 5, `sl/forks/2026-10-29-gauntlet-2`, V pure-1-g). Plan v6's elite gauntlet (`run --gauntlet`) takes the rest of that block: 205400–205549 is step 1b's smoke runs (V pure-1-g, `--explore` 1.0 / 0.5 / 0.15 on 205400 / 205450 / 205500, 50 each, `sl/eval/2026-10-29-gauntlet-smoke`); 206000–217999 is generation 1 (V pure-1-g; `--explore` 1.0 on 206000–209999, 0.5 on 210000–213999, 0.15 on 214000–217999, each a time box from its start, `sl/selfplay/v6g1/e100`, `e050`, `e015`); the next generations follow from 218000, 12,000 seeds each. **The elite gauntlet's evaluation seeds: 300000–300499** (`run --seed 1 --start 300000 --n 500`, fixed, never trained on).

| batch | env | played by | commits | train seeds | runs | used | folder (under `data/silent/`) |
|---|---|---|---|---|---|---|---|
| g0 b1 | NoSL | pimc 8x300 (LeafValue), L0 V v0, explore 0.2 | `1b80389`, `f355299` | 0–8999 | 356 (2 failed) | yes | `nosl/selfplay/g0/b1` |
| g1 b1 | NoSL | pimc c2 explore 0.003, L0 V v0 explore 2 | `11652b0`, `217af09`, `432aa0c` | 30000–34999 | 508 (3 failed) | yes | `nosl/selfplay/g1/b1` |
| g2 b1 | NoSL | pimc c2 explore 0.003, L0 V v0 explore 0.25 | `5032064`, `f11d11c` | 40000–47999 | 339 | yes | `nosl/selfplay/g2/b1` |
| g3 b1 | NoSL | pimc c2 explore 0.003, L0 n2-b0.03 explore 0.25 | `377e180` | 50000–60999 | 58 | no: 8 minutes only, so biased to early deaths; 0.25 is n2-b0.03's 45% exploration | `void/nosl/selfplay/g3/b1` |
| g3 t025 | SL | plan c2 beam 8x300, L0 n2-b0.03 explore 0.25 | `377e180` | 61000–61999 | 33 | no: 0.25 explores 45% with n2-b0.03 | `void/sl/selfplay/g3/t025` |
| g3 b1 | SL | plan c2 beam 8x300, L0 n2-b0.03 explore 0.006 | `377e180` | 61020–65999 | 419 | yes | `sl/selfplay/g3/b1` |

g3 b1 (SL): the records of the shards on three hosts say commit `a2643cf` or `ff9aa3f`, the checkouts' HEAD that an old
`tmp/build.sh` stamped; all of them were played by `377e180`, which predates `5060f25` (a run holding Lava Rock
replanned at every combat decision after the act 1 boss; legal play, only slow).

## Weights

Every file is `weights/silent/NAME.json` (in git). V v0's initial weights are built in.

| name | kind | trained on | gate | role now |
|---|---|---|---|---|
| (V v0) | V v0, hand-set weights | – | first champion's V | – |
| c1 | combat net | g0 (315 runs) | gated 2026-10-27, not promoted | – |
| c2 | combat net | g0 (315 runs) | passed 2026-10-27 against PIMC + LeafValue | NoSL champion's leaf; the SL planner's leaf |
| c3 | combat net | g1 (505 runs) | void at 160 of 200 pairs (+0.58 floors, open) | – |
| c4 | combat net | g1 + g2 (844 runs) | not gated yet | – |
| n1 | V v2 net | g0 + g1 (859 runs) | failed 2026-10-27 (−0.61 floors) | – |
| n2-b0.1 | V v2 net, pull β 0.1 | g0–g2 (1,198 runs) | passed 2026-10-28 (+2.00 floors) | – |
| n2-b0.03 | V v2 net, pull β 0.03 | g0–g2 (1,198 runs) | passed 2026-10-28 (+1.78 floors) | NoSL champion's V; SL champion's V until 2026-10-29; played SL g3 |
| n2-b0.01 | V v2 net, pull β 0.01 | g0–g2 (1,198 runs) | stopped halfway, to be run again | – |
| n3-ctl | V v2 net, pull β 0.03 (towards g0–g2's V v0), no boss labels | g0–g3 (1,617 runs) | not gated | – |
| n3-free-ctl | V v2 net, no pull, reads 9 of V v0's 17 features (floor, HP, max HP, gold, the map ahead), no boss labels | g0–g3 (1,617 runs) | failed 2026-10-29 in SL, non-inferiority (`--not-worse`, δ 1.5) at 33 pairs; over all 200: −5.77 ± 0.64 floors, act 2 boss reached 12 times (champion 105), deaths in act 2 hallways 76 (38) and elites 45 (7) | – |
| pure-1 | pure V (learn/v/: ids and raw state only, no pull), runs only | g0–g3 (1,617 runs) | SL report 2026-10-29 (screen afterstates, 200 runs): floor 22.64; +7.35 ± 0.62 over random macro | – |
| pure-1-b | pure V + a0 decision-time boss sims (2,358 act 0 card rewards of g3) | g0–g3 (1,617 runs) | SL report 2026-10-29 (screen afterstates, 200 runs): floor 21.44; −1.21 ± 0.67 against pure-1 (decision-time boss sims do not help) | – |
| pure-1-d | pure V + door states s0, s1 (1,891 real-run doors at the act 0 / act 1 boss) | g0–g3 (1,617 runs) | SL report 2026-10-29 (screen afterstates, 200 runs): floor 24.77; +2.12 ± 0.70 against pure-1 (real-run door states help) | – |
| pure-1-g | pure V + door states s0, s1 + the boss gauntlet pilot's doors (900 doors, acts 0–2, `sl/forks/2026-10-29-gauntlet`) | g0–g3 (1,617 runs) | SL report 2026-10-29 (screen afterstates, 200 runs): floor 25.69; +0.93 ± 0.58 against pure-1-d, +3.04 ± 0.69 against pure-1 | SL champion's V since 2026-10-29 (first pure V; NoSL still n2-b0.03) |
