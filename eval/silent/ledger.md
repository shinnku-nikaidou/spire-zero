# SILENT ledger

The lineage of the Silent's brain: which brain played which data, and which data trained which weights (docs:
learning §6). The session that runs the training loop keeps it, one row per self-play batch and per weights file. Raw
data is in `data/silent/ENV/` (outside git); evaluations are in `leaderboard.md` and `reports/`.

Champions now: NoSL `champions/nosl.json` (pimc c2 8x300, L0 n2-b0.03). SL has none yet: it needs the SL planner's
full promotion-1 evaluation.

## Self-play batches

Train seeds are `run --seed 1 --start K`. A new batch takes the next free block of 100,000 seeds, entered here before
it starts: shard i starts at block + 1000·i and plays `--n 1000` at most, so no two shards share a seed.

**Next free block: 100000–199999.**

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
| n2-b0.03 | V v2 net, pull β 0.03 | g0–g2 (1,198 runs) | passed 2026-10-28 (+1.78 floors) | NoSL champion's V; played SL g3 |
| n2-b0.01 | V v2 net, pull β 0.01 | g0–g2 (1,198 runs) | stopped halfway, to be run again | – |
