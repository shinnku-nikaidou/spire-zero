# Audit D: learning/ and evaluation/ (English reference, twins checked)

Checked at HEAD `8d2b9ed` ("repo: prepare for going public ..."). NOTE: the history was rewritten while this audit ran,
and the rewrite remapped hashes inside files too: reference L90 `eae4d46` -> `84ecd81`, L134 `2d95172` -> `0cd5930`;
figure data `rev` `aeeb0e6` -> `e471ab5`, `1ef1008` -> `45ef371`, `1eb4eac` -> `f27d317`; report `config.commit` too.
Line numbers did not move. All hashes below are the current ones; the rule (drop them) is unchanged.

Facts used (checked in the repo): champion files `eval/champions/sl.json`/`nosl.json` name `weights/all/v9-2.json` and
`c9-2.json`; sl.json names five reports `eval/CHAR/reports/2026-11-06-sl-a0-promotion-1-100-plan-c9-2-l0-v9-2-clone-p0.json`
(won of 100: Silent 92, Ironclad 82, Defect 68, Regent 71, Necrobinder 47; mean floor 46.45 / 46.00 / 46.44 / 45.57 /
41.55; 0 void; 0 missing ends). c9-2/v9-2 fit: 21,272 runs, 4,310 held out (c9-2: 430,142 held-out round starts),
i.e. the medium-budget A0 data of all five characters, ε 0.3/0.1/0.02 (ledger rows 77-81). The current gate procedure
(from the now-untracked skill): champion's pair and new pair both play, 100 at medium on `promotion-1-100` per
character, same step, same commit, no low budget, no deadline.

---

### en/arch/learning/index.mdx  (lines 78)  -- FATE: EDIT
Reason: present-tense overview; three host/history phrases and a stale champion sentence.
Sections:
  - (intro + Status aside L16-20) -- keep (Status lists planned items: future)
  - §1 `#sec-1` What is learned -- edit (L32)
  - §2 `#sec-2` The loop in one picture -- keep (Stockfish/Fishtest is design rationale about Stockfish, not our hosts)
  - §3 `#sec-3` What is built and what is planned -- keep
  - §4 `#sec-4` This chapter -- edit (L76, L78)
Findings:
  - L32 host "in separate processes, on other machines, from logged runs" -> "in separate processes, from logged runs"
  - L32 history "A language model was to take part only offline" -> "A language model would take part only offline, ...; planned" (future)
  - L76 history/host "Reference: compute, ..., the ledger, weight names" -> "Reference: processes and outputs, the data directory, lists, seeds, weight names, ..." (match the reference rewrite)
  - L78 stale fact "two nets, V v9-1 and the combat net c9-1" -> "V v9-2 and the combat net c9-2" (champion files)
  - L78 history "the history of its nets and data is the ledger, `eval/ledger.md`" -> drop sentence
Inbound links to deleted/renumbered sections: none (no section deleted).
Figures/data: none imported.
Twins: match.
Future/next content: Status aside L18 (TD(λ), ExIt, LLM patches, shaping, privileged training, anti-local-optima);
  L32 LLM patches; mermaid L46-48 (TD/ExIt planned, SPSA/LLM); §3 table rows L64, L66, L67, L69, L70.
Rough size: 0 lines removed, 3 lines rewritten.

---

### en/arch/learning/combat-net.mdx  (lines 101)  -- FATE: EDIT
Reason: §1-§4 are present-tense method; the data paragraph and §5 are lineage and c9-1's numbers, not the champion's.
Sections:
  - §1 `#sec-1` What it predicts -- keep
  - §2 `#sec-2` What it reads -- keep
  - §3 `#sec-3` Samples and labels -- edit (L35 rewrite, L37 drop/recompute)
  - §4 `#sec-4` Loss and training -- edit (L42, L53, L57)
  - §5 `#sec-5` What it measures -- rewrite (all numbers from c9-2's `fit`; L99 and L101 dropped/rewritten)
Findings (procedure vs history separated):
  - L35 version/history "the 10,038 runs of `v8g2` ... c8-3 went on from c8-2 ... `v8g1` ... c8-1 ... `promotion-301-500`" -> rewrite as a present fact of the champion's file: "The data the champion's combat net learned from is whole SL self-play of all five characters at ascension 0, every combat fought at the medium budget: 21,272 runs; 430,142 held-out round starts." Keep the sentence "Self-play's seeds and the gates' never meet ... (`common.train_lists`)". Drop the c8 lineage and the `promotion-1-500` story (metrics §5 already says the file is retired).
  - L37 version/experiment "On the set `c7` of those weights, ... 98 of 1,479,991 heal ... 9.4 %" -> drop, or recompute on the champion's data without a set name
  - L42 version "--list data/silent/lists/v8g2 ... --test-list data/silent/lists/medium" -> "--list data/CHARACTER/lists/NAME --env SL --name NAME [--test-list LIST]"
  - L53 stale number "against a cross-entropy of about 1.24" (c9-1's 1.249) -> recompute from c9-2 (`ce` 1.320) or say "about 1.3"; check "0.051 potion rows ... 0.33 nats" too
  - L55 keep ("An experiment changes the code, and only a result keeps the change." is a working rule, not a story)
  - L57 history/story/version "c8-3 went on from c8-2 this way, on the 10,038 runs of `v8g2` alone: its best epoch was 2 of 6, ... 1.2913 to 1.2579" -> drop sentence (keep the `--init` mechanics)
  - L73 stale "The numbers are the report in `weights/all/c9-1.json` ... 6,004 runs ... 407,042 round starts, played at the low budget" -> "the report in `weights/all/c9-2.json`: 4,310 held-out runs, 430,142 round starts, medium budget"
  - L75-97 stale numbers: all are c9-1's (e.g. CE 1.249/2.352, deaths 9.27 %/10.03 %, rank 0.727/0.121, AUC 0.957/0.679). c9-2's: CE 1.320/2.374, deaths 4.50 %/4.65 %, rank_lost 0.725/0.133, AUC 0.931/0.691. Regenerate the room table, low-HP, Fairy in a Bottle and Knowledge Demon paragraphs from c9-2's `fit`.
  - L99 history/version "Against the net it went on from. ... c9-0 ... c9-1's 1.27 ..." -> drop paragraph (lineage)
  - L101 story/version "With V v9-1 ... won 46, 35, 31 and 9 ... where v9-0 with c9-0 won 1, 2, 1 and 0" -> rewrite: "Which of these translates to more won runs is for a gate to say (Gates §5); the champion's own results are in Champions and baselines §2." (optionally the five current win counts 92/82/68/71/47 of 100)
Inbound links to deleted/renumbered sections: none deleted. (§3 is linked from combat-brain/leaf, combat-brain/walkthrough, loop L74, reference L178; §4 from combat-brain/network, reference L178; §1 from metrics L184: all stay valid.)
Figures/data: none imported.
Twins: match (L35 zh-CN/zh-tw repeat "c8-2"/"c8-1" once more each: translation only, same content).
Future/next content: none.
Rough size: ~4 lines removed (L37, L57 sentence, L99, part of L35), ~6 lines rewritten, §5 tables (~20 lines) regenerated.

---

### en/arch/learning/loop.mdx  (lines 222)  -- FATE: EDIT
Reason: the method sections stand; generation names, budgets per dataset, tiers per dataset and a dead proxy are history.
Sections:
  - (intro, Status L13-17) -- keep (Status: future)
  - §1 `#sec-1` The loop as designed -- edit (L50)
  - §2 `#sec-2` Self-play -- edit (L56, L60, L62)
  - §3 `#sec-3` Fit: Monte Carlo targets -- edit (L66, L77, L79)
  - §4 `#sec-4` TD(λ) targets (planned) -- edit (L97, L101 with the regenerated figure)
  - §5 `#sec-5` Expert iteration (planned) -- keep
  - §6 `#sec-6` SPSA (not used) -- keep
  - §7 `#sec-7` LLM patch proposals (planned) -- edit (L130)
  - §8 `#sec-8` Gates -- edit (L136 stale procedure)
  - §9 `#sec-9` Exploration -- edit (L160, L162)
  - §10 `#sec-10` Credit assignment (+ "Counterfactual forks") -- keep
  - §11 `#sec-11` Potential-based shaping -- keep
  - §12 `#sec-12` Privileged information -- edit (L198)
  - §13 `#sec-13` Local optima -- keep
Findings:
  - L50 host "in processes and on machines separate from any decision" -> "in processes separate from any decision"; "an LLM was never to take part" -> "an LLM never takes part"
  - L56 version/history "The newest dataset is `v9g0`: 29,987 ... played by v9-0 and c9-0. The Silent won 1,981 ... V v9-1 and the combat net c9-1 went on from those two nets, and are the champions' nets" -> PROCEDURE to keep: "The champion plays, so a generation's data is played by the V and the combat net it follows: all five characters, at the champion's medium budget, about 6,000 runs a character." HISTORY to drop: dataset name, per-character wins and floors, v9-0/v9-1 lineage (also stale: the champion is v9-2/c9-2)
  - L60 host "Each generation's runs are one time-boxed job per exploration tier, under one deadline that can be moved while it plays (Reference §1)" -> drop sentence (keep the first sentence: newest generation only)
  - L62 version/history "`v7g1` was played at the low budget (...), `v7g2` and `v8g2` at the champion's medium, and `v9g0` at the low budget again; the gates play at medium, with the low budget as a reference" -> "Self-play plays the champion's medium budget, as the gates do (The SL planner: budget §2)." (also stale: gates no longer play low; owner to confirm medium is the procedure: ledger rows for the champion's data and the running generation say medium)
  - L66 history "(win 1, loss 0; or the proxy, the floor reached)" -> "(win 1, loss 0)"; E[floor] was deleted
  - L77 history "The design had a rule from r33hab ... never mix HP targets and win targets ... it lowered the held-out loss in every training seed it was tried with" -> keep as rationale only: "V's training adds the second head's squared errors to the hazards' loss at weight 3 (`learn/v/train.py:44`, `:144`), since that lowers the held-out loss." Drop the "design had a rule" narrative. (Citation `:139` is stale: AUX_WEIGHT is `:44`, used `:144`.)
  - L79 history/experiment "A proxy is accepted only through the gate. A dense target such as the floor reached is a stopgap ... GuanDan study ... none of its 5 pre-registered replication seeds" -> drop paragraph (no proxy target exists); if a rule is wanted: "A change is accepted only through the gate on real outcomes." (§8 already says it)
  - L97 history "seed `0SSL7NS01MU6` that an earlier V won on floor 48 ... That V valued a run in expected progress ... 48 + 1 = 49" -> regenerate the figure from the champion V (P(win), G = 1) and say "the champion's V"; if not regenerated, at least drop "earlier" (but the progress unit no longer exists in the system)
  - L101 numbers "V started at 33 ... 12.1 above ... 1.2 above" -> recompute with the regenerated figure
  - L130 host (private service) "through the project's LLM gateway" -> "through an LLM API"
  - L136 stale procedure "plays the 300 seeds of `promotion-1-300` (at ascension 10, the first 200 ...) at the champion's medium budget, and again at the low budget" -> "The champion's pair and the challenger's pair both play the 100 seeds of `promotion-1-100` with every character, at the champion's medium budget and the same commit (Evaluation: gates §10)."
  - L160 version/history "0.3, 0.15 and 0.05 in `v7g1`, 0.15, 0.08 and 0.03 in `v7g2`, 0.06, 0.03 and 0.01 in `v8g2`, and 0.3, 0.1 and 0.02 in `v9g0`" -> "A generation's runs are split into three tiers of ε, a third of the runs each: 0.3, 0.1 and 0.02." (procedure)
  - L162 experiment "0.161 at ε = 0.15 over 2,347 decisions in one test" -> keep the check rule, drop the old test's numbers
  - L198 untracked "(a local note, `tmp/notes/fair-play-spec.md`, not in git)" -> drop the pointer: "The project's fair-play rule allows it at training time, ..."
Inbound links to deleted/renumbered sections: none (no section deleted; §2-§13 keep their numbers).
Figures/data:
  - TdTargets -> `src/data/book/learning-td.json`: `source.rev` "f27d317" (hash), `source.run` "ce13ba5116b7f48a" (16-hex run id, hash-like), `source.job` "data/silent/sl/eval/2026-10-30-eval-v6-3-2-clone" (date+version), `source.values` names `weights/silent/v6-3-2.json` (old net, file not in git), `source.note` "V v6-3-2's value (expected progress)". Displayed: caption L99-101 shows the run id and "V is an earlier net, valued in progress" (history + a unit the system no longer has). Proposal: regenerate the series with `weights/all/v9-2.json` on the same example run (values = chance to win, outcome G = 1), caption "the champion's V"; drop the run id and the `source` hash/job/values fields.
  - ShapingToy -> clean (made-up routes, "not from a run").
Twins: match.
Future/next content: Status L15; §1 diagram/table planned rows (L26-31, L44-47); §4 whole (TD(λ)); §5 whole (ExIt, PCR, Reanalyse); §7 whole (LLM patches); L146 A/A test; L161 cut point (links macro-brain/next §3); L164 Thompson sampling; §10 items 2, 3, 5 and "Counterfactual forks"; §11 whole; §12 whole; §13 table planned rows.
Rough size: ~3 lines removed (L60 sentence, L79, L162 numbers), ~9 lines rewritten, figure regenerated.

---

### en/arch/learning/reference.mdx  (lines 231)  -- FATE: REWRITE (§1, §3, §4, §5, §6, §12; the rest EDIT/KEEP)
Reason: §1 is host infrastructure, §3-§6 carry the data/net lineage and the ledger; §2, §7-§11 are present-tense reference.
Sections:
  - (frontmatter L3, intro L10) -- edit (L3 "compute and time-boxed jobs ... the ledger and seed blocks" -> "processes and outputs, ... seeds")
  - §1 `#sec-1` Compute -- rewrite as "Processes and outputs" (keep the number: §3-§12 have inbound links)
  - §2 `#sec-2` The `learn/` project -- keep
  - §3 `#sec-3` The data directory -- rewrite (formats only)
  - §4 `#sec-4` Lists -- rewrite (format; drop the table of old lists)
  - §5 `#sec-5` The ledger and the seeds -- rewrite as "Seeds" (keep the number)
  - §6 `#sec-6` Weights -- edit (L85, L89, L90, L91)
  - §7 `#sec-7` Net files -- edit (L123 + figure)
  - §8 `#sec-8` Inference in C# -- edit (L134)
  - §9 `#sec-9` Scripts -- edit (L178; stale line citations, see cross-page)
  - §10 `#sec-10` Simulator commands the loop uses -- keep
  - §11 `#sec-11` Tests -- keep (59 tests confirmed by `pytest --collect-only`)
  - §12 `#sec-12` Open questions -- edit (L227, L228)
Findings:
  - L16-20 host (mermaid) "box job / remote hosts / storage hub zstd / GPU machine" -> PROPOSED §1: a local-only chain: `run --records --log` (the simulator, N processes, `--workers`) -> `data/CHARACTER/ENV/JOB/` (run records, decision logs, manifest) -> list pair `data/CHARACTER/lists/NAME-{logs,recs}.txt` -> `v.train` / `combat.train` (PyTorch, GPU when present) -> `weights/all/NAME.json` -> Brain (ONNX Runtime, CPU).
  - L23 host "43–52 s of a slot on one host and 83 s on another. Self-play and gates run on several remote hosts at once." -> keep only the process scaling (8 processes ~1,860 combats/s vs 275); drop the per-host times and the remote-hosts sentence (or one number: a whole SL run at medium takes 5–17 min of one process, from the champion's reports' `seconds_per_run`)
  - L24 host+skill "Time-boxed jobs ... `~/sts2/deadline/NAME.txt` ... `.claude/skills/fleet/SKILL.md` ... `.claude/skills/sl-selfplay/SKILL.md`" -> delete bullet
  - L25 host "sends a job's files to a storage host ... zstd ... GPU machine ... development machine ... `git archive REV learn/`" -> delete bullet (keep at most "a net's training code is the commit its `fit` was made at" if true; else nothing)
  - L26 keep (PyTorch on GPU when present; C# inference on CPU)
  - L40 host "The full files live on the storage host; the development machine keeps each job's `manifest.json` and small files." -> drop
  - L44 history "`selfplay/gN/bK/` an early self-play generation" -> drop row
  - L45 version/host "`v7g1/e030`, ... later boxes of a generation with a suffix" -> keep the format `selfplay/vNgM/eXXX/` (example `sl/selfplay/vNgM/e030`), "later batches" instead of "boxes"
  - L46 date/version/history "KIND is ... `gauntlet`, `gauntlet-ref`, `gauntlet-smoke` ... `sl/eval/2026-10-31-eval-v7-1f-clone`, `sl/eval/2026-10-31-train-v7`" -> keep only kinds still made (gate/eval/train...), drop gauntlet kinds (feature deleted); example as a pattern `sl/eval/DATE-eval-NAME`
  - L47 history/host "`forks/DATE-SUBJECT/` counterfactual forks, the boss gauntlet pilot, and tests ... `2026-10-31-deadline-test`" -> drop row (forks are planned, gauntlet deleted, deadline test is host)
  - L49 version "`void/sl/selfplay/g3/t025`" -> pattern `void/ENV/...`
  - L50 version "`lists/c7-logs.txt`" -> `lists/NAME-logs.txt`
  - L52 history "Older jobs keep one set per shard: ... if time-boxed, `done-SHARD.txt`" -> drop sentence; define "shard" as the process that played the chunk, not a host slot
  - L54 host "`inputs` ... `box`: the deadline (UTC and epoch, the file, its changes), ... each host's slots, speed, clock offset and queue. Fetching adds `done`, `prefix`, each slot's end ..." -> "The manifest says what played and why: job, kind, character, env, purpose, rev, game, options, status and the seed range." Drop `box`/fetch fields
  - L60-67 version/history (whole list table: `v8g2`, `v8g1`, `v7g1`, `c7`, `v-g0-g3`, `sl-combat`, with v8-3/c8-3/v8-2/c8-2/v8-1/c8-1) -> drop the table; keep the list-pair format (L58) and one line: "`medium`-style lists hold runs that are only reported (`--test-list`), never trained on"
  - L69 heading "The ledger and the seeds" -> "Seeds"
  - L71 history "`eval/ledger.md` ... the project's only history of its nets and data ... The session that runs the training loop keeps it" -> drop
  - L73 history (ledger paragraph on champions) -> drop
  - L74 version/history (seed blocks per dataset: `v7g1`, gauntlets, elite gauntlet, `v7g2`/`v8g1`, `v8g2`, `v8g3`, `v9g0`, next free block) -> PROCEDURE: "Train seeds are `run --seed 1 --start K` (`MakeSeed(1, i)`); each dataset plays its own block of 100,000 indices, so no two datasets share a seed; no train index below 40,134,134 meets an evaluation seed (Metrics §6)."
  - L75 host "`box.py` refuses anything else" -> drop clause; keep "Both trainers refuse a list holding an eval job's runs (`common.train_lists`)"
  - L76-77 history "a batch table ... a weights table" -> drop
  - L79 history "`eval/CHARACTER/gauntlet-leaderboard.md` holds the elite gauntlet's results, kept by hand" -> drop (gauntlet deleted); keep the seed files sentence; "whose last 200 seeds are in training data" may stay (present fact)
  - L83 history "the champion files and the ledger are every character's, `eval/champions/` and `eval/ledger.md`" -> drop "and the ledger"
  - L85 stale + lineage "holds `v9-1.json` ... `c9-1.json`, which both champions ... play ...: the first nets trained on every character's runs (`v9g0`), going on from `v9-0.json` and `c9-0.json` ..., whose weights are those of v8-3 and c8-3" -> "`weights/all/` holds V's files `v9-0.json`–`v9-2.json` and the combat net's `c9-0.json`–`c9-2.json`; both champion files name `v9-2.json` and `c9-2.json` for every character (Champions §1)."
  - L89 version "(`v7-1f`)" -> placeholder `vN-Ma`; "`vNgM` names a generation's data, never a net" may stay (naming rule)
  - L90 hash/history "Earlier net files are in git's history (the last commit holding them is `84ecd81`), and `eval/ledger.md` says what each was" -> drop sentence
  - L91 stale "c9-1's weights learned from SL's games" -> "The champion's combat net learned from SL's games; ..."
  - L123 stale "The figure opens the two net files, v9-1.json and c9-1.json" -> "the champion's two net files, v9-2.json and c9-2.json" (after regenerating the data)
  - L134 hash/history "An earlier V of the same structure takes 0.093 ms a call ... (commit `0cd5930`)" -> re-measure with v9-2 and state it, or drop the bullet; V's inputs have grown since, so "of the same structure" is doubtful
  - L178 version "`--list data/silent/lists/v8g2`" -> `--list data/CHARACTER/lists/NAME`
  - L227 history/version/story "as v8-3 and c8-3 they were gated with the Silent at ascension 10, V passed ..., lower 95 % −0.26 ..." -> "What the champion's nets are worth in NoSL: neither has been trained or gated there." (future/open)
  - L228 future "How much a net that learns all five characters keeps ... only data of all five can say" -> the nets already learn all five; rewrite to the open part ("whether the net needs to be wider for five characters") or drop
Inbound links to deleted/renumbered sections (none renumbered if §1/§5 keep their numbers and titles change):
  - `#sec-1` (rewritten): loop L60 (sentence dropped), status.mdx L31 (other group; its "compute" row is itself host content)
  - `#sec-3`: champions L159, metrics L154, harness/decision-log L19 -- still valid (formats stay)
  - `#sec-4`: combat-net L25 -- valid
  - `#sec-5` (retitled "Seeds"): loop L54 (valid), loop L56 (sentence dropped), metrics L102, L104 (valid)
  - `#sec-6`: glossary L123, champions L52, combat-net L69, gates L166 -- valid
  - `#sec-12`: macro-brain/next -- valid
Figures/data:
  - NetFileAnatomy -> `src/data/book/learning-netfiles.json`: `source.rev` "45ef371" (hash) -> drop; data is v9-1/c9-1 (files in git, but not the champion's). Component hard-codes the names: FILES keys and default `which` "v9-1"/"c9-1" (NetFileAnatomy.svelte L23-24), NOTE.name "(l0 v9-1, plan c9-1)" (L32), caption "Extracted from weights/all/v9-1.json and c9-1.json at the page's commit" (L139-141). Proposal: re-extract from `v9-2.json`/`c9-2.json`, key the JSON by role (`v`, `c`), print `file.file`/`name` from the data so the component holds no net name; caption "Extracted from the champion's two net files". The fixed note "No run is at an ascension above 0 in the data, so its standard deviation reads as 1" (L91) still holds for v9-2 (std 1.0) but should be computed from the data (nets trained on A0-A10 data will break it).
Twins: match.
Future/next content: §11 "Planned" L219-223 (calibration plot, shaping test, A/A); §12 open questions L225-231.
Rough size: ~30 lines removed (§1 bullets, §3 rows, §4 table, §5 ledger text), ~15 lines rewritten, figure data regenerated.

---

### en/arch/evaluation/index.mdx  (lines 107)  -- FATE: EDIT
Reason: chapter overview is present-tense; one false status line, one untracked pointer, one old experiment, stale gate procedure.
Sections:
  - (intro, Status L16-20) -- keep (Status: future)
  - §1 `#sec-1` Two questions -- edit (L24)
  - §2 `#sec-2` The sim-to-real ladder -- keep
  - §3 `#sec-3` Two leaderboards -- edit (L83)
  - §4 `#sec-4` Where it stands -- edit (L99)
  - §5 `#sec-5` This chapter -- edit (L107)
Findings:
  - L24 stale "A report of the current champion is pending." -> drop (sl.json names five reports of the champion)
  - L83 untracked "(`tmp/notes/refs/strong-bots.md:42`)" -> drop; cite spire-mentor itself
  - L83 experiment/history "The nearest pair is one earlier V on `promotion-1`: SL with the planner 28.25, NoSL with PIMC 26.16, +2.09 floors ..." -> drop sentence
  - L99 stale procedure "plays the challenger on the 300 seeds of `promotion-1-300` ... The same seeds at the low budget are played for reference ... If both nets of a generation pass, the one with more wins is promoted" -> "Both pairs, the champion's and the new one, play the 100 seeds of `promotion-1-100` with every character at the champion's medium budget (`--afterstates clone`, no exploration) at the same commit; each character's runs are paired by seed, and the new pair is promoted when the five together say so (Gates §10)."
  - L107 history "where the history and the reports live" -> "the champion's reports"
Inbound links to deleted/renumbered sections: none.
Figures/data: none imported.
Twins: match.
Future/next content: Status L18 (R2-R4, act-level gate, A/A, NumReloads, NoSL save-export count, calibration, search compute, final set); §2 table R3/R4 L50-51; §4 rows L96-97.
Rough size: 2 sentences removed, 3 lines rewritten.

---

### en/arch/evaluation/metrics.mdx  (lines 210)  -- FATE: EDIT
Reason: the reference content (metrics, Wilson, seeds, report/record/leaderboard formats) is current; the worked example is an old champion's report, plus seed-table history and a fixed-bug narrative.
Sections:
  - (intro L11) -- edit (example report)
  - §1 `#sec-1` The main metric -- edit (L17, L19 numbers)
  - §2 `#sec-2` What must be zero -- edit (L34)
  - §3 `#sec-3` Secondary metrics -- edit (table L49-54 numbers)
  - §4 `#sec-4` The Wilson interval -- keep (L76 format example may stay)
  - §5 `#sec-5` Seed sets -- edit (table "used for" column, L102-105)
  - §6 `#sec-6` How the seeds are made -- edit (L123 "the ledger")
  - §7 `#sec-7` The report -- edit (L165)
  - §8 `#sec-8` The run record -- edit (L184, L186, L188)
  - §9 `#sec-9` The leaderboard -- edit (L205)
  - §10 `#sec-10` Tests -- keep (L209 `abc1234` may stay, see below)
Findings:
  - L11 history "the example report: an earlier champion on `promotion-1-300`, 23 won of 300" -> switch the worked example to one of the champion's reports (e.g. the Necrobinder's, 47 of 100, 53 losses; or the five together, 500 runs) and recompute L17, L19, L49-54, L188 and the figure; if kept, at least drop "earlier champion"
  - L17 / L19 numbers from the old example (65 reached the act 3 boss, 23 won, 42 died; 290 of 300 entered the act 1 boss, 63.4 HP) -> recompute
  - L34 host/story "A run fails because of the host, such as a machine whose memory flips bits" -> "A run fails for reasons outside the brain (a crashed process, a machine fault)"
  - L49-54 numbers (1.08, 29.7 %, 208, 271.2 s, 488.6 ms, surfaces) -> recompute from the new example
  - L56 host (generic) "Wall time depends on the host and its load" -> "on the machine and its load"
  - L94 history "`promotion-1.txt` ... used for: the older reports" -> "reports on 200 seeds" (or "–")
  - L95 stale "`promotion-1-300.txt` ... the wins gate, at medium and at the low budget" -> "reports on 300 seeds"
  - L96 stale "`promotion-1-100.txt` ... runs on part of that set; a report on the first part" -> "the wins gate, every character, and the champion's reports"
  - L97 history "`promotion-1-200.txt` ... the reports of the A10 gate" -> "reports on 200 seeds"
  - L102 host "the job launcher `box.py` refuses anything else" -> drop clause; keep "and training refuses a list holding an eval job's runs"
  - L103 version/history "in the data of earlier combat nets, and so in the line of c8-1, c8-2, c8-3, c9-0 and c9-1. Results reported on it stand as history." -> "its last 200 seeds are in data the champion's combat net has learned from, so it no longer tests"
  - L104 history "The ledger hands them out in blocks of 100,000, one block per batch ... (`eval/ledger.md`, ...)" -> "Each dataset plays its own block of 100,000 indices, so no two datasets share a seed (Learning: reference §5)."
  - L105 history "`promotion-1.txt`'s header says to rotate to a `promotion-2` (base 4) ...; no `promotion-2` exists, and base 4 went to `a10-check-1`" -> drop (or fix the seed file's header instead)
  - L123 history "for any block the ledger will hand out" -> "for any train index below 40,134,134"
  - L165 history "(`r1` in older names, at ascension 0)" -> drop; also the example `...-clone-first` matches no file (the champion's reports end `-clone-p0`), see cross-page
  - L184 history "The oldest committed reports have no `end` at all." -> drop
  - L186 history/bug "The committed reports at ascension 0 were played by code that wrote no `end` for a death inside a decision's own action: ... 8 of its 277 losses. The reports at ascension 10 have none." -> drop (the champion's reports have 0 missing ends); keep the first part of the paragraph (when an `end` is missing)
  - L188 numbers from the old example ("277 losses: 133 at a boss ...; ... 10 outside combat, 8 of them deaths in a fight"; bosses beaten 276/290, 94/171, 23/65) -> recompute; the missing-end sentence goes with L186
  - L205 stale "The board, `eval/silent/leaderboard.md`" -> "Each character's board, `eval/CHARACTER/leaderboard.md`"
  - L209 `abc1234`: it is the test fixture's `RunConfig` commit (`src/Sts2Ai.Tests/Fixtures.cs:31`, the page says `:32`), a made-up placeholder, not a real commit: a format example that MAY STAY; suggest "a placeholder commit, `abc1234`" so a hash sweep does not flag it, and fix `:32` -> `:31`
Inbound links to deleted/renumbered sections: none.
Figures/data:
  - WilsonInterval (no data file): presets 23/300, 13/300, 1/300, 0/200 are unnamed, FableBot 71/343; caption L76 "three earlier nets' whole-run reports on promotion-1-300, one on promotion-1 with no win" -> history wording; rewrite "Presets: small win counts like those of early reports, ..." -> better "example counts" or the champion's five reports' counts plus the made-up small ones.
  - DeathsByFloor -> `src/data/book/eval-reports.json`: `source.rev` "e471ab5" (hash), `source.files` "eval/silent/reports/2026-10-30-sl-r1-promotion-1-300-plan-c2-l0-v6-3-2-clone-p0.json" (date + old nets; not displayed). Displayed caption L80: "(an earlier champion, SL, promotion-1-300)" and "8 of these deaths have no end in the record, and learn/gate.py counts those as outside combat" (fixed-bug history). Proposal: regenerate from the new example report (noEnd 0), caption "the champion's report, CHARACTER, SL, promotion-1-100", show the noEnd clause only when > 0; drop `rev`.
Twins: match.
Future/next content: §2 L38-41 (`NumReloads`, NoSL save exports); §3 L58-62 (calibration, search compute, potion hoarding); L99/L106 `final-1` never run (fact, not a plan).
Rough size: ~6 sentences removed, ~10 lines rewritten, ~12 numbers recomputed, figure data regenerated.

---

### en/arch/evaluation/gates.mdx  (lines 224)  -- FATE: EDIT
Reason: the statistics and both gates are present-tense; two skill pointers, the time-box paragraph, two stories and a stale procedure.
Sections:
  - §1 `#sec-1` Why a gate, and why pair -- keep (L15 time per run: see cross-page)
  - §2 `#sec-2` Common random numbers -- edit (L34, L44 wording)
  - §3 `#sec-3` The SPRT -- edit (L75 wording)
  - §4 `#sec-4` McNemar's test and A/A tests -- edit (L90 wording, L94 parenthetical)
  - §5 `#sec-5` The layered gates -- keep
  - §6 `#sec-6` The whole-run gate -- edit (L128; delete L153)
  - §7 `#sec-7` Not worse -- edit (L159)
  - §8 `#sec-8` Promotion -- keep
  - §9 `#sec-9` Re-baselining -- edit (delete L175)
  - §10 `#sec-10` The wins gate -- edit (L212, L214, L216)
  - §11 `#sec-11` Tests -- keep
Findings:
  - L34 history wording "A, B and C are three earlier nets ...; D and E are two earlier NoSL champions" -> "three nets ... two NoSL brains" (anonymous illustration on committed reports; keep the measurements as an illustration, or recompute on the champion's and the previous pair's reports on `promotion-1-100`)
  - L44 history "two earlier nets on `promotion-1-500`, a set since retired" -> drop the sentence or say "two nets on `promotion-1-500`"
  - L75 history wording "the later net as the challenger" -> "net C (or B) as the challenger"; "no such gate was run" may stay
  - L90 wording "two earlier nets on `promotion-1-300`" -> "nets C and A"
  - L94 story "(one re-baseline played a champion again and all 200 seeds reached the same floor, §9)" -> drop the parenthetical
  - L128 host "A time-boxed challenger that finished only the head of the set" -> "A challenger that played only the head of the set"
  - L153 host+skill "Time boxes. Evaluations run in time boxes: ... (`.claude/skills/sl-selfplay/SKILL.md:115-119`) ... same machines and the same box" -> DELETE the paragraph. The pointer becomes nothing: a gate no longer has a deadline (each side plays the whole seed file), and §8 already says a promotion needs the whole set. If the rationale is wanted, one in-page sentence: "Each side plays the whole seed file: a set cut short would lean towards short runs, since runs that die early finish first."
  - L159 story "The SL planner's medium effort, its default budget, passed such a gate, on floors, after 33 pairs" -> "The SL planner's medium budget is such a case (The SL planner: budget §1)." or drop
  - L175 history/experiment "An earlier NoSL champion shows the size of the effect ... 21.88 to 22.70 ... After a later pair of small fixes, all 200 seeds reached the same floor." -> delete the paragraph (keep L173)
  - L212 skill "in the same time box (`.claude/skills/sl-selfplay/SKILL.md:75-80`)" -> state the procedure in the page, pointing only at code in git: "The procedure keeps those: the champion's pair and the challenger's pair play the same seed file at the same commit, each with the champion file's options and only `--value` and `--combat-net` changed (no `--explore`), one job per character and side:" + code block `run --champion eval/champions/sl.json --character C --asc 0 --seeds eval/seeds/promotion-1-100.txt --value weights/all/V.json --combat-net weights/all/CN.json --records R --log L`, then `uv run python gate.py --records R --logs L --against R_CHAMPION` (`learn/gate.py`)
  - L214 stale procedure "each at the ascension its data was played at: 200 seeds at the low budget, `promotion-1-200`, and 100 at medium, `promotion-1-100`" -> "100 seeds at medium, `promotion-1-100`, both pairs playing at the same commit, so neither side reuses older runs"; "The champion's own gate is on Champions and baselines §2" -> "The champion's reports are in Champions and baselines §2"
  - L216 history "the ledger records it" -> drop clause
  - L218 keep (illustration on committed reports, "no such gate ran")
Inbound links to deleted/renumbered sections: none renumbered (no heading deleted). §9 (paragraph removed) is linked from champions L155: still valid.
Figures/data:
  - PairedScatter and SprtReplay -> `src/data/book/eval-pairs.json`: `source.rev` "e471ab5" (hash) -> drop; `source.files` six report paths with dates 2026-10-28/30 and old names (`v6-2`, `v6-3-1`, `v6-3-2`, `c2`, `pimc-c2-l0-n2-b0.1`) -> not displayed; drop or replace with "committed reports on promotion-1-300 and promotion-1"; if the old reports leave the repo, rebuild from the champion's and the previous pair's five reports on `promotion-1-100`. Displayed labels are "net A".."net E" + env + seed set: clean. SprtReplay caption L196 "the later net as the challenger" -> "net C or B as the challenger". PairedScatter caption L130: clean.
Twins: match (zh-CN/zh-tw L153 and L212 carry the same `.claude/` pointers).
Future/next content: §4 McNemar (L92) and A/A test (L94) planned; §5 act-level gate row planned (L101).
Rough size: 2 paragraphs removed (L153, L175), ~9 lines rewritten.

---

### en/arch/evaluation/champions.mdx  (lines 92)  -- FATE: EDIT (§2 REWRITE)
Reason: §1 is already present tense (v9-2/c9-2); §2 is a champion lineage (v9-0 -> v9-1, and stale: the champion is v9-2); §3 cites untracked notes.
Sections:
  - (frontmatter L3, intro L8) -- edit ("where the history and the reports live" -> "the champion's reports")
  - §1 `#sec-1` The champion file -- edit (L44 wording, L52 ledger)
  - §2 `#sec-2` History and reports -- rewrite as "Reports"
  - §3 `#sec-3` Baselines -- edit (L77, L82, L84)
  - §4 `#sec-4` Open questions -- edit (L91)
Findings (page lines):
  - L3 / L8 history "where the history and the reports live" / "says where the record of results is kept" -> "the champion's reports"
  - L44 host-ish "(`brain` has no `--env`: each host names its own)" -> "(`brain` has no `--env`: the caller gives it)" (check meaning: the brain serves both environments)
  - L52 history "the ledger `eval/ledger.md`" -> drop from the list of shared things
  - L56-58 history "These pages describe the current champion only. The record of what came before is kept apart: `eval/ledger.md` is the history ..." -> drop
  - L62-74 history/version/story (the v9-0 + c9-0 vs v9-1 + c9-1 gate, its table, low-budget counts, mean floors before/after) -> delete
  - L78 untracked "From the project's local notes on prior art" -> "From published results"
  - L82 untracked "(`tmp/notes/refs/prior-art-rl.md:343`)" -> drop pointer (cite FableBot's published runs)
  - L84 untracked "`tmp/notes/refs/strong-bots.md:42`" -> drop pointer
  - L91 history "so that the NoSL board has a champion again" -> "has a champion"
PROPOSED present-tense §2 "Reports" (`#sec-2` kept):
  - `eval/CHARACTER/reports/` holds a character's committed reports, JSON and Markdown (Metrics §7); `eval/CHARACTER/leaderboard.md` ranks them (Metrics §9).
  - The champion's reports are the five `sl.json` names, one per character, each 100 runs at medium, ascension 0, `promotion-1-100`; they are the champion's side of every SPRT gate. Table (from the reports): Silent 92 of 100 (floor 46.45), Ironclad 82 (46.00), Defect 68 (46.44), Regent 71 (45.57), Necrobinder 47 (41.55), with Wilson intervals.
  - How a promotion rewrites the files: `gate --promote` writes every field (§1); a pair the wins gate passes is written by hand into both files: `value`, `combat-net` and the `champion` name to the new pair's files in `weights/all/`, `reports` to the new pair's five reports from the gate's own runs, `since` to the date. Old net files stay in `weights/all/` (a name is never reused, Learning: reference §6). The NoSL file names no report.
  - Where: `eval/champions/sl.json`, `eval/champions/nosl.json`; reports and boards under `eval/CHARACTER/`.
  - `eval/ledger.md`: history (lineage, 65 seven-hex hashes, 16 host-name hits, dates, quoted decisions) -> the docs should NOT link to it; whether it stays in the public repo is a repo decision.
Inbound links to deleted/renumbered sections (§2 retitled, content replaced):
  - gates L214 (rewritten above), combat-net L101 (rewritten above), reference L227 (rewritten above)
  - macro-brain/selfplay L79 "The champion's V, v9-1, passed its gate with c9-1 ... (§2)" -> other group: stale and lineage; reword to "The champion's results are in ... §2"
  - sl-planner/budget L77 "Now. The SL champion is the planner ... with c9-1 ... won 74 ..." -> other group: stale numbers; target still fits (the champion's reports)
  - status.mdx L11, L75, L81 -> other group (status page is lineage by nature)
Figures/data: none imported.
Twins: match.
Future/next content: §4 open questions L88-92 (R4 throughput, minimum discordant seeds, error rates, NoSL report, `final-1`).
Rough size: ~20 lines removed (§2 lineage), ~10 lines rewritten/added.

---

## Cross-page issues

1. **Stale champion.** The champion files name v9-2/c9-2, but these still call v9-1/c9-1 the champion's nets: learning/index L78; combat-net L73 and all §5 numbers (they are c9-1's `fit`, not c9-2's); loop L56; reference L85, L91, L123 and NetFileAnatomy; champions §2. Outside this group: status.mdx L11, macro-brain/selfplay L79, sl-planner/budget L77.
2. **Stale gate procedure in four places.** loop §8 L136, evaluation/index L99, gates §10 L214 and metrics §5 L95-97 still describe `promotion-1-300` and/or a low-budget part. The current procedure has both pairs play 100 at medium on `promotion-1-100` per character, in the same step and at the same commit, with no low budget. The procedure should be stated once (gates §10) and linked from loop §8 and evaluation §4; reference §9's `learn/gate.py` block repeats gates §10 too.
3. **Self-play procedure.** loop L62 says self-play is at the low budget. The champion's data and the running generation were played at medium with ε 0.3/0.1/0.02 (ledger rows 77-82). The now-untracked skill contradicts itself: a bullet says tiers 0.3/0.15/0.05, its command says 0.3/0.1/0.02. The owner should confirm which values the docs state.
4. **The champion's name does not match its reports.** `sl.json` says "combat prompts first", while its five reports' `config.champion` say "combat prompts p0" and were played before the change that `since: 2026-11-07` marks. By gates §9 the reports need a re-baseline. Also, metrics L165's example name `...-clone-first` matches no file (the files end in `-clone-p0`).
5. **False status line.** evaluation/index L24 says "A report of the current champion is pending", but five exist.
6. **One board per character.** metrics L205 names a single board, `eval/silent/leaderboard.md`. There is one per character.
7. **Line citations that drifted.** `learn/v/train.py`: loop L77 cites `:139` (AUX_WEIGHT is at `:44` and used at `:144`). Reference §9 cites `:46-58` (main is at `:48`), constants `:39-43` (now `:41-44`) and the report at `:165-177` (now about `:167-185`). Metrics L60 cites `:160-177`. Metrics L209 cites `Fixtures.cs:32`, which should be `:31`. When run with `--init`, v.train's report also has `init_held_out_loss` and `init_brier_held_out`, which reference §9's table lacks. Combat citations were spot-checked and hold.
8. **Run time.** gates L15 says "about five minutes each at medium". The champion's reports give `seconds_per_run` 286 (Ironclad), 336 (Silent), 423 (Defect), 906 (Regent) and 1,046 (Necrobinder). Reference L134's "same structure" timing is from an older V with fewer inputs.
9. **`eval/ledger.md` is history.** It holds lineage, hashes, host names, dates and "the user's word". The docs link it in learning/index L78, reference L71-77/L83/L90, metrics L104/L123, champions L52/L58 and gates L216; all of those should go. Whether the file itself goes public is a repo decision. `eval/silent/gauntlet-leaderboard.md` (deleted feature, still tracked) is in the same position.
10. **The figures' source reports.** gates §2/§3/§10, the Wilson presets and DeathsByFloor rest on 2026-10-28/30 reports of old nets in `eval/silent/reports/` (78 files, names with dates and v6/c2). Each report's `config.commit` holds a SHA, and the leaderboards print a commit column. If old reports leave the repo, rebuild the figures from the champion's five reports and the previous pair's five reports on `promotion-1-100`.
11. **`.claude/skills/` is gitignored since `8d2b9ed`.** So gates L153/L212 and reference L24 already point outside git.
12. **Overlap.** reference §5 (Seeds) and metrics §5 overlap on the train/test rule and the retired set. reference §1 after the rewrite is short enough to merge into §2 if renumbering is acceptable (it is not, cheaply: §3-§12 have about 20 inbound links). learning/index §3 and loop §1's table list the same built/planned status.
13. **Docstring and code disagree.** reference L164 notes that `learn/v/train.py`'s docstring says early stopping weighs the second head like the loss, but the code stops on the hazard loss alone. This still holds; fix the docstring or the code.
