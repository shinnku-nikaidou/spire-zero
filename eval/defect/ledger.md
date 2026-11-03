# DEFECT ledger

The lineage of the Defect's brain: which brain played which data, and which data trained which weights (docs:
learning §6). The session that runs the training loop keeps it, one row per self-play batch and per weights file. Raw
data is in `data/defect/ENV/` (outside git).

Champions now: none of the Defect's own (no `eval/defect/champions/`). Since 2026-11-03 the five characters share combat
net **c9-0** and V **v9-0** (`weights/all/`, the character a context token in both; the user's word), whose lineage is in
the Silent's ledger (`eval/silent/ledger.md`): c8-3 and v8-3 carried over with no training, so they had seen the Silent's
runs only. v9g0 is the Defect's first data.

## Self-play batches

Train seeds are `run --seed 1 --start K`, test seeds the files of `eval/seeds/` (other bases), and the two never meet
(the user, 2026-11-01): a self-play or fork job plays train seeds only, an eval job a test file only (`box.py` refuses
anything else), and training refuses a list holding an eval job's runs (`common.train_lists`). The blocks of 100,000 are
the same for every character: a block is entered in all five ledgers before it starts. The blocks before v9g0
(0–699999) are in the Silent's ledger, all of them the Silent's.

**Next free block: 800000–899999.** The SL self-play dataset v9g0 (the user, 2026-11-03: 6,000 low-budget A0 runs a character, 30,000 in all, `--explore` 0.3 / 0.1 / 0.02 drawn evenly, no `--macro-explore`; V v9-0, combat net c9-0, clone, at `e905a6a`) takes 700000–799999, 20,000 a character: SILENT 700000–719999, IRONCLAD 720000–739999, DEFECT 740000–759999, REGENT 760000–779999, NECROBINDER 780000–799999. Within a character's 20,000: `--explore` 0.3 on the first 2,000, 0.1 on the next 2,000, 0.02 on the next 2,000 (`sl/selfplay/v9g0/e030`, `e010`, `e002`); +6,000 to +18,999 for later boxes; +19,000 to +19,999 for smoke runs (`sl/forks/`), no data of use. The Defect's: e030 740000–741999, e010 742000–743999, e002 744000–745999,
smoke 759000–759999.

| batch | env | played by | commits | train seeds | runs | used | folder (under `data/defect/`) |
|---|---|---|---|---|---|---|---|
| v9 g0 | SL, A0 | plan c9-0 low budget (beam 4x150, replan 0), clone, L0 v9-0, `--asc 0`, `--explore` 0.3 / 0.1 / 0.02 (a job each), uniform draws only | `e905a6a` | 740000–745999 (see above) | planned (launch 2026-11-03) | – | `sl/selfplay/v9g0/e030`, `e010`, `e002` |

## Weights

The Defect has no weights of its own: it plays the nets all characters share, `weights/all/` (their rows are in the
Silent's ledger).
