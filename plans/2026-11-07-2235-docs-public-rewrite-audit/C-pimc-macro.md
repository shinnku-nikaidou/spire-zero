# Audit C: NoSL PIMC + macro brain (English reference pages)

Facts checked against HEAD: `eval/champions/{sl,nosl}.json` (champion = c9-2 + v9-2 in both; NoSL `reports: {}`),
`weights/all/v9-2.json` (fit: 21,272 runs, 5,608 won; same mu/sigma as v9-1; nonzero rows from the ONNX initializers:
damage 170, block 68, mod amounts 20, relic counter 0, relic status 0, identical to v9-1), `eval/ledger.md` rows v9g1/v9g2
(medium budget, `--explore` 0.3/0.1/0.02, v9g2 at A0-A10), `learn/gate.py`. Code citations checked at HEAD and correct:
MacroBrain.cs:145, 201-203; CloneSearch.cs:81-92; Args.cs:236; ChampionOptions.cs:40, 42-43; RunNet.cs:174-221.
No code comment in src/ learn/ docs/src cites a section proposed for deletion or renumbering below.

---

### en/arch/pimc/index.mdx  (lines 104)  -- FATE: EDIT
Reason: sound description of worlds, fidelity and fair play; a stale combat-net claim, a dated measurement, a fixed-bug story and a planned section.
Sections:
  - intro (L13-23) -- edit (Aside L17-21 is future; drop with §5)
  - §1 `#sec-1` What NoSL cannot see -- edit (L35)
  - §2 `#sec-2` Sampled worlds -- keep
  - §3 `#sec-3` How faithful a world is -- edit (L65, L74, L76)
  - §4 `#sec-4` Fair play -- edit (L85, L86)
  - §5 `#sec-5` The planned form -- delete (future only)
  - §6 `#sec-6` This chapter -- renumber to §5, edit
Findings:
  - L3 future "and the planned simpler form of Monte Carlo + net" -> drop clause from description
  - L5-8 future badge "built, redesign planned" -> decide with the future list (likely "built")
  - L17-21 future Aside "§5 sums up the planned form, which is not implemented" -> drop
  - L35 stale/version "The combat net shows the same picture ... 57.7% ... 28.9%" -> drop sentence: the current combat net has no intent output (leaf §4 / arch.ts: outcome + potion_used only), and no other page holds these numbers
  - L65 date "With Silent's starter deck (2026-10-27)" -> drop the date; table may stay as the builder's fidelity
  - L74 history-ish "What the builder still approximates" -> "What the builder approximates"
  - L76 history (bugs found and fixed) "Five of the first 400 PIMC runs failed ... All three are fixed" -> keep only "In whole runs a world built wrong fails the decision."
  - L82 "Only the observation leaves the host" -> host = the Harness host process, not a fleet host; OK (optionally "the Harness host")
  - L85 stale "the same way the mod will from the real game" -> "does" (mod's NoSL assist mode is built per status.mdx)
  - L86 future "Planned: a counterfactual test ..." -> future list
  - L88-97 future (whole §5) -> delete
  - L103 future "the same search on `TurnEnds`"; L104 "the planned budget" -> drop phrases
Inbound links to deleted/renumbered sections: `pimc/#sec-5`: only in-page L19 (deleted with the Aside). `pimc/#sec-6`: none. L97 (inside deleted §5) links search §7 and budget §8 (also deleted).
Figures/data: WorldSampler.svelte -> visible text clean; reads `combat-examples.json` `nosl.obs`/`nosl.draw`. That JSON's `nosl.source` (not rendered) holds hash `rev: "1fc5f50"`, old names `"pimc c2 8x300 ... macro l0 v6-3-2"`, job `"... NoSL champion (pimc c2 8x300, L0 v6-3-2) on promotion-1-300 #0"` and run id `034f8a6ab80c494f` (16 hex, trips a hash grep) -> strip `source` to seed/floor/how.
Twins: match.
Future/next content: Aside L17-21; §4 L86 counterfactual test; §5 whole (merged `TurnEnds` form); L3, L5-8, L103-104 mentions.
Rough size of the rewrite: ~17 lines removed, ~6 rewritten.

### en/arch/pimc/search.mdx  (lines 109)  -- FATE: EDIT
Reason: core search/average/CRN/fusion content is current design; one dated probe, an "earlier net" label, an ISMCTS-measurement plan and the planned §7.
Sections:
  - intro (L10) -- edit
  - §1 `#sec-1` The search in one world -- edit (L18)
  - §2 `#sec-2` The average by first action -- keep
  - §3 `#sec-3` Noise and common random numbers -- edit (L49)
  - §4 `#sec-4` Strategy fusion -- edit (L68-70)
  - §5 `#sec-5` The sampler's bias -- keep
  - §6 `#sec-6` Cycles inside a turn -- keep
  - §7 `#sec-7` The search on TurnEnds -- delete (future only; last section, no renumber)
Findings:
  - L3 future "and the planned search on TurnEnds" -> drop from description
  - L10 future "[§7](#sec-7) is what the planned design changes in it" -> drop
  - L18 experiment+date "How often that happened was measured with a probe ... (2026-10-27) ... so no decision changed." -> drop the measurement sentence, keep the rationale for keying with the hidden state
  - L49 version "(PIMC at 8×300 with an earlier V and combat net; seed #0 of `promotion-1-300`)" -> "(PIMC at 8×300; seed #0 of `promotion-1-300`)"; the values are an older leaf's, fine as an illustration, or regenerate with c9-2
  - L68 future "So ISMCTS is held in reserve until fusion is measured to matter." -> future list (keep the literature comparison as rationale)
  - L70 plan+experiment "The measurement that decides it ... A first try on 36 decisions ... about a minute." -> delete paragraph; to future list
  - L90-109 future (whole §7) -> delete
Inbound links to deleted/renumbered sections: `pimc/search/#sec-7`: combat-brain/index.mdx:74, combat-brain/index.mdx:173, pimc/budget.mdx:79, pimc/budget.mdx:83, pimc/index.mdx:97, in-page L10.
Figures/data: WorldsTable.svelte -> visible text clean (no names/hashes); same `combat-examples.json` `nosl.source` issue as above (rev 1fc5f50, c2, v6-3-2).
Twins: match.
Future/next content: L68 ISMCTS reserve; L70 fusion measurement plan; §7 whole.
Rough size of the rewrite: ~25 lines removed, ~3 rewritten.

### en/arch/pimc/budget.mdx  (lines 83)  -- FATE: REWRITE
Reason: §1, §3, §5 are current facts; §2 and §4 carry gate experiments with dates; §6 is a results history with "earlier" nets and a stale champion; §7-§8 are future.
Sections:
  - intro (L10) -- keep
  - §1 `#sec-1` Worlds and nodes -- keep (L16 "nothing has tuned them yet" -> "untuned")
  - §2 `#sec-2` Following the plan -- edit (drop L22)
  - §3 `#sec-3` One share per first action -- keep (L28 pointer to §7)
  - §4 `#sec-4` The same budget in every room -- rewrite to one or two sentences
  - §5 `#sec-5` Workers -- keep (dedupe timing with §1)
  - §6 `#sec-6` Results -- rewrite to "The champion" (keep anchor) or delete
  - §7 `#sec-7` Weaknesses and open questions -- future decision (item 6 is a current limitation)
  - §8 `#sec-8` The budget on TurnEnds -- delete (future only)
Findings:
  - L3 history/future "what it has measured, its weaknesses and open questions, and the planned budget on TurnEnds" -> trim description
  - L22 experiment "Following every plan the worlds agree on ... did not pass its gate ... −0.72 ± 0.92 floors" -> drop paragraph
  - L32 experiment+date+story "twice the nodes per world in boss fights failed its gate on seeds 0–99 (2026-10-28) ..." -> keep "Every room gets 8 × 300" (spire-mentor prior art may stay as rationale); "four times the nodes is untried" -> future list
  - L36 duplicate measurement "68.7 ms per action against 307.5 ms" vs L14 "about 70 ms" -> keep one
  - L42 history "measured while card prompts were still answered at random, with the hand-made leaf" -> drop with the panel table L44-48
  - L50 history/version "With the fixed prompt rule of the time and an earlier combat net ..." -> drop
  - L52-64 history/version/story: whole-run table rows "an earlier V", "an earlier combat net"; "passed its gate ... after 14 pairs, by 8.21 floors"; "after 77 pairs, by 2.36 floors"; "With later V's ... 23.96 and 26.16" -> drop
  - L66 stale "PIMC with the combat net c9-1 ... L0 with V v9-1" -> c9-2 / v9-2 (eval/champions/nosl.json); "no NoSL report" is still true -> this paragraph becomes §6
  - L70-75 future/open questions -> future list (item 6, no NoSL report and never gated in NoSL, is a current limitation)
  - L77-83 future (whole §8) -> delete; L81's MoveRecord rationale is already in index §3 L74
Inbound links to deleted/renumbered sections: `budget/#sec-6` (if rewritten in place the anchor stays, but the panel results these links cite are gone): combat-brain/index.mdx:79 (SL vs NoSL "112 of 120 fights"), combat-brain/index.mdx:160 and evaluation/gates.mdx:100 (panel results) -> retarget to sl-planner/budget §5 or drop. `budget/#sec-7`: status.mdx:83, in-page L28 (if §6 is deleted instead, §7 becomes §6 and these change). `budget/#sec-8`: combat-brain/index.mdx:34, combat-brain/index.mdx:155, pimc/index.mdx:97.
Figures/data: none imported.
Twins: match.
Future/next content: L32 "four times the nodes is untried"; §7 open questions 1-5; §8 whole.
Rough size of the rewrite: ~35 lines removed, ~5 rewritten.

### en/arch/macro-brain/index.mdx  (lines 71)  -- FATE: EDIT
Reason: clean overview; only the status Aside, badge and chapter table carry future content.
Sections:
  - Aside Status (L16-20) -- edit
  - §1 `#sec-1` What it decides -- keep
  - §2 `#sec-2` The method in one picture -- keep
  - §3 `#sec-3` This chapter -- edit (row L71 follows the fate of next.mdx)
Findings:
  - L5-8 future badge "partly built" -> decide with the future list
  - L18 future "in NoSL, where run worlds do not exist yet" -> "in NoSL it predicts each afterstate from the screen"
  - L18 future "Planned: afterstates from run worlds ..., the L1 rollout check, and the `IMacroBrain` interface ([Next steps §2])" -> future list
  - L71 future row "what is planned but not built, open questions" -> follow next.mdx's fate
Inbound links to deleted/renumbered sections: none.
Figures/data: none.
Twins: match.
Future/next content: L18 "Planned:" sentence; badge; L71.
Rough size of the rewrite: ~1 line removed, ~2 rewritten.

### en/arch/macro-brain/afterstates.mdx  (lines 70)  -- FATE: KEEP
Reason: design argument; two future mentions.
Sections:
  - §1 `#sec-1` From Q(s, a) to V(s′) -- keep
  - §2 `#sec-2` One scale for every surface -- keep
  - §3 `#sec-3` Random afterstates and the two environments -- edit (L53)
  - §4 `#sec-4` Why one V -- edit (L68)
Findings:
  - L53 future "mean over K sampled worlds (planned)" -> say what NoSL does now (predicted from the screen), move the plan to the future list
  - L68 future "distilling SL's L0 into a policy is a candidate then" -> future list or drop
  - L64 "both were weighed against the single V" -> design rationale, fine
Inbound links to deleted/renumbered sections: none.
Figures/data: none.
Twins: match.
Future/next content: L53, L68.
Rough size of the rewrite: ~2 lines.

### en/arch/macro-brain/search.mdx  (lines 87)  -- FATE: EDIT
Reason: current description of L0 on the screen and the clone; one "until", one old cost measurement, one check with "an earlier combat net".
Sections:
  - §1 `#sec-1` Afterstates predicted from the screen -- edit (L11)
  - §2 `#sec-2` L0 on the clone -- keep
  - §3 `#sec-3` Searching through a node's screens -- keep
  - §4 `#sec-4` Card prompts -- keep
  - §5 `#sec-5` No way back -- keep
  - §6 `#sec-6` Cost and where the probes run -- edit (L83, L87)
Findings:
  - L11 future "Until run worlds exist (...), NoSL has no world to play an option in" -> "NoSL has no world to play an option in"
  - L83 measurement of unclear vintage "a macro decision took 40 ms on average ... 9 s of a 187 s two-run total" vs L84 "70–90 ms" -> keep one current number
  - L87 experiment/version "(17 runs with `play` in combat, 2 with the SL planner and an earlier combat net)" -> state only what `run --check` checks
Inbound links to deleted/renumbered sections: none.
Figures/data: none.
Twins: match.
Future/next content: L11 (implied run worlds).
Rough size of the rewrite: ~1 line removed, ~2 rewritten.

### en/arch/macro-brain/network.mdx  (lines 221)  -- FATE: EDIT
Reason: architecture text is current; §6 and §9 describe v9-1 (no longer the champion) with lineage, and one claim about value sizes is stale.
Sections:
  - §1 `#sec-1` The whole net -- keep
  - §2 `#sec-2` What it reads -- keep
  - §3 `#sec-3` The deck -- keep
  - §4 `#sec-4` The bags: relics, potions, context, map -- keep
  - §5 `#sec-5` The body and the heads -- keep
  - §6 `#sec-6` Sizes and cost -- edit (L192)
  - §7 `#sec-7` One forward pass, two implementations -- edit (L202)
  - §8 `#sec-8` What the structure cannot express -- keep
  - §9 `#sec-9` One run through the net -- edit (L219 + NetWalk data)
Findings:
  - L192 stale+version "The file is `weights/all/v9-1.json`. It went on from v9-0 and learned from v9g0: 29,987 SL runs ..., 2,057 of them won." -> champion is `weights/all/v9-2.json`: 21,272 SL runs (A0, five characters, medium budget), 5,608 won (its `fit`)
  - L192 history "Every width has learned." (the 2x widening) -> drop
  - L192 version/lineage "Its μ and σ are kept from the first net of its line, whose training runs were all at ascension 0" -> "Its μ and σ give the ascension μ 0 and σ read as 1, so A10 reads as 10" (true for v9-2). The row counts (170 / 68 / 20; relic counter, status, melted zero) hold for v9-2 (checked).
  - L202 stale "Values of P(win) are mostly below 0.1" -> v9-2's 8 test values are 0.04-0.79 (7 above 0.1); reword the reason for the relative tolerance
  - L219 version "A real sample from v9-1's file" -> "from the champion's file"; the walk must then come from v9-2's `tests[0]` (a different run: Defect, act HIVE, floor 24, value 0.172) or say "a net file's test run" without a name
Inbound links to deleted/renumbered sections: none.
Figures/data: ArchDiagram.svelte + arch.ts `valueNet` -> clean (structure only, no names, no weights). EnteringFork.svelte -> clean (illustrative, no weights). NetWalk.svelte -> all numbers hard-coded from v9-1's `tests[0]` (Defect, floor 42, P(win) 0.00235; code comment L2 "v9-1's tests[0]"); step "Six numbers" visible text "kept from the nets it went on from ... the first of those nets' training runs were all A0" is lineage -> rewrite as a fact; regenerate from v9-2 or drop the name. No macro-brain figure loads any weights file at runtime; `docs/src/lib/v/` does not exist; `macro-examples.json` is not used by this chapter.
Twins: match.
Future/next content: none.
Rough size of the rewrite: 2 paragraphs rewritten (L192, L219), 1 sentence (L202), NetWalk's step data.

### en/arch/macro-brain/value.mdx  (lines 135)  -- FATE: KEEP
Reason: current definition, labels, loss and training; one stale count and one job-deadline word.
Sections:
  - §1 `#sec-1` What V is -- keep
  - §2 `#sec-2` From hazards to a value -- edit (L40, L41)
  - §3 `#sec-3` From a logged decision to a sample -- keep
  - §4 `#sec-4` The second head -- keep
  - §5 `#sec-5` Training -- keep
  - §6 `#sec-6` The report -- keep
Findings:
  - L40 stale/version "Few runs are won: 286 of the 21,275 runs of a generation's self-play" -> the champion's training read 21,272 runs with 5,608 won (26%); update the numbers and soften "few", or drop the numbers
  - L41 host (job deadline) "truncated, or its time box ended" -> "truncated, or its job was stopped"
  - L98 "an experiment changes them there, and only a result keeps the change" -> process rule, OK (optional drop)
Inbound links to deleted/renumbered sections: none.
Figures/data: none.
Twins: match.
Future/next content: none.
Rough size of the rewrite: ~2 lines.

### en/arch/macro-brain/selfplay.mdx  (lines 87)  -- FATE: REWRITE
Reason: the procedure is right in shape, but its command, budget, tiers, gate size and champion are stale, and generation history and a host deadline are mixed in.
Sections:
  - §1 `#sec-1` The loop -- edit (mermaid L18)
  - §2 `#sec-2` The runs -- rewrite (L28-33)
  - §3 `#sec-3` Exploration: even draws -- edit (L49)
  - §4 `#sec-4` A generation -- rewrite (L61, delete table L66-73)
  - §5 `#sec-5` The gate -- rewrite (L79, L85)
Current procedure (the facts a reader needs to run it; keep):
  - loop: champion plays whole SL runs with even draws at ε -> decision logs + run records -> `v.train` on that generation alone, `--init` from the champion -> gate -> promote into both champion files
  - command: `run --env SL --policy plan --macro l0 --afterstates clone --workers 4 --effort medium --character C --asc N --combat-net weights/all/c9-2.json --value weights/all/v9-2.json --explore ε --seed 1` (the ledger's running generation plays medium, A0-A10)
  - exploration: formula L41-43, what is drawn (L45), ε fixed per run, `explored` mark, `eps ε` name suffix, gate refuses exploring challengers, why even (L47), the label caveat (Aside L51-55)
  - tiers: three tiers, 0.3 / 0.1 / 0.02, and why (L49 last sentence)
  - generation: a job per character, tier (and ascension); runs not fixed; train seeds from a reserved block (`eval/ledger.md`) never overlapping gate seeds; failed runs dropped (L75)
  - gate: both pairs play, 100 seeds of `promotion-1-100` per character at medium, no exploration, on the clone; paired wins, `d̄ − 1.645·SE > 0`; five characters read together; `learn/gate.py --against` and its report (L87)
Generation history (drop): L33 v9g0/v8g2/v7g1 budgets and timings; L49 per-generation tiers (v7g1, v7g2, v8g2, v9g0) and "wide again since for four of the characters it is the first data"; L66-73 table (v8g2's tiers 0.06/0.03/0.01, 10,038 runs, 165 won); L79 "v9-1 passed its gate with c9-1".
Findings:
  - L18 stale "gate 500 paired seeds, wins" -> "gate 100 paired seeds per character, wins"
  - L28-30 stale command: `--effort low --nodes 150 --beam 4 --easy-beam 0 --loss-plans 0 --replan 0`, `--combat-net weights/all/c9-1.json`, `--asc 0` -> medium, c9-2, `--asc N`
  - L33 version/history "The latest generation, `v9g0` ... `v8g2` ... `v7g1` ... 45–85 s" -> "Self-play plays at the champion's medium budget"; the low-vs-medium correlation (515 boss doors, 0.94) argues for a budget not used now -> drop here (lives in sl-planner/budget §2)
  - L49 version "0.3, 0.15 and 0.05 in `v7g1`; ... 0.3, 0.1 and 0.02 in `v9g0`, wide again ..." -> "Each generation's runs are split into three tiers of ε: 0.3, 0.1 and 0.02."
  - L61 host/deadline "one time-boxed job per tier ... a deadline, which sits in a file on each host and can be moved while the job runs" -> "a job per character, tier and ascension; the number of runs is not fixed"
  - L61 "a block entered in the ledger" -> name `eval/ledger.md` only if that file is cleaned (it is in git and full of hashes, dates, host names, plan paths and "the user")
  - L66-73 version/stale size table "from the latest one played" -> delete
  - L79 story/version "The champion's V, v9-1, passed its gate with c9-1 on every character at ascension 0" -> delete (also stale: champion is v9-2)
  - L79 stale "and 200 of `promotion-1-200` at the low budget" -> drop (the gate is 100 medium per character); "at the ascension its data was played at" -> state the gate's ascension for an A0-A10 generation
  - L85 stale "At 500 seeds this takes about 2–3 percentage points more wins." -> recompute for 100 per character or drop
Inbound links to deleted/renumbered sections: none (all five sections stay). For reference, links in: `#sec-1` evaluation/index.mdx:99; `#sec-2` environments/worlds.mdx:107, learning/loop.mdx:56, sl-planner/budget.mdx:29 (says the data "was played at a cheaper budget": stale with the medium-budget procedure); `#sec-3` learning/loop.mdx:150, macro-brain/index.mdx:18, :58, next.mdx:10, value.mdx:82; `#sec-4` learning/index.mdx:55, learning/loop.mdx:60, value.mdx:18, :40; `#sec-5` learning/loop.mdx:142, value.mdx:135.
Figures/data: none.
Twins: match (zh-CN/zh-tw L61 carry 主机/主機 and 截止 for "host" and "deadline").
Future/next content: none.
Rough size of the rewrite: ~20 lines removed, ~10 rewritten.

### en/arch/macro-brain/next.mdx  (lines 73)  -- FATE: EDIT §1 now; §2-§3 FUTURE (decide later)
Reason: no history; §1 is the current limitations (keep), §2 is entirely planned-not-built, §3 open questions; one stale limitation and two stale facts embedded in the plans.
Sections:
  - §1 `#sec-1` Known weaknesses -- edit (limitations of the current system; L11 stale)
  - §2 `#sec-2` Planned, not built (subheadings L1: rollout check, NoSL: run worlds, Interface, Budget, Tests; unnumbered) -- future (all of it)
  - §3 `#sec-3` Open questions -- future / open questions
Section by section, future vs history:
  - §1: limitations of the current system, not history, not plans; only L10's last sentence "A cut point per run would remove this (§3)" is a plan
  - §2: future only; no history. It embeds one current fact (L52 "Out-of-combat card prompts arrive through the same `card_choice` surface ... Harness routes by whether a combat is running") that belongs on search §4 or harness/routing if §2 goes
  - §3: open questions (future); Q1 cut point repeats L10, the selfplay Aside L51-55 and learning/loop.mdx:161; Q5 is NoSL-only
Findings:
  - L10 future "A cut point per run would remove this ([§3](#sec-3))" -> future list
  - L11 stale "The route sees no build, the max mixes paths, and the route ends with the act" -> the net has no route encoder and no max pooling; network §8 says the map bag holds no links and the map ends with the act -> rewrite to those
  - L39 stale citation in code block "(docs: harness §4)" -> harness §4 is now "The budget" (harness/routing); routing is harness/routing §1
  - L62 stale test "and the fallback count is 0" -> Harness routes with no fallback (status.mdx); drop the clause
  - L64 duplicate "NoSL counterfactual: the same public record with different hidden state" = pimc/index L86
Inbound links to deleted/renumbered sections (whole page or §2/§3 removal): page: macro-brain/index.mdx:71 (chapter table), sidebar `astro.config.mjs:33`; `next/#sec-1`: macro-brain/selfplay.mdx:53; `next/#sec-2`: macro-brain/index.mdx:18; `next/#sec-3`: learning/loop.mdx:161, in-page L10. If only §2 goes, §3 -> §2 and learning/loop.mdx:161 and L10 change.
Figures/data: none.
Twins: match.
Future/next content: §2 whole (L1 rollout check, NoSL run worlds, IMacroBrain interface, budget per mode, tests), §3 Q1-Q5, L10.
Rough size of the rewrite: §1 1 line rewritten; if §2-§3 go, ~60 lines removed.

---

## Cross-page issues

1. **Champion stale in four places.** budget.mdx L66 (c9-1, v9-1), network.mdx L192 and L219 (v9-1), selfplay.mdx L30 (c9-1) and L79 (v9-1); both champion files name c9-2 + v9-2. Also outside this group: `docs/scripts/check.mjs:68` and `docs/scripts/combat-walk.py:21` check and build the combat walk from c9-1.json; `components/combat/OutcomeHead.svelte:2` comment says c9-1.
2. **docs/README.md L18-20 is stale.** "`src/lib/v/` holds V's forward pass ...; `pnpm check` makes it value the test runs of `weights/silent/v8-3.json`": `src/lib/v/` does not exist, `weights/silent/` is gone, and check.mjs checks no V (only the combat walk vs c9-1). The macro-brain figures load no weights at runtime (NetWalk is hard-coded from v9-1's `tests[0]`).
3. **Figure data with hashes and old net names.** `src/data/combat-examples.json` (`sl`, `panel`, `nosl` `.source`: `rev 1fc5f50`, `c2`, `v6-3-2`, run ids). `src/data/macro-examples.json` (`source.rev 1fc5f50`, job `data/silent/sl/eval/2026-10-30-eval-v6-3-2-clone`, `v6-3-2`; its option values 33-48 are E[floor] units of a retired V). macro-examples is used by `components/book/DecisionLog.svelte` and `TdTargets.svelte` (another group), not by this chapter.
4. **The self-play budget changed but other pages still say low.** sl-planner/budget.mdx:29 says the data "was played at a cheaper budget"; the ledger shows v9g1 and v9g2 at medium. selfplay §2 and §5 and probably evaluation/gates §10 (another group) still describe 500 seeds / 200 low on `promotion-1-200`.
5. **Combat-net intent claim.** pimc/index L35 credits the combat net with naming the next intent (57.7% vs 28.9%); the c9 net has no such output and no page holds the number.
6. **v9-2's values are not small.** network L202 ("mostly below 0.1") and value L40 ("few runs are won: 286 of 21,275") describe an earlier data regime; the champion's training data was 26% wins.
7. **The planned `TurnEnds` redesign is told four times**: pimc/index §5, pimc/search §7, pimc/budget §8, combat-brain/index §3/§6 (another group). Removing them together cuts the links listed above.
8. **NoSL run worlds are told five times**: macro-brain/index L18, afterstates L53, search L11, next §2 "NoSL: run worlds", simulator/worlds §8.
9. **The cut-point question is told four times**: selfplay Aside L51-55, next L10, next §3 Q1, learning/loop.mdx:161.
10. **Duplicate timings**: pimc/budget L14 "about 70 ms" vs L36 "68.7 ms"; macro search L83 "40 ms" vs L84 "70–90 ms".
11. **status.mdx** links budget §7 (L83) and holds history items such as "done 2026-11-04 ... V v9-1 and the combat net c9-1, trained on `v9g0`" (L81); another group's page, but it points into this chapter.
12. **`eval/ledger.md`** (in git), which selfplay §4 sends readers to, is full of hashes, dates, host names, `plans/...` paths and "the user". If the docs keep pointing to it, it needs its own cleanup.
