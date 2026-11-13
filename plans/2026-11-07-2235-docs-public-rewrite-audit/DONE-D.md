# DONE, group D: learning/ and evaluation/

Pages: learning/index, combat-net, loop, reference; evaluation/index, metrics, gates, champions; each in en, zh-CN
(root) and zh-TW. No heading renumbered or deleted; retitled only where the plan says (reference §1 "Processes and
outputs", §5 "Seeds"; champions §2 "Reports"). Headings, links and code spans have the same counts in all three locales
(one pre-existing extra code span in zh-TW gates §10, `gate.py`, left). Nothing built, nothing committed.

## Per page

**learning/index.** §1 "on other machines" dropped, LLM "would take part ... planned"; §4 reference row "processes and
outputs, ..., seeds, ..." (no ledger); last paragraph: "Both play the same two nets, the champion's V and the champion's
combat net, ..." (no names, no ledger).

**learning/combat-net.** §3 data paragraph is the champion's file's fact (`weights/all/c9-2.json`: all five characters,
A0, medium, 21,272 runs, 2,118,826 round starts, 430,142 held out from 4,310 runs); c8 lineage, `promotion-1-500` story
and the `c7` set statistics dropped; "6.2 % of all potions gone" dropped (no current measurement). §4 command as a
pattern (`--list data/CHARACTER/lists/NAME --env SL --name NAME [--test-list LIST]`); potion share recomputed from c9-2's
held-out fit (about 2 %: 0.084 rows per won sample, 0.29 nats, CE about 1.32); c8-3 `--init` story dropped. §5 every
number from c9-2's `fit` (held_out, by_room, by_hp, by_potion FAIRY_IN_A_BOTTLE, by_encounter); the hardest fight is now
Aeonglass (42.7 % deaths), not Knowledge Demon; "against the net it went on from" and the v9-0/v9-1 win comparison
dropped; last line links champions §2.

**learning/loop.** Intro "as designed"; §1 "in processes separate", "an LLM never takes part"; §2 v9g0 paragraph -> the
champion plays all five characters at A0-A10 (link selfplay §4); time-box sentence dropped, "`--init` from the champion's
file" added; Budget -> "the champion's own medium budget, as the gates do" (link sl-planner/budget §1). §3 "(win 1,
loss 0)"; r33hab rule kept as prior art + rationale, citation `learn/v/train.py:44`, `:144`; proxy/GuanDan paragraph
dropped. §4 only "an earlier V" dropped ("won on floor 48", "The figure's V values a run in expected progress"). §7 "an
LLM API". §8 wins gate: one sentence + link to gates §10 (no 300/low budget); the SPRT line fixed (wins only, δ = 0.1);
promotion link -> champions §2. §9 tiers 0.3/0.1/0.02 (link selfplay §3), old check numbers dropped. §12 `tmp/notes`
pointer dropped ("fair-play rules").

**learning/reference.** Description and intro ("where the page and the code disagree, the code is right"). §1 REWRITE
"Processes and outputs": local chain mermaid (English labels in all locales), Simulation (`run` processes, `--workers`,
1,860 vs 275 combats/s, 5-17 min a run at medium from the champion's reports), Outputs, Training; no hosts, hub,
deadlines, box.py, `.claude`, `git archive`. §3 formats only: rows `selfplay/vNgM/aAA-eXXX/`, `eval/DATE-SUBJECT/`
(`DATE-a0-V-C`), `forks/` (trial runs, kept: `common.train_lists` and test_common.py still take forks), `assist/`,
`void/`, `lists/`; chunk files as `pool/worker.py` writes them (rec/log/out/tally/counts/done per SHARD-INDEX); manifest
core fields only. §4 list table dropped. §5 "Seeds": train seeds, a block of 100,000 per dataset with a range per job
(matches group C's selfplay §4), the 40,134,134 bound, `common.train_lists`; no ledger, no box.py, gauntlet board
dropped. §6 holdings v9-0..2 / c9-0..2, both files name the champion's two; `(vN-Ma)`; sizes 6.0-9.6 MB; hash and
ledger sentence dropped; "The champion's combat net learned from SL's games". §7 "two net files, a V's and a combat
net's" (figure step). §8 Args.cs citations :235/:234; the Cost bullet (older V, hash) dropped. §9 v.train citations
fixed; `init_held_out_loss`, `init_brier_held_out` row added; combat.train command as pattern. §12 Q1 -> NoSL worth
(not trained, not gated, no report); Q2 -> whether the shared nets need to be wider.

**evaluation/index.** §1 "pending" line -> link to champions §2; "or a recorded decision" dropped. §2 table header
"today" -> "state". §3 Now: no net names, "Both nets learned from SL's games only: NoSL is not trained for now";
spire-mentor "by its own published results" (tmp pointer gone); the earlier-V pair sentence dropped. §4 gate paragraph
-> wins gate in one sentence + link gates §10; "C# SPRT `gate`, sequential, on wins". §5 champions row.

**evaluation/metrics.** Example report = the champion's Necrobinder report (`eval/necrobinder/reports/...-clone-p0.json`,
47 of 100): §1 (74 reached the act 3 boss, 47 won, 27 died there; 95 of 100 entered the act 1 boss, 48.7 HP, 69 %),
§3 table (3.36; 35.4 % 701 of 1,983; 182; 1,045.7 s; 1,358.4 ms; combat 59,579, rewards 5,668, map 4,237), §4 format
example `47.0 % (47/100, 37.5 %–56.7 %)`, §8 (53 losses: 43 boss, 8 elite, 2 monster, 0 outside; gate.py gives the same;
bosses beaten 87/95, 75/83, 47/74; no missing ends). §1 "the C# gate compares floors by default" fixed (both gates list
floors beside a verdict on wins). §2 "crashed process or machine fault"; RunCommand.cs:233. §3 "machine and its load";
train.py:167-182. §5 used-for column current, box.py, c8 line, ledger, promotion-2 note dropped; retired set: "in data
the champion's combat net has learned from". §6 "any train index below 40,134,134". §7 `r1` dropped, example name as a
pattern `...-plan-C-l0-V-clone-first`. §8 RunCommand.cs:270-275; oldest-reports and missing-end history dropped. §9 NoSL
board reason (the NoSL file names no report), "each character's board". §10 "a placeholder commit `abc1234`".

**evaluation/gates.** §1 run time "5 to 17 minutes ... by character". §2 A-E anonymous ("three nets", "two NoSL
brains", "the retired set"); added: the SPRT gate plays only the challenger, the wins gate plays both sides at one commit.
§3 "net C or net B as the challenger". §4 "on promotion-1-300", re-baseline parenthetical dropped. §5 panel row "the
champion's combat brains are in" sl-planner/budget §5 and pimc/budget §6 (matches group B's "The champion"); wins row
"both pairs play promotion-1-100 at medium with every character". §6 "a challenger that played only the head",
"today's" dropped, Time boxes paragraph deleted. §7 the 33-pairs result dropped. §9 NoSL re-baseline story deleted. §10
intro "a new pair of nets"; "What it does not check" without the skill pointer; new **The procedure** with the commands
(two `run --champion eval/champions/sl.json ...` jobs per character, then `uv run python gate.py --records ... --logs ...
--against ...`), **The verdict**, **Promotion** by hand (both files, the SL file's five reports via `report`), no
ledger.

**evaluation/champions.** Description and intro; §1 `metric` dropped from the `gate` fields (Gate.Result has none); new
bullet: `brain` serves both host kinds from one file (`BrainCommand.Champions`, `BrainCommand.cs:72-79`, after
70e9a25), the mod's brain plays the NoSL file (link mod §4); "NoSL is not trained for now"; ledger dropped. §2 REWRITE
"Reports": reports folder and boards; the five reports table (wins, Wilson, mean floor, ms/decision, s/run; none void);
how a promotion rewrites the files (`gate --promote` one file; the wins gate by hand into both, SL `reports`, NoSL none,
old files stay). §3 "From their published results", both tmp pointers dropped. §4 "has a champion".

## Left on purpose

- gates §1 "At a win rate of 7.7 %, +3 pp takes about 1,240 runs per side": an anonymous power illustration.
- gates §2/§3/§4/§10 illustrations on committed reports (nets A-E, promotion-1-300 / promotion-1 / promotion-1-500):
  kept anonymous, as the plan says. The §10 "What it takes to pass" table is still computed at 300 pairs; the gate now
  plays 100 per character, so a 100-pair table would fit better (not recomputed).
- metrics §8 "(48 for every won run so far)": a fact of the data.
- combat-net §3 "the mask never closes the true bucket" (c7 set) dropped rather than recomputed: the champion's lists
  are not on this machine.
- reference §3 keeps the `forks/` row (current folder for smoke runs; the trainers take it).

## For the figures step

- learning/loop §4: the TdTargets prose still describes the old figure (expected progress, 48 + 1 = 49, "V started at
  33", 12.1 / 1.2 above V, floors 16-17 and 32-33); only "an earlier V" was removed ("The figure's V values a run in
  expected progress"). Rewrite with the regenerated `learning-td.json` (chance to win, G = 1). §3's "181 in the book's
  example run" and §4's "181 macro decisions" come from the example run.
- learning/reference §7: "The figure opens two net files, a V's and a combat net's" (names dropped); NetFileAnatomy and
  `learning-netfiles.json` still show v9-1 / c9-1.
- evaluation/metrics: DeathsByFloor (`eval-reports.json`) must be regenerated from the Necrobinder report the text now
  uses (noEnd 0; caption "the champion's report, Necrobinder, SL, promotion-1-100", no missing-end clause). The
  WilsonInterval caption ("three earlier nets' whole-run reports ...") is in the component.
- evaluation/gates: the SprtReplay caption still says "the later net as the challenger" (component); the page text says
  "net C or net B". PairedScatter / SprtReplay `source` fields hold a rev and dated report names.

## Cross-page links

Changed here: loop §2 -> selfplay §4 (was reference §5 and reference §1), budget -> sl-planner/budget §1 (was §2); loop
§8 -> champions §2 (was §1); loop §9 -> selfplay §3; combat-net §5 -> champions §2 only; evaluation/index §1 -> champions
§2, §4 -> gates §10 (no restated procedure); gates §2 -> §10; gates §5 rows (text "the champion's combat brains");
gates §10 -> metrics §7, champions §2; champions §1 -> mod §4 (new), §2 -> gates §8, §10, reference §6, metrics §2;
reference §1 -> §10, simulator/combat §7, champions §2; reference §4 -> loop §2; reference §5 -> metrics §6 twice;
metrics §1 -> gates §10, §5 -> gates §10.

For others: reference §1 no longer covers hosts or time boxes, so nothing should link it for those (status.mdx's link
goes with the page). macro-brain/selfplay §4 step 6 links champions §1 for "replaces the V and the combat net of both
champion files": champions §2 now says how. `.gitignore:10` cites "docs: learning §6" (means learning/reference §3).

## Where the code disagreed with FACTS.md or the audit

1. `learn/gate.py` has no `--promote` (brief: "read `learn/gate.py --promote`"). Promotion by command is the simulator's
   `gate --promote PATH` (`EvalCommands.cs:111-126`), which rewrites only the one champion file given, with one
   character's report as its only one. A wins-gate promotion is by hand in both files.
2. `learn/gate.py --against` pairs by seed alone (`paired`, `theirs = {r["seed"]: r ...}`), so it must run once per
   character; "the five characters together say so" is a reading of five outputs, not something the script computes.
   gates §10 says so.
3. The C# `gate` tests wins only (`Gate.cs` header; `Gate.Result` has no `metric`). Pages said floors: loop §8 ("by
   floor, δ = 1.5"), metrics §1 ("compares floors by default"), evaluation/index §4 ("on floors or on wins"), champions
   §1 (`metric` field). All fixed.
4. The gate's ascension is not in FACTS; the champion's reports are A0 and the gate's runs were `--asc 0`, so the
   procedure says ascension 0, "the ascension of the champion's reports". Self-play now covers A0-A10: if the next gate
   should play other ascensions, gates §10 changes.
5. Audit: `abc1234` is at `Fixtures.cs:32`, not :31 (kept :32). Args.cs: `--value` check :235, `--combat-net`/policy
   :234 (page had 234/233). `.gitignore` data lines are 10-11 (page 8-9). v.train: main :48-59, refusals :70-71 /
   :81-82, constants :40-44, logged-not-added :150-153, report :170-185, Brier :167-182 (audit's "about :167-185").
   RunCommand.cs: NoSL export :233 (page :239), record :270-275 (page :272-284). Weights files are 6.0-9.6 MB (page
   4.9-8.8).
6. Data layout: the current generation's folders are `aAA-eXXX` (ascension and tier), eval folders `DATE-aASC-V-C`; each
   chunk has its own `out-` and a `tally-` file (`pool/worker.py:261-292`), not one `out-SHARD.txt`. The manifest is
   written by `pool/box.py` (in git); the page describes the format without naming the tool.
7. The champion's reports' `config.champion` say "combat prompts p0" and their files end `-clone-p0.json`; `sl.json`
   says "first". metrics §7's name example follows the current convention (`...-clone-first`).
8. Outside docs: `Harness/RunRecords.cs:31` comments "fleet references/maintenance.md".
