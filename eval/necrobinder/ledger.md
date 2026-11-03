# NECROBINDER ledger

The lineage of the Necrobinder's brain: which brain played which data, and which data trained which weights (docs:
learning §6). The session that runs the training loop keeps it, one row per self-play batch and per weights file. Raw
data is in `data/necrobinder/ENV/` (outside git).

Champions now: none of the Necrobinder's own (no `eval/necrobinder/champions/`). Since 2026-11-03 the five characters share combat
net **c9-0** and V **v9-0** (`weights/all/`, the character a context token in both; the user's word), whose lineage is in
the Silent's ledger (`eval/silent/ledger.md`): c8-3 and v8-3 carried over with no training, so they had seen the Silent's
runs only. v9g0 is the Necrobinder's first data.

## Self-play batches

Train seeds are `run --seed 1 --start K`, test seeds the files of `eval/seeds/` (other bases), and the two never meet
(the user, 2026-11-01): a self-play or fork job plays train seeds only, an eval job a test file only (`box.py` refuses
anything else), and training refuses a list holding an eval job's runs (`common.train_lists`). The blocks of 100,000 are
the same for every character: a block is entered in all five ledgers before it starts. The blocks before v9g0
(0–699999) are in the Silent's ledger, all of them the Silent's.

**Next free block: 800000–899999.** The SL self-play dataset v9g0 (the user, 2026-11-03: 6,000 low-budget A0 runs a character, 30,000 in all, `--explore` 0.3 / 0.1 / 0.02 drawn evenly, no `--macro-explore`; V v9-0, combat net c9-0, clone, at `e905a6a`, then `db96e56`) takes 700000–799999, 20,000 a character: SILENT 700000–719999, IRONCLAD 720000–739999, DEFECT 740000–759999, REGENT 760000–779999, NECROBINDER 780000–799999. Within a character's 20,000: `--explore` 0.3 on the first 2,000, 0.1 on the next 2,000, 0.02 on the next 2,000 (`sl/selfplay/v9g0/e030`, `e010`, `e002`); +6,000 to +18,999 for later boxes; +19,000 to +19,999 for smoke runs (`sl/forks/`), no data of use. The Necrobinder's: e030 780000–781999, e010 782000–783999, e002 784000–785999,
smoke 799000–799999. v9g0 played at two revs (the user, 2026-11-03): first `e905a6a` with c9-0 / v9-0 at 1x, 06:18–07:21 UTC, 1,524 runs of all five characters; then the user had both nets widened 2x in place and the box moved: `db96e56`, the same settings, on the seeds the first box had not finished (`AFTER` it, folders `e030-b`, `e010-b`, `e002-b`). The 2x nets give the 1x nets' values to 4.2e-07 (c9-0) and 1.2e-07 (v9-0), so only ties break differently: on the same seeds, one run per character, 3 of 5 ended with the same record and 2 went other ways (a combat play's order, a card_choice by V); a 2x run takes 7–11 % longer. The first box finished, of the Necrobinder's: e030 110 (780000–780102 complete, and 7 beyond); e010 100 (782000–782087 complete, and 12 beyond); e002 102 (784000–784093 complete, and 8 beyond); `db96e56` plays the rest of each job's 2,000.

| batch | env | played by | commits | train seeds | runs | used | folder (under `data/necrobinder/`) |
|---|---|---|---|---|---|---|---|
| v9 g0 | SL, A0 | plan c9-0 low budget (beam 4x150, replan 0), clone, L0 v9-0, `--asc 0`, `--explore` 0.3 / 0.1 / 0.02 (a job each), uniform draws only; c9-0 / v9-0 at 1x in the first box, 2x in the second (see above) | `e905a6a` (first box), `db96e56` (second) | 780000–785999 (see above) | running | – | `sl/selfplay/v9g0/e030`, `e010`, `e002`, each with `-b` |

## Weights

The Necrobinder has no weights of its own: it plays the nets all characters share, `weights/all/` (their rows are in the
Silent's ledger).
