# DONE-C: NoSL PIMC + macro brain (group C)

All 30 files edited (10 pages x en, zh-CN root, zh-TW), the same changes in each twin. Checked after editing: the three
locales of every page have the same numbered headings, the same link targets in the same order and the same code spans.
No heading renumbered or retitled except pimc/budget §6 (D4). Nothing built, nothing committed.

## Per page

**pimc/index** (EDIT). §1: dropped the combat net's intent claim (57.7 % / 28.9 %; the combat net has no intent head,
`learn/combat/net.py:204-205`). §3: dropped the date; "still approximates" -> "approximates"; the five-failed-runs /
three-fixes story replaced by the rule: a world not built as observed fails the decision (`SimWorlds.NoSL`,
`SimWorlds.cs:41-47`), linked to harness/routing §6. §4: "the mod will" -> "the mod does". §6 table: "results" ->
"the champion". Kept on purpose (D1): description, badge "built, redesign planned", the "Built and planned" Aside, the
planned counterfactual test (§4), planned §5. Kept per the plan row: §3's fidelity table (without record -> with record).

**pimc/search** (EDIT). §1: dropped the dated probe paragraph (990 decisions, 376,000 states ...), kept the keying
rationale. §3: "with an earlier V and combat net" dropped. §4: "held in reserve until" -> "is not used unless fusion
is measured to matter"; the measurement paragraph kept as planned and marked "not built" (no such tool in `src/`); the
"first try on 36 decisions" result and its "a complex turn took about a minute" timing dropped. Planned §7 kept.

**pimc/budget** (REWRITE). Description: "what it has measured" -> "the NoSL champion". §1: "nothing has tuned them yet"
-> "untuned". §2: the failed-gate paragraph (27 % / 31 % / −0.72 floors) deleted. §4: the dated boss-budget gate
deleted; spire-mentor kept as the argument; "whether more nodes in boss fights pay is open (§7)". §5: the duplicate
timing (68.7 vs 307.5 ms) dropped; one timing stays in §1 (0.55–0.75 s one core, about 70 ms with 8 workers). §6
retitled "The champion": the NoSL champion (`eval/champions/nosl.json`) is PIMC 8 x 300 with the champion's combat
net, L0 with the champion's V on afterstates predicted from the screen; same nets as SL, not trained or gated in NoSL;
names no report, so a NoSL gate refuses every character and no leaderboard marks a NoSL champion. The panel table,
the whole-run table and their history deleted. §7: item 1 adds "every room" (§4), item 2 "not measured", item 6
rewritten as "No NoSL report" (links §6). Planned §8 kept. Left on purpose: the page title 'Budget, tools and results'
(the plan names only §6's title; suggest 'Budget, tools and the champion' if the lead agrees, x3 locales).

**macro-brain/index** (KEEP + 1 edit). Status aside: "where run worlds do not exist yet" dropped. The "Planned: ..."
sentence and the badge stay (D1, D11).

**macro-brain/afterstates** (1 edit). §3 table, NoSL column: "predicted from the screen (Search §1); a mean over K
sampled worlds is planned" (was only "(planned)", which left out what NoSL does). §4's distillation sentence kept.

**macro-brain/search** (EDIT). §1: "Until run worlds exist" -> "NoSL has no world to play an option in (run worlds are
planned, ...)". §6: one cost number kept (70–90 ms on the clone vs 1–2 ms from the screen, dropped "at the low
budget"); the 40 ms / 187 s line dropped; the `run --check` sentence says what it checks (each seed again over the
wire, observations, records and decision logs compared, every node replayed from its checkpoint; any difference or
game error fails the batch: `RunCommand.cs:12-14, 135-140`), links simulator/reference §6.

**macro-brain/network** (EDIT). §6 "What the file holds": the champion's file `weights/all/v9-2.json`, its `fit`
(21,272 runs, 5,608 won, 4,310 held out; all five characters, A0, medium); row counts re-checked on v9-2's ONNX
(damage 170, block 68, amounts 20; relic counter / status / melted all zero; the body's last 256 columns zero); μ/σ
as a fact ("come from runs at ascension 0": ascension μ 0, σ 1); dropped "went on from v9-0", "every width has
learned", "kept from the first net of its line" and the first-half / second-half story of unlearned rows (now: "its row
is still the one it started with"). §7: "mostly below 0.1" -> "can be a few hundredths, late in a hard run" (v9-2's 8
test values are 0.04–0.79). §9: only "v9-1's file" -> "a net file's test runs" (brief).

**macro-brain/value** (EDIT). §2: "286 of the 21,275 runs" -> "most runs are lost: about three in four of the runs the
champion's V learned from" (5,608 / 21,272 won), link moved to network §6; "or its time box ended" -> "truncated at
`run --max-decisions`" (see disagreements). §5: "an earlier net file" -> "a net file". §6: added the report row
`init_held_out_loss`, `init_brier_held_out` (`learn/v/train.py:184`, present in v9-2's `fit`).

**macro-brain/selfplay** (REWRITE, the one home of the procedure). §1 mermaid: the champion's pair, `v.train,
combat.train`, "100 paired seeds per character", edge "passes". §2: the command `run --champion eval/champions/sl.json
--character CHARACTER --asc N --workers 4 --explore ε --seed 1 --start K --n RUNS --log DIR/log.jsonl --records
DIR/rec.jsonl`, each option explained (`--champion` merge: `Args.cs:180-195`); budget = the champion's medium, argued
from V's definition (value §1); the low-budget correlation and every generation's budget dropped. §3: tiers 0.3 / 0.1
/ 0.02, a third of the runs each; per-generation tiers dropped. §4: a generation = one job per character, tier and
ascension (5 x 3 x 11), runs not fixed (no deadline, no hosts, no ledger); seeds (`--seed 1 --start K`, a block of
100,000 per generation, own range per job, eval files from other bases, `common.train_lists` refuses eval runs); the
list pair; training with both commands and `--init ../VALUE` / `../COMBAT_NET` read from the champion file, `NAME`
never reused; promote both nets in both champion files. Size table deleted. §5: one paragraph and a link to
evaluation/gates §10 (no formula, no seed sets, no "v9-1 passed its gate"); kept the why (Brier cannot decide).

**macro-brain/next** (EDIT). §1: the stale route-encoder limitation -> "the map bag holds no links, and the map ends
with the act" (network §8). §2: "NoSL has no run worlds yet" -> "Run worlds are not built"; the code block's
`docs: harness §4` -> `docs: harness/routing §1`; test 1 without "the fallback count is 0". §2, §3 kept (D1).

## For the figures step

- network §9: the sentence "a Defect run at ascension 0: the afterstate of a map move on floor 42, into an unknown
  room" is tied to `NetWalk.svelte` (hard-coded from v9-1's `tests[0]`). Regenerated from v9-2's `tests[0]` (Defect,
  act HIVE, floor 24, value 0.1716) the floor and room in that sentence change, x3 locales. NetWalk's "Six numbers"
  step text ("kept from the nets it went on from ...") is lineage, in the component.
- pimc/search §3 and pimc/index §2: WorldsTable / WorldSampler prose (Terror Eel, Leg Sweep 0.1708 ± 0.0113, the
  correlations) comes from `combat-examples.json` `nosl`; only "with an earlier V and combat net" was dropped.
- macro-brain/index §1 "181 in one won run" is the book's example run.

## Cross-page links others must change

- evaluation/gates.mdx:100 (group D, x3) cites "[NoSL PIMC: budget §6]" for panel results: §6 is now the NoSL
  champion, with no panel. Drop that link or retarget.
- learning/loop.mdx:140 (group D, x3) cites selfplay §5 for the lower-bound rule: selfplay §5 no longer states it;
  link evaluation/gates §10.
- sl-planner/budget.mdx:29 (group B, x3) says V's and the combat net's data are played at a cheaper budget and links
  selfplay §2: selfplay §2 now says the champion's medium budget.
- evaluation/index.mdx:99 (group D) links selfplay §1 and still says "the 300 seeds of promotion-1-300".
- Selfplay now relies on: learning/reference §3 (the job folder under `data/CHARACTER/sl/`), §4 (the list pair), §5
  (train seeds, the 100,000 block, `common.train_lists`), §6 (a name never reused); evaluation/gates §10 (the gate
  procedure and its commands); simulator/reference §1 (build and run). Their rewrites should keep those facts there.
- New outbound links: pimc/index §3 -> harness/routing §6; macro search §6 -> simulator/reference §6; value §2 ->
  network §6; network §6 -> value §6; pimc/budget §6 -> evaluation/champions §1.
- combat-brain/index's links to pimc/budget §6 (audit lines 79, 160) are already gone in the working tree.

## Where the code disagreed with FACTS.md or the audit

- FACTS "Mermaid labels ... are English in all three locales (as today)": not so today. The zh-CN and zh-TW mermaid
  labels are localized (selfplay §1, macro-brain/index §2 and the other zh pages with mermaid). I kept selfplay's zh
  labels localized, as the rest of the zh pages are.
- Audit (value L41) proposed "its job was stopped": a run cut off by stopping its process writes no record (the record
  is written when the run ends, `RunCommand.cs:252`), and `learn/v/data.py:71` makes `censored` any outcome other than
  won / lost, i.e. `truncated` (at `--max-decisions`). Written as "truncated at `run --max-decisions`".
- Audit proposed deleting pimc/index §5, pimc/search §7, pimc/budget §8 and next §2-§3: FACTS D1 keeps them; kept.
- `Args.cs:212`, the `--explore` help text, says "every other screen's options but the potion belt's"; the brain draws
  a potion's option too (`MacroBrain.cs:16-17`, `Offered`), as selfplay §3 says. The help text is stale.
- `RunNet.cs:70`, comment: "a 'win' net's values are mostly below 0.01"; v9-2's test values are 0.04–0.79.
- `RunCommand.cs:11`, comment: "Combat prompts go to p0" (already in the plan's §9 list).
- Not re-measured at HEAD (kept as the pages' one number): macro search §6 / network §6 "70–90 ms on the clone, 1–2 ms
  from the screen", network §6 "0.08 ms a V call"; pimc/budget §1 "0.55–0.75 s on one core, about 70 ms with 8
  workers"; pimc/index §3's fidelity table (simulator/combat §6, group E, may turn it into one column; pimc/index §3 and
  pimc/budget §8 still quote 82–89 % -> 96–98 %).
- FYI, not in a page: v9-2's first body layer also has 6 zero deck columns (3 dead φ units, in both the mean and the
  sum halves).
