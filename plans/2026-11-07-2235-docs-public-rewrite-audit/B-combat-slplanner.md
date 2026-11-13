# Audit B: combat-brain/* and sl-planner/* (English reference)

Scope: en/arch/combat-brain/{index,leaf,network,walkthrough,shared}.mdx, en/arch/sl-planner/{index,turn-search,beam,budget}.mdx,
their figures and data, the zh-CN / zh-tw twins. Read-only; nothing in the repo was edited.

Current champion facts used below (from `eval/champions/sl.json`, `nosl.json`, the 2026-11-06 reports):
SL = plan c9-2 beam 8x300 easy 4 loss plans 3, combat prompts `first`, L0 v9-2 clone; NoSL = pimc c9-2 8x300, L0 v9-2.
Reports (A0, promotion-1-100): Silent 92 won / floor 46.45, Ironclad 82 / 46.00, Defect 68 / 46.44, Regent 71 / 45.57,
Necrobinder 47 / 41.55; 286-1,046 s per run.

---

### en/arch/combat-brain/index.mdx  (lines 189)  -- FATE: EDIT
Reason: describes the current design well; one experiment-backed paragraph, one pointer to old panel results, and a lot of planned content to decide on.
Sections:
  - intro + Aside "Status" -- edit (only future content: the "Planned, not implemented" paragraph and table L28-35)
  - §1 `#sec-1` What it decides -- keep
  - §2 `#sec-2` A fight as a search problem -- keep (L61 cost numbers are current measurements)
  - §3 `#sec-3` Two environments, one shape -- edit (L79 experiment; L74 planned row)
  - §3.1 `#sec-3.1` TurnEnds (planned) -- future, decide later
  - §3.2 `#sec-3.2` Leaf -- keep (L107 says the figure is the planned shape: future)
  - §4 `#sec-4` The interface -- keep (L152, L155 planned bullets: future; L153 minor)
  - §5 `#sec-5` How it is checked -- edit (L160 pointer to panel results; items 1 and 3 planned)
  - §6 `#sec-6` The order of validation (planned) -- future, decide later (whole section is planned)
  - §7 `#sec-7` The three chapters -- keep (table mentions planned parts)
Findings:
  - L79 experiment "On the panel, PIMC run on SL's one exact world played almost exactly like" -> keep the argument (PIMC's search ends with the turn, little is revealed inside a turn, so the true world tells it little; only a search past the turn profits), drop the 112/120 and "9 more of the same 120 fights" panel numbers (old panel with an earlier net, 2026-10-28) and the link to budget §5
  - L160 experiment "Fights measured on a fixed panel ... are reported in" budget §5 / pimc budget §6 -> drop the sentence if the panel results go (see budget §5)
  - L153 future-ish "stay, for the placeholders and for fast policies that could play" -> "serve the placeholders" (drop the "could")
  - L85 future "Today two searches do this job ... must pass a NoSL gate" -> list under future
Inbound links to deleted/renumbered sections: §6 is linked from status.mdx:26 (`/en/arch/combat-brain/#sec-6`); §7 has no inbound links. §3.1 is linked from pimc/search.mdx:107, sl-planner/turn-search.mdx:96, glossary.mdx:81 (all planned-content pointers). Nothing is deleted unless future content goes.
Figures/data: none imported (mermaid only).
Twins: match (same 189 lines, 9 headings; no extra hashes, dates, versions, hosts, `.claude`, `tmp/`).
Future/next content: frontmatter badge "partly built" and description "what is only planned"; Aside L28-35 (planned table); §3 table row L74 "search planned"; §3.1 whole (L81-85); L107; L152 `Facts` on the result; L155 planned budget; §5 item 1 Tactics (planned), item 3 NoSL counterfactual (planned); §6 whole (L167-173); §7 table "planned shape", "planned iterative widening", "the planned fight budget", "the planned budget" (L183-189).
Rough size of the rewrite: ~4 lines rewritten now; ~30 lines of planned content pending the future decision.

### en/arch/combat-brain/leaf.mdx  (lines 86)  -- FATE: EDIT
Reason: current design; only the net file named is the old one.
Sections:
  - §1 `#sec-1` What a leaf is -- keep
  - §2 `#sec-2` The price of an ending: V -- keep
  - §3 `#sec-3` The combat net -- keep
  - §4 `#sec-4` The two outputs -- edit (L44 file name; figure data)
  - §5 `#sec-5` Pricing the endings -- keep
  - §6 `#sec-6` One net, two environments -- keep
  - §7 `#sec-7` What this value cannot see -- keep (L86 minor)
Findings:
  - L44 version "Weights: `weights/all/c9-1.json`." -> `weights/all/c9-2.json`, which needs combat-walk.json regenerated from c9-2 (OutcomeHead draws `walk.tests`); c9-2's 16 test states are different states from c9-1's (max outcome difference 0.96), so the figure changes
  - L86 version-ish "earlier nets, exploring, at the medium budget" -> "the nets that played its training games, exploring, at the medium budget" (fact still true for c9-2: v9g1 was played at medium)
Inbound links to deleted/renumbered sections: none.
Figures/data: OutcomeHead.svelte -> comment L1 names `weights/all/c9-1.json` (code comment, in git); draws combat-walk.json `tests`, i.e. c9-1's states and outputs. No visible text with hashes or names; visible text is clean.
Twins: match.
Future/next content: none.
Rough size of the rewrite: 2 lines + figure data regeneration.

### en/arch/combat-brain/network.mdx  (lines 264)  -- FATE: EDIT
Reason: the structure description is current; §8 names c9-1 and its last paragraph is a lineage story with numbers that no longer hold for c9-2.
Sections:
  - §1 `#sec-1` The whole net -- keep
  - §2 `#sec-2` What it reads -- keep
  - §3-§7 `#sec-3`..`#sec-7` -- keep
  - §8 `#sec-8` Sizes -- edit (L220 file name; L244 rewrite)
  - §9 `#sec-9` One forward pass, two implementations -- keep (line cites checked: encode.py:150, :24, :71; net.py:223, :231-310; data.py:25-30; export.py:72; CombatNet.cs:69, :144 all land)
  - §10 `#sec-10` What the structure cannot express -- keep
Findings:
  - L220 version "The file is `weights/all/c9-1.json`." -> `weights/all/c9-2.json` (sizes table holds for c9-2: same vocab, same 1,738,813 weights)
  - L244 history/version "going on from weights that had learned from the Silent's fights at ascension 10" -> rewrite to what c9-2 holds now, without lineage. Measured on the files: c9-2 differs from the paragraph's c9-1 numbers: $R$ rows off zero 155 -> 261, $Z$ 38 -> 39, $X$ 8 -> 9, $O_j$ 35 -> 44 rows; zero columns of the allies' part at the body 4 -> 2 (the ally layer's 22 zero inputs, $D_c$ 180, $B$ 69, the potion-slot column at zero, and the five identical potion-head groups all still hold for c9-2). Suggested facts-only form: "The parts that no training data fed are still at zero: ..." with c9-2's counts; drop "first half / second half" widening history and "the Silent's fights at ascension 10".
Inbound links to deleted/renumbered sections: none (walkthrough.mdx:200 links §8 for "the five groups hold the same weights", which stays true for c9-2).
Figures/data: ArchDiagram.svelte + v/arch.ts (combat net part) -> clean.
Twins: match.
Future/next content: none.
Rough size of the rewrite: 1 line + 1 paragraph (~10 sentences) rewritten.

### en/arch/combat-brain/walkthrough.mdx  (lines 265)  -- FATE: EDIT (data regeneration + prose)
Reason: names c9-1 as its file; switching to the champion c9-2 changes the state walked through, and much of the prose is hand-written for that one state.
Net named: `weights/all/c9-1.json`, `tests[9]` (L14). The text does not speak of older nets anywhere else (no "earlier", no lineage); L200 "In this file the five groups hold the same weights" is still true for c9-2.
Checked by: `docs/scripts/check.mjs:68` reads `weights/all/c9-1.json`; `docs/scripts/combat-walk.py:21` `FILE = ROOT / "weights/all/c9-1.json"`. Both need c9-2 (better: take the combat net from `eval/champions/sl.json` so a promotion does not leave them behind).
Sections: §1-§16 `#sec-1`..`#sec-16` -- keep all; rewrite the hand-written prose for the new state.
Findings:
  - L14 version "`weights/all/c9-1.json`, `tests[9]`" -> `weights/all/c9-2.json` and the chosen test index
  - c9-2's `tests[9]` is a Defect in LOUSE_PROGENITOR_NORMAL, round 3, not the Ironclad in EXOSKELETONS_WEAK; whichever c9-2 state is chosen, these hard-coded passages must be rewritten: L18 (character, encounter), L23 (Juggling), L29-32 (potion, orbs, Hard to Kill), L36-38 (per-enemy powers by index), L54 ("here 1 round", slot counts), L62, L64 ("six relics"), L76-85 ("why" column: 20 cards, one power, three enemies...), L89 and L101 (Strike numbers, the arithmetic 0.228 + ... = 0.216), L118 (Juggling), L132, L138-142 (bag counts), L180-183 (0.839, 0.161, "3 buckets"), L191-196 (HP conversions, Burning Blood 6 HP), L200-202 (Fairy in a Bottle), L214, L224 ("83 HP of 93"), L236-238, L242-253 (The Kin, Phrog Parasite: `others.danger` / `others.mask` are picked by the script and may change), L259-261 ("78/93").
Inbound links to deleted/renumbered sections: none (leaf.mdx:57 -> §13, leaf.mdx:34 page link).
Figures/data: combat-walk.json -> `source: "c9-1.json, tests[9]"` (not displayed, version name in git); `fit` holds c9-1's 29,987 runs / 2,005,554 samples (not displayed); no hashes (the two 7-digit hex-looking matches are the numbers 1738813 and 2005554). Visible values all come from c9-1.
Twins: match (both name c9-1 at L14 too).
Future/next content: none.
Rough size of the rewrite: data regenerated; ~40 lines of prose rewritten; 2 script constants.

### en/arch/combat-brain/shared.mdx  (lines 89)  -- FATE: EDIT
Reason: current design; two history/experiment phrases and a planned section in the middle.
Sections:
  - §1 `#sec-1` Card prompts: answers as branches -- keep
  - §2 `#sec-2` The record of enemy moves -- keep (L30 planned bullet: future)
  - §3 `#sec-3` The worker pool -- edit (L61)
  - §4 `#sec-4` Planned -- future, decide later
  - §5 `#sec-5` What search counts as the same -- edit (L89)
Findings:
  - L89 history "Search no longer tells those lines apart" -> "Search does not tell those lines apart"
  - L61 experiment "a 6-combat panel trace with 8 workers diffs empty against the in-process one" -> drop; keep "`selftest --workers N` checks it"
  - L65, L67 measurements of unknown build ("A NoSL worker sampled for 40 s during a whole-run gate", "8 workers take 68.7 ms per action against 307.5 ms") -> keep only if they hold for the current build, else drop the numbers
  - L24 future-ish "It is the part of the public record that is built today" -> "It is the public record the brain keeps" (the "today" implies more planned)
Inbound links to deleted/renumbered sections: if §4 (planned) goes, §5 becomes §4: 7 inbound links to `combat-brain/shared/#sec-5`: combat-brain/index.mdx:76, environments/worlds.mdx:70, pimc/search.mdx:16, pimc/search.mdx:18, simulator/combat.mdx:66, sl-planner/turn-search.mdx:14, sl-planner/turn-search.mdx:31. No inbound links to `#sec-4`.
Figures/data: none imported (mermaid only).
Twins: match.
Future/next content: L30 (record stays for NoSL world building, fidelity 82-89% -> 96-98%); §4 whole (L69-71, worlds that live in a worker).
Rough size of the rewrite: ~3 lines.

### en/arch/sl-planner/index.mdx  (lines 104)  -- FATE: EDIT
Reason: design text is current; the worked example comes from an old champion's run and §4 tells a fixed-bug story with an old evaluation's numbers.
Sections:
  - intro + Aside "Built and planned" -- edit (L21, L25 example provenance; Aside is future)
  - §1 `#sec-1` What SL may see -- keep
  - §2 `#sec-2` Why the best line is the right answer -- keep
  - §3 `#sec-3` The planner in one picture -- keep
  - §4 `#sec-4` Plan once, follow the plan -- edit (L73)
  - §5 `#sec-5` The planned search -- future, decide later
  - §6 `#sec-6` This chapter -- keep (table mentions planned parts)
Findings:
  - L21 version/story "a run that an earlier V went on to win (seed `E91KLB4BRWB4`, from a 300-run evaluation)" -> regenerate the example with the current champion (the book's example seed `0SSL7NS01MU6`, which panel-examples.json already uses, or any current-champion fight) and drop "an earlier V" / "from a 300-run evaluation"
  - L25 version "what the leaf of that run's champion gave a win at 34 of 70 HP" -> the plan's value 0.4914 is the old leaf's (c2, V v6-3-2 at 1fc5f50); after regenerating, "the plan's value, X, is the leaf's price of a win at N HP"
  - L73 history "In the first 40 SL runs with the planner, 38 had none ... Lava Rock ... The checkpoint now carries" -> delete the bug story (the fact that the checkpoint holds relics' plain fields is already in §1 L31)
  - L73 version/story "In the 300 runs of an earlier V's evaluation (5,375 fights) no step failed: 0 replans, and 95.6%" -> state from the current champion's reports: 0 replans (the 2026-11-06 reports and gate show 0); drop 95.6% or re-measure
Inbound links to deleted/renumbered sections: none deleted. §5 (planned) has no inbound links from other pages; if it goes, §6 -> §5 (no inbound links to `sl-planner/#sec-6`).
Figures/data: PlanTimeline.svelte -> visible text clean; displays combat-examples.json `sl.fight` (old champion: `sl.source.rev` "1fc5f50", `champion` "combat plan c2 ... macro l0 v6-3-2 clone", `job` "sl/eval/2026-10-30-eval-v6-3-2-clone", run id `a69ff865e57e1f24`; none of `source` is displayed, but the figure shows "the plan's value 0.4914" from the old leaf). Regenerate `sl` from a current-champion run and drop `rev` / `job` (or set `champion` to the current champion string).
Twins: match.
Future/next content: frontmatter badge "built, redesign planned", description "the planned simpler search"; Aside L15-19; §5 whole (L77-96); §6 table "the planned TurnEnds", "the planned iterative widening", "the planned fight budget".
Rough size of the rewrite: 3 lines rewritten (L21, L25, L73 -> 2 sentences) + figure data regenerated.

### en/arch/sl-planner/turn-search.mdx  (lines 102)  -- FATE: KEEP (one wording fix)
Reason: describes the current search; one "as before".
Sections:
  - §1 `#sec-1` Depth first over the turn -- keep (bottled_ai credit is prior art, not our history)
  - §2 `#sec-2` Transpositions and the full hidden state -- keep
  - §3 `#sec-3` Sharing the nodes over the first actions -- keep
  - §4 `#sec-4` What a turn's search returns -- keep
  - §5 `#sec-5` Cycles inside a turn -- edit (L79)
  - §6 `#sec-6` TurnEnds (planned) -- future, decide later
Findings:
  - L79 history "So a deck with no cycle is played move for move as before." -> "A deck with no cycle is searched as if there were no LoopSearch." or drop
  - constants checked against code: LoopSearch Moves 4000, CashNodes 60, MaxRepeats 4096, MaxBreaks 8; SlPlanner MaxTurns 40 -- hold
Inbound links to deleted/renumbered sections: §6 is linked from sl-planner/index.mdx:94 only.
Figures/data: TurnTree.svelte -> clean (made-up turn).
Twins: match.
Future/next content: §6 whole (L94-102), incl. L100 "left to the implementation and its gate".
Rough size of the rewrite: 1 line.

### en/arch/sl-planner/beam.mdx  (lines 125)  -- FATE: EDIT
Reason: rules are current; several experiment results, an old-net panel table with a date, and a commit hash.
Sections:
  - §1 `#sec-1` A beam over turns -- keep
  - §2 `#sec-2` No bound -- keep
  - §3 `#sec-3` The greedy dive -- edit (L35, L38)
  - §4 `#sec-4` The easy beam -- edit (L44)
  - §5 `#sec-5` Picking the line, and proving it -- edit (L56, L58)
  - §6 `#sec-6` Losing plans -- edit (L65; table L70-84 delete or regenerate)
  - §7 `#sec-7` Iterative widening (planned), §7.1 `#sec-7.1`, §7.2 `#sec-7.2`, §7.3 `#sec-7.3` -- future, decide later
Findings:
  - L35 story "(in a 300-run evaluation, the first plan won 99.8% of the hallway fights that were won" -> drop the parenthesis (old V's evaluation) or restate from a current-champion measurement
  - L38 experiment "On a fixed set of fights it saved 14-16% of the nodes of the unproved plans" -> drop; the design fact is in turn-search §4 L71 ("the result is the one a fresh search would give")
  - L44 experiment "medium spends 9.5 s on a won hallway fight against high's 13.8 s" -> drop the numbers; keep "the easy beam is most of medium's saving over high"
  - L44 experiment "A narrower beam in every hallway fight saved as much time but missed" -> drop (tried and dropped); keep "the easy beam keeps the full width whenever the dive loses"
  - L56 hash "played by the SL champion at 81adf16, 23 of the 24 fights' plans" -> without a hash: "in the book's example seed, played by the current SL champion, most fights' plans had a single candidate" or drop the example sentence; note the number was measured with prompts p0, before every prompt answer became a search branch, so it may no longer hold (re-measure before keeping a number)
  - L58 experiment "In the fights below, none of 21 plans was proved." -> drop with the table, keep "Proofs are rare: a turn yields many more states than the beam keeps."
  - L65 experiment "at high effort, which has no limit, a lost boss fight was planned 6.2 times on average and took 241 s" -> keep the rule's reason in one sentence ("planning a lost fight again every turn would take most of a run's time, and few lost fights turn after the third plan"), drop the numbers
  - L70 date/version "(an earlier combat net, medium effort, 2026-10-30)" + table L72-82 -> delete, or regenerate with c9-2 at the current build (data source combat-examples.json `panel`, command `--combat-net c2`, rev 1fc5f50)
  - L84 story "in whole runs, the same rules plan a lost boss fight 2.95 times on average" -> drop (old evaluation; see budget §3)
  - L115 experiment "about 460 a second (11,345 nodes in 24.7 s on the Byrdonis fight of §6)" -> planned section; if the §6 table goes, this pointer breaks: drop the numbers or re-source
  - L115 future "Depth-first search may be compared later." -> future list
Inbound links to deleted/renumbered sections: no section deleted (§6 keeps its rules). Links that cite the table's numbers: budget.mdx:93 (`#sec-6`, "5,653-21,592 nodes ... of the panel's elites and bosses") and budget.mdx:93 (`#sec-7.2`, "about 460 nodes a second on the panel"). Other links to §6 (rules only, unaffected): combat-brain/index.mdx:75, :151; harness/decision-log.mdx:70; harness/routing.mdx:48; budget.mdx:55; sl-planner/index.mdx:66, :73; status.mdx:52. Links to §7: combat-brain/index.mdx:33, :74, :171; budget.mdx:81, :90; sl-planner/index.mdx:95; glossary.mdx:82.
Figures/data:
  - BeamStepper.svelte -> clean (made-up fight, madeUpFight.ts)
  - Widening.svelte -> clean; it is a planned-design figure (caption L108 "Planned design, not running code"): future
  - 81adf16 shown without a hash: L56 "played by the current SL champion" (or drop the sentence)
Twins: match (both carry 81adf16 and 2026-10-30 in the same places).
Future/next content: description "the planned iterative widening"; L11 "[§7] is the planned search"; §7 whole (L86-125), incl. L115 "may be compared later".
Rough size of the rewrite: ~20 lines removed (table + numbers), ~6 sentences rewritten.

### en/arch/sl-planner/budget.mdx  (lines 95)  -- FATE: REWRITE
Reason: the budget options are current, but most of the page is old experiments (paired effort runs with an earlier V, a 2026-10-28 panel, an old-V evaluation table, planner vs an old NoSL champion) and its "Now" names v9-1 / c9-1.
Sections:
  - §1 `#sec-1` Effort levels -- rewrite (keep table columns effort / B / E / L; drop the results column and L23, L25)
  - §2 `#sec-2` The low budget -- rewrite (the option, without "the data was played at" and without the choosing experiment)
  - §3 `#sec-3` Where the time goes -- rewrite (keep the restore share as a fact if current; replace or drop the old-V table and its bullets)
  - §4 `#sec-4` Workers -- keep
  - §5 `#sec-5` Results -- rewrite to the current champion's reports only (delete "The panel" and "Whole runs")
  - §6 `#sec-6` Weaknesses and open questions -- keep (future items listed below)
  - §7 `#sec-7` One budget per fight (planned) -- future, decide later
Findings:
  - L18-21 experiment results column "200 seeds: -0.76 ± 0.89 floors (95%) ... passed the not-worse gate on floors after 33 pairs" -> drop the column (or one sentence: "medium plays as far as high in about 0.6 of its time" if a current measurement exists)
  - L23 history/version "with the macro brain of the time (L0 with an earlier V) ... Two later changes cut the cost" -> delete
  - L25 experiment "A beam of 4, twice as wide in elite and boss fights ... failed the not-worse gate" -> delete; keep "every room gets the same beam" (already beam §1 L23)
  - L29 history "V's and the combat net's data was played at a cheaper budget" -> stale: c9-2 / v9-2 learned from v9g1, played at medium (ledger row v9 g1: "medium budget (beam 8x300, easy 4, loss plans 3)"); rewrite §2 as "a cheaper budget for self-play when wanted: ..." with the command
  - L35 experiment "It was chosen on boss fights: on 515 of them ... correlate 0.83 ... 0.94" -> drop, or keep as one-line rationale without "was chosen" (owner's call)
  - L39 experiment "on the panel, 150 nodes per state and turn searched about as many nodes as 300" -> drop; keep "most turns end their search before the limit" only if true now
  - L41 version "an evaluation of an earlier V and an earlier combat net (300 SL runs on `promotion-1-300`" + table L43-51 + bullets L53-57 -> re-measure from the current champion's decision logs (2026-11-06 reports, 5 characters) or delete; the table's data is combat-examples.json `sl.stats` (old V v6-3-2, c2)
  - L65 date/experiment "**The panel (2026-10-28).** ... prompts by the fixed rule of the time, an earlier combat net" + table L67-71 + L73 -> delete
  - L75 history/story "With the macro brain the same as the NoSL champion's of the time ... +4.35 ± 2.56 floors" -> delete
  - L77 version "with c9-1 in its leaves and L0 with V v9-1 ... won 74 runs with the Silent ..." -> current: c9-2 / v9-2; Silent 92 (floor 46.45), Ironclad 82 (46.00), Defect 68 (46.44), Regent 71 (45.57), Necrobinder 47 (41.55), 286-1,046 s per run
  - L93 experiment "about 460 nodes a second on the panel ... 5,653-21,592 nodes for one plan of the panel's elites and bosses today" -> breaks if the beam §6 table goes; planned section anyway
  - L94 "as they do now ([§2])" -> after §2's rewrite self-play and gates both play medium; the clause needs re-wording (future section)
Inbound links to deleted/renumbered sections (no section number changes; content of §1, §2, §3, §5 changes):
  - `#sec-5` (panel results the linkers rely on): combat-brain/index.mdx:79, combat-brain/index.mdx:160, evaluation/gates.mdx:100, pimc/budget.mdx:50 -> all four cite the panel and must change with it
  - `#sec-3` (old-V table, restores): combat-brain/shared.mdx:65 (restores: fine), beam.mdx:35 (99.8%), beam.mdx:84 (2.95), beam.mdx:115 (88% restores: fine), sl-planner/index.mdx:73 (95.6%)
  - `#sec-2` (low budget as the data's budget): learning/combat-net.mdx:35, learning/loop.mdx:62, macro-brain/selfplay.mdx:33, sl-planner/turn-search.mdx:39 -> the first three describe old datasets (see cross-page)
  - `#sec-1` (effort levels, unaffected): evaluation/champions.mdx:48, evaluation/gates.mdx:159, harness/index.mdx:144, harness/routing.mdx:79, mod/index.mdx:111, beam.mdx:23, beam.mdx:44
  - `#sec-7`: combat-brain/index.mdx:155, :171; sl-planner/index.mdx:96; turn-search.mdx:100
Figures/data: none imported. Its tables are hand-copied from combat-examples.json `sl.stats` (§3) and from runs not in the data files (§1, §5).
Twins: match (same dates 2026-10-28, same c9-1 / v9-1, same seed sets).
Future/next content: description "the planned single budget per fight"; L10 "[§7] is the planned budget"; §6 L81 "Iterative widening is the planned answer", L84 "Searching on to the node's exit is open", L86 equal wall-clock budget; §7 whole (L88-95).
Rough size of the rewrite: ~35 of 95 lines removed or rewritten (§1 column + 2 paragraphs, §2 2 sentences, §3 table + 5 bullets, §5 two paragraphs + table, "Now" updated).

---

## Extra checks asked for

**81adf16 / 1fc5f50 and how to show them without a hash**
- sl-planner/beam.mdx:56 "played by the SL champion at 81adf16" -> "played by the current SL champion" (or drop the sentence; its 23-of-24 count was measured with prompts p0 and may not hold for the current search).
- docs/src/components/book/PanelMock.svelte (figure on output.mdx, not this group's page):
  - L4 code comment "played by the champions at 81adf16" -> "played by the champions" (drop "at 81adf16").
  - L341-343 visible caption, all three languages, "... (the SL planner with c9-2, L0 with v9-2) at 81adf16, decision ..." -> drop " at 81adf16" (zh: drop "在 81adf16 上"); the champion is already named.
  - L197 displays `source.champion`, "combat plan c9-2 ... combat prompts p0 ..."; the current champion file says "combat prompts first" -> regenerate or edit the string to the current champion's name.
- docs/src/data/book/panel-examples.json:
  - L2 `source` note "each run played locally on 2026-11-07 ... at 81adf16 through a logging proxy" -> "each run played by `run --brain` against `brain --champion eval/champions/sl.json` or `nosl.json`" (drop the date and the hash).
  - `sl_combat.source.rev`, `nosl_combat.source.rev`, `map_options.source.rev` = "81adf16" -> drop the field (no component reads `rev`; PanelMock's type has only champion / decision / env).
  - `map.source.rev` = "1fc5f50" (MapRoute's data) -> drop; same subtree also holds old names: `job` "data/silent/sl/eval/2026-10-30-eval-v6-3-2-clone" (date + v6-3-2), `champion` "combat plan c2 ... v6-3-2", `values` "--value weights/silent/v6-3-2.json" (that file is not in git), `brain` "l0 v6-3-2 clone". MapRoute displays only seed / floor / HP / points; drop `rev`, `job`, `values`, `champion`, `brain`, or regenerate the map example from the 81adf16-era run the other three entries use (same seed, run id).
  - run ids `ce13ba5116b7f48a` are 16-hex and will trip a 7-40-hex hash scan; they are run ids, not commits: keep or drop `run`.
- docs/src/data/combat-examples.json: `sl.source.rev`, `panel.source.rev`, `nosl.source.rev` = "1fc5f50" -> drop the field. Not displayed by any figure.

**combat-examples.json / combat-walk.json fields the figures display**
- combat-examples.json:
  - `sl` (PlanTimeline, sl-planner/index): figures show only `sl.fight` (encounter, floor, HP, steps, plan length, elapsed, `first.candidates[0].value` 0.4914). No hash or name displayed, but the whole fight is the old champion's (`champion` "combat plan c2 ... v6-3-2", `job` with 2026-10-30, run `a69ff865e57e1f24`); the displayed plan value is the old leaf's.
  - `nosl` (WorldSampler, WorldsTable on pimc pages): displays `obs`, `draw`, `decision`; `source` holds rev 1fc5f50, "pimc c2 ... v6-3-2", "promotion-1-300 #0", run `034f8a6ab80c494f` (not displayed).
  - `panel` and `sl.stats`: read by no component; they are only the source of the hand-copied tables in beam §6 and budget §3 (old nets c2 / v6-3-2). Delete with the tables, or regenerate with the current champion.
- combat-walk.json: displayed fields (state, tokens, vectors, head, potion, ended, siblings, others, label, tests) carry no names or hashes, but all are c9-1's; `source` "c9-1.json, tests[9]" and `fit` are not displayed.

---

## Cross-page issues

1. **The combat net named in the book is c9-1, the champion is c9-2.** leaf.mdx:44, network.mdx:220, walkthrough.mdx:14, OutcomeHead.svelte:1, combat-walk.json `source`, `docs/scripts/combat-walk.py:21`, `docs/scripts/check.mjs:68`. c9-2's test states differ (tests[9] is a Defect / LOUSE_PROGENITOR_NORMAL), so this is a regeneration plus a walkthrough prose rewrite, not a rename. Reading the champion's `combat-net` from `eval/champions/sl.json` in both scripts would keep them current after promotions.
2. **budget §5 "Now" is v9-1 + c9-1's report** (Silent 74, ...). Current: Silent 92 / 46.45, Ironclad 82 / 46.00, Defect 68 / 46.44, Regent 71 / 45.57, Necrobinder 47 / 41.55.
3. **The old panel (2026-10-28, earlier combat net, "prompts by the fixed rule of the time") is cited from four places**: combat-brain/index.mdx:79 and :160, evaluation/gates.mdx:100, pimc/budget.mdx:50 (plus pimc/budget §6 itself). Removing it from budget §5 needs all of them changed in one pass.
4. **Self-play budget facts are stale across pages**: budget §2 says the nets' data was played at the low budget; c9-2 / v9-2 learned from v9g1 at medium. macro-brain/selfplay.mdx:33 ("The latest generation, `v9g0`"), learning/combat-net.mdx:35 (c8-3 / v8g2 lineage, "`promotion-1-500` no longer serves"), learning/loop.mdx:62 (v7g1 / v7g2 / v8g2 / v9g0) are history/version in other groups' pages and all link budget §2.
5. **Two different "low budgets"**: `--effort low` in code is beam 8, easy 2, loss plans 3, 300 nodes (`CombatBrain.cs:22`, budget §1), while budget §2's "low budget" is a separate command (beam 4, 150 nodes, easy 0, loss 0, replan 0); turn-search.mdx:39 "(150 at the low budget)" means the second. Name them apart or drop §2's.
6. **PanelMock's champion string says "combat prompts p0"**; the current champion files say `first` (shared.mdx:20 says `first` too). The SL fight in PanelMock / beam §5 was played before every prompt answer became a branch.
7. **Example runs from old champions remain in figures**: PlanTimeline (sl-planner/index), WorldSampler / WorldsTable (pimc), MapRoute's `map` entry, all at 1fc5f50 with c2 / v6-3-2. SurfaceCounts.svelte:67-69 shows "an earlier V's SL report ... simulator at ${data.source.commit}" in visible text (another group's figure, a displayed hash field).
8. **The planned redesign is described five times**: combat-brain/index §3.1 + §6 + Aside, sl-planner/index §5, turn-search §6, beam §7 (with Widening figure), budget §7, plus shared §4 and status.mdx:26. If the future content stays, one "planned redesign" page would hold it once; if it goes, combat-brain/index §6 and shared §4 go whole (renumber shared §5 -> §4: 7 inbound links; status.mdx:26 links combat-brain §6).
9. **The 88% restore figure is told three times** (combat-brain/index §2 "Cost", shared §3 "Where the time goes", budget §3 "Restores"); one place with links from the other two would do.
10. **shared.mdx section order**: the planned §4 sits between the pool (§3) and "what search counts as the same" (§5); moving or dropping it changes §5's anchor, which 7 pages link.
