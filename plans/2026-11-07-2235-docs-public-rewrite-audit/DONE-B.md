# DONE: group B (combat-brain/*, sl-planner/*)

All edits in en, zh-CN (root) and zh-TW, line for line: after the edits every page's three locales have the same line
count, the same headings, the same link targets and the same inline code spans (checked by script). No heading renumbered
or retitled except budget §5 (D4). Nothing built, nothing committed. Walkthrough not edited (see below).

## Per page

**combat-brain/index**
- Status aside: "today's code" -> "the code"; table header "built today" -> "built". §3 table row "search today" -> "search".
- §3: the "Why not one algorithm" paragraph keeps the argument (PIMC's search ends with the turn, little is revealed
  inside a turn, so on SL's one world it would play much as in NoSL; knowing the future pays only past the turn) and
  drops the panel numbers (112/120, 9 more wins) and both links (pimc/budget §6, sl-planner/budget §5).
- §3.1 (planned) "Today two searches" -> "In the code two searches". §3.2 "today's code" -> "the code".
- §4: `ICombatPolicy` / `IChoicePolicy` now "serve the placeholder (`PlayPolicy`) and the card prompts no search weighs
  (`FirstPicks`)" (checked: `PlayPolicy : ICombatPolicy`, `FirstPicks : IChoicePolicy`; no "fast policies that could").
- §5 item 2: dropped the "fixed panel ... reported in budget §5 / pimc budget §6" sentence.
- §7 table: budget row "results" -> "the champion's budget".
- Left on purpose: §2's restore costs (0.28 ms + 0.073 ms, 2.8 ms, ~330/s; SL 0.5 ms + 0.1 ms/step, 10–20 ms). They
  restate simulator/combat §2 and simulator/worlds §7 (group E). If E drops or changes those numbers, §2 must follow.
  Planned §3.1, §6, the Aside's planned table and the planned bullets stay (D1); §3.1's "must pass a NoSL gate" stays as
  planned text although a NoSL gate refuses every character today.

**combat-brain/leaf**
- §4: "Weights: `weights/all/c9-1.json`" -> "the champion's file, `weights/all/c9-2.json`". FIGURES: OutcomeHead still
  draws c9-1's 16 test states from `combat-walk.json` until it is regenerated from c9-2.
- §7 "Its own player": "earlier nets" -> "the SL planner and L0 with the nets that played them, exploring, at the medium
  budget that also plays gates" (c9-2's data was played at medium: FACTS).

**combat-brain/network**
- §8: "The file is the champion's, `weights/all/c9-2.json`." Size table unchanged: recounted on c9-2, 1,738,813, same
  vocab.
- §8 "What this file holds" rewritten to c9-2's own facts, measured on the file's ONNX initializers (script in my
  scratchpad, `uv run` in `learn/`): its data (21,272 runs / 2,118,826 round starts; 4,310 / 430,142 held out, from
  `fit`); untrained table rows hold zeros in their first 32 numbers and small random numbers in the other 32; learned
  rows: D_c 180/597, R 261, B 69, Z 39, X 9/266, O_g and O_h all five orbs, O_j 44/60 (places 1–6 and 8 all five orbs,
  7 and 9 four, 10+ Lightning only); modifiers all but `keyword:None`, `rider:None`, Tinker None/Curse/Quest/Status;
  unheld ids (e.g. `BEACON_OF_HOPE`, 42 cards) have `<unk>`'s first 32 numbers; zero-weight inputs: 22 of the ally
  layer's 192 (all in the allies' powers), 2 of the allies' 128 numbers at the body (c9-1 had 4), the potion-slot
  column; the potion head's five groups identical. No "went on from", no Silent-A10 / widening story. The per-character
  context rows sentence went (all 96 context rows have learned; it only made sense with the lineage).

**combat-brain/shared**
- §2: "the part of the public record that is built today" -> "the one part ... that is built". Planned bullet keeps its
  place without the 82–89% -> 96–98% fidelity numbers ("a built world's monsters take their move logs from it").
- §3: "Deterministic" drops the 6-combat panel trace. "Where the time goes" drops the 40 s NoSL profile
  (48/38/7/4/16/16/5 %), the 88 % SL restore share, "rather than the nodes" and "about 160 MB"; it now says restoring
  states, not the leaf, is the largest share of a worker's time (cites simulator/combat §7) and why (each request
  restores, and every step back of a search restores again: `TurnDfs.Visit`, `WorldSearch`, checked). The "Speed"
  paragraph (68.7 vs 307.5 ms) deleted.
- §5: "no longer tells" -> "does not tell". Planned §4 stays.

**sl-planner/index**
- Intro: dropped "that an earlier V went on to win" and "from a 300-run evaluation"; "what the leaf of that run's
  champion gave" -> "the leaf's value of a win". FIGURES: L21 and L25 (seed `E91KLB4BRWB4`, Vantom, 5.2 s, 22 actions,
  the whole poison-plan story, 0.4914, 34 of 70 HP) all describe PlanTimeline's old fight and must be rewritten when
  `combat-examples.json` `sl` comes from the champion's play; the description, combat-brain/index §7 and this page's
  "a real boss fight" wording depend on whether the new fight is a boss fight.
- §4: deleted the Lava Rock story and the old evaluation (148/120, 5,375 fights, 95.6 %). Now: why a clone error shows
  as a replan; the checkpoint carries relics' plain fields (link kept); "In the champion's report runs, 100 for each
  character, no step failed: 0 replans"; a fight is planned again on purpose when its plan expects to lose (beam §6) or
  when the plan ends at the 40-turn cap (beam §1, `SlPlanner.MaxTurns`).
- §5 (planned) "as today" -> "as in the beam". §6 table "measured results" -> "the champion's budget and speed".

**sl-planner/turn-search**: §5 "played move for move as before" -> "as it would without this rule". Nothing else.

**sl-planner/beam**
- §3: dropped the 99.8 % parenthesis (and "usually a win") and the 14–16 % node saving; the dive's turns give "the plan
  a fresh search would give".
- §4: dropped 9.5 s vs 13.8 s and the narrower-beam try; kept "most of medium's saving" and "narrows only fights the
  dive already wins".
- §5: dropped the hashed 23-of-24 example (measured with p0) and "none of 21 plans was proved".
- §6: the L rationale without numbers (each plan is a whole search; without a limit a lost fight is planned at every
  turn); the dated panel table, its intro and the closing 2.95 sentence deleted; one closing line from the rules ("a
  fight whose plans keep losing is planned three times: two plans one turn each, the third to its end").
- §7 (planned): "today's dive" -> "the dive of §3"; §7.2 "about 460 a second (Byrdonis ...)" and "88 %" -> "each node is
  an action of the game's own code, and each step back a restore"; "may be compared later" -> "A comparison with
  depth-first search is open"; §7.3 header "today" -> "built".

**sl-planner/budget** (REWRITE)
- description and intro: no "of self-play", "what it has measured", "today".
- §1: table without the results column; the two paragraphs of paired runs and "the same in every room" try deleted;
  replaced by what medium differs from high in (easy beam, losing-plan limit), low narrower, every room the same.
- §2 "The low budget": the hand-set command as an option a run may take; it is named apart from `--effort low` (300
  nodes, beam 8, replans losing fights); the champion, its self-play and its gates play medium (links
  macro-brain/selfplay §2 and evaluation/gates §10). The 515-boss-fight correlation (0.83 / 0.94) deleted.
- §3: only "Restores": how a turn's search restores (`TurnDfs.Visit`), SL restores grow with the node, restores not the
  leaf are the largest share, so cost grows with beam, plans and nodes. The old-V table (5,375 fights) and its five
  bullets deleted (no current measurement exists in git; the reports keep no per-fight time).
- §4: "The SL champion's runs use 4 workers each" -> "A run's pool has `--workers` processes, 8 by default (one per
  world of `--worlds`)" (`Args.cs`: `--workers` defaults to `budget.Worlds`).
- §5 "The champion": the SL champion's budget and nets (no net names), its five reports (100 SL runs, A0,
  `promotion-1-100`), wins and floors by link to champions §2 (FACTS: win counts only there and in the overview), 0
  failed / truncated / random decisions, and a table of `ms_per_decision` / `seconds_per_run` per character (FACTS'
  numbers, re-read from the five report files). The 2026-10-28 panel, the whole-run PIMC comparison and the c9-1 "Now"
  deleted.
- §6 "Lost fights cost time" without the quarter / 5 % numbers. §7 (planned): "across hosts" -> "across machines", the
  panel scale -> "about 8 × 300 nodes a turn at medium"; "can still differ, as they do now" -> "can differ, as a run can
  take the low budget"; "today" dropped.
- Title "Budget, cost and results" kept (only §5's title changes under D4).

**combat-brain/walkthrough**: not edited. Nothing on it breaks the rules except the file it walks, which is data. For the
figures step (regenerate `combat-walk.json` from c9-2; audit B lists the lines): L14 (`weights/all/c9-1.json`,
`tests[9]`), L18 (character, encounter), L23 (Juggling), L29–32 (potion, orbs, Hard to Kill), L36–38 (per-enemy powers by
index), L54 ("here 1 round", slots), L62, L64 ("six relics"), L76–85 (the "why" column), L89 and L101 (Strike, the
arithmetic 0.228 + ... = 0.216), L118 (Juggling), L132, L138–142 (bag counts), L180–183 (0.839, 0.161, "3 buckets"),
L191–196 (HP conversions, Burning Blood 6 HP), L200–202 (Fairy in a Bottle; "the five groups hold the same weights"
stays true for c9-2), L214, L224 ("83 HP of 93"), L236–238, L242–253 (The Kin, Phrog Parasite), L259–261 ("78/93").
Also `docs/scripts/combat-walk.py:21`, `docs/scripts/check.mjs` (c9-1 path) and the `OutcomeHead.svelte` comment.

## Links others must match

- **evaluation/gates §5 (L100, "combat panel" row)** links sl-planner/budget §5 and pimc/budget §6 as "the panels
  measured". budget §5 no longer holds a panel: drop that clause (or the link to budget §5).
- **pimc/budget §2 (L50)** cites sl-planner/budget §5 for "the same outcome in 112 of the 120 fights": gone; drop the
  link with the panel numbers. pimc/budget §2 L36 repeats the dropped worker speed (68.7 vs 307.5 ms).
- **evaluation/gates §6 (L159)** cites budget §1 for "medium ... passed such a gate, on floors, after 33 pairs": gone
  from budget §1 (the plan drops it there too).
- **macro-brain/selfplay §2 (L33)** cites budget §2 for the 515 boss fights (0.94 / 0.83): gone. **learning/loop §2
  (L62)** and **learning/combat-net §3 (L35)** link budget §2 for low-budget datasets: budget §2 now says self-play plays
  medium.
- **simulator/worlds §6 (L95)** cites sl-planner §4 for "148 and 120 replans in 2 of 40 SL runs": gone from §4.
- **simulator/combat §7**: shared §3 (and budget §3 through it) now rely on it for "restoring states, not the leaf, is
  the largest share of a worker's time". Keep that statement there (the plan's reduction) or tell me.
- New links from my pages: budget §2 -> evaluation/gates §10 and macro-brain/selfplay §2; budget §3 ->
  simulator/worlds §7; budget §5 -> evaluation/champions §1 and §2; sl-planner §4 -> beam §1; network §8 ->
  learning/combat-net §3. combat-brain/index §3 and §5 no longer link budget §5 or pimc/budget §6.

## Where the code disagreed with FACTS, the plan or the audit

1. sl-planner §4 said "the run's record counts the replans". It does not: `run` prints each run's replans and the
   batch total (`RunCommand.cs:94-96, :139`); the run record has no such field (harness/decision-log §6 already says
   so). Fixed.
2. Plan: "0 replans, from its reports". The reports keep no replan count (`RunRecord` / report have no field). The 0 is
   the gate job's run output as recorded in `eval/ledger.md` (the 2026-11-06 gate: "0 replans"; the later no-hand gate
   also 0). I wrote "In the champion's report runs, 100 for each character, no step failed: 0 replans" without a link.
   Lead: keep, or drop the number if a source outside the ledger is required.
3. The champion's report files were played with prompt route `p0` (`config.champion` says "combat prompts p0", runs'
   `brains` count `p0` decisions) at e9c1bc6, while `sl.json` says `first` and the current search differs. budget §5's
   speed numbers are those reports' (as FACTS gives them); they are not the current build's search.
4. Audit's suggested beam §6 rationale ("few lost fights turn after the third plan") does not follow from the old numbers
   (5 of 12 late wins came after the third plan). Used a rationale without that claim.
5. budget §4's "4 workers each" is in no champion file; `--workers` defaults to 8 (`Args.cs`), the mod's brain starts
   half the logical processors, 1 to 8 (`BundledBrain.cs:91`).
6. network §3 says each table entry starts as random numbers of sd 0.1 (true of a fresh `train`); the champion's file
   shows untrained rows with zeros in their first 32 numbers, a trace of the nets it went on from. §8 states the fact
   without the lineage, so a careful reader may wonder why; left as is.
7. leaf §4/§7 describe a potion's chance per bucket group; in the champion's file the five groups are identical
   (network §8, walkthrough §12), so in practice a potion's chance does not follow the HP lost. Not changed (design text).
8. Audit numbers for c9-2 confirmed on the file (R 261, Z 39, X 9, O_j 44, allies at the body 2, ally layer 22, D_c 180,
   B 69, potion-slot column zero, groups identical).
