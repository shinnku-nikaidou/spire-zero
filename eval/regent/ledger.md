# REGENT ledger

The lineage of the Regent's brain: which brain played which data, and which data trained which weights (docs:
learning §6). The session that runs the training loop keeps it, one row per self-play batch and per weights file. Raw
data is in `data/regent/ENV/` (outside git).

Champions now: none of the Regent's own (no `eval/regent/champions/`). Since 2026-11-03 the five characters share combat
net **c9-0** and V **v9-0** (`weights/all/`, the character a context token in both; the user's word), whose lineage is in
the Silent's ledger (`eval/silent/ledger.md`): c8-3 and v8-3 carried over with no training, so they had seen the Silent's
runs only. v9g0 is the Regent's first data.

## Self-play batches

Train seeds are `run --seed 1 --start K`, test seeds the files of `eval/seeds/` (other bases), and the two never meet
(the user, 2026-11-01): a self-play or fork job plays train seeds only, an eval job a test file only (`box.py` refuses
anything else), and training refuses a list holding an eval job's runs (`common.train_lists`). The blocks of 100,000 are
the same for every character: a block is entered in all five ledgers before it starts. The blocks before v9g0
(0–699999) are in the Silent's ledger, all of them the Silent's.

**Next free block: 800000–899999.** The SL self-play dataset v9g0 (the user, 2026-11-03: 6,000 low-budget A0 runs a character, 30,000 in all, `--explore` 0.3 / 0.1 / 0.02 drawn evenly, no `--macro-explore`; V v9-0, combat net c9-0, clone, at `e905a6a`, then `db96e56`) takes 700000–799999, 20,000 a character: SILENT 700000–719999, IRONCLAD 720000–739999, DEFECT 740000–759999, REGENT 760000–779999, NECROBINDER 780000–799999. Within a character's 20,000: `--explore` 0.3 on the first 2,000, 0.1 on the next 2,000, 0.02 on the next 2,000 (`sl/selfplay/v9g0/e030`, `e010`, `e002`); +6,000 to +18,999 for later boxes; +19,000 to +19,999 for smoke runs (`sl/forks/`), no data of use. The Regent's: e030 760000–761999, e010 762000–763999, e002 764000–765999,
smoke 779000–779999. v9g0 played at two revs (the user, 2026-11-03): first `e905a6a` with c9-0 / v9-0 at 1x, 06:18–07:21 UTC, 1,524 runs of all five characters; then the user had both nets widened 2x in place and the box moved: `db96e56`, the same settings, on the seeds the first box had not finished (`AFTER` it, folders `e030-b`, `e010-b`, `e002-b`). The 2x nets give the 1x nets' values to 4.2e-07 (c9-0) and 1.2e-07 (v9-0), so only ties break differently: on the same seeds, one run per character, 3 of 5 ended with the same record and 2 went other ways (a combat play's order, a card_choice by V); a 2x run takes 7–11 % longer. The first box finished, of the Regent's: e030 82 (760000–760077 complete, and 4 beyond); e010 89 (762000–762086 complete, and 2 beyond); e002 87 (764000–764086 complete); `db96e56` plays the rest of each job's 2,000.

| batch | env | played by | commits | train seeds | runs | used | folder (under `data/regent/`) |
|---|---|---|---|---|---|---|---|
| v9 g0 | SL, A0 | plan c9-0 low budget (beam 4x150, replan 0), clone, L0 v9-0, `--asc 0`, `--explore` 0.3 / 0.1 / 0.02 (a job each), uniform draws only; c9-0 / v9-0 at 1x in the first box, 2x in the second (see above) | `e905a6a` (first box), `db96e56` (second) | 760000–765999 (see above) | 5,998 (2,000 / 1,999 / 1,999; the first box 258, the second 5,740; 0 game errors, worker restarts, replans; 763411 and 764125 dropped: endless fights after p0 turned the hand into Minion Sacrifice for Guards, ended by hand), two boxes, 2026-11-03 06:18–07:21 UTC and 07:30 UTC to 2026-11-04 01:33 UTC, every chunk given before the deadline: floor 19.52 (18.46 / 19.46 / 20.64), 12 won (0 / 2 / 10) | not yet | `sl/selfplay/v9g0/e030`, `e010`, `e002`, each with `-b` |

## Weights

The Regent has no weights of its own: it plays the nets all characters share, `weights/all/` (their rows are in the
Silent's ledger).
