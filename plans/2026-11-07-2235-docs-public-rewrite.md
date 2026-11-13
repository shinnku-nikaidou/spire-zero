# The docs for a public repository: rewrite plan

Drafted 2026-11-07 22:35 Taiwan time, for the repository going public (still private). Approved by the user 2026-11-07 ("Approve": planned sections kept, no renumbering).
**Done 2026-11-07 ~23:45 Taiwan time** (two commits on main, pushed): the text by seven writers in parallel, one per
chapter group (instead of the themed steps 2-7), then the figures; `pnpm check` 0 problems, host scan 0 hits.
Step 10 (deploy) waits for the user's word. Base: main at 8d2b9ed (the rewritten history; line numbers below are the same as at 15a35eb).

The user's rule: **the docs describe the system as it is now.** No commit hashes; no narratives of how things went
(history, versions, earlier experiments, results told as a story, plan numbers, dates of decisions); nothing points into
`.claude/` (out of git since 8d2b9ed); the docs never name the remote hosts, their addresses, the hub or distributed
runs.

Grounding: every English page was read in full by seven read-only audits, which also compared the zh-CN and zh-TW twins
and every figure's component and data file. Their reports, with line numbers, quotes and inbound links for every
finding, are in `plans/2026-11-07-2235-docs-public-rewrite-audit/` (A-F2, plus `code-doc-cites.txt`: the 96
`docs: PAGE §N` citations in code). This plan is the summary and the decisions; the reports are the worklist.

## 0. What the audit measured

- **Commit hashes are fewer than thought**: 8 real hashes in 20 files (counted after the history rewrite, which remapped
  the values inside files: the audits quote the old values, e.g. `81adf16` is now `a3a2d15`, `1fc5f50` is now
  `b013f95`). 4 are cited in pages, x3 locales: sl-planner/beam §5, simulator/combat §7, learning/reference §6 and §8;
  the rest sit in figure data (`docs/src/data/**`, the `rev` / `commit` fields) and in `PanelMock.svelte`'s caption.
  `abc1234` (metrics §10) is the test fixture's made-up commit, a format example. Run ids such as `ce13ba5116b7f48a` are
  16-hex run ids, not commits.
- **History is the larger job**: dataset and net lineage (v7g1 ... v9g0, c7-2 ... c9-1), dated measurements, bug-fix
  stories, old panels and gates, the status page, the champions' lineage, the ledger.
- **Hosts**: named in learning/reference §1 (box job -> remote hosts -> storage hub, deadline files, `box.py`),
  simulator/reference §1 and simulator/pool §7 ("the fleet"), status §2, glossary §7 ("storage host"), metrics §2/§5,
  macro-brain/selfplay §4 and value §2 (deadlines on hosts). No host name or address appears anywhere: the rule held
  for names, not for "remote hosts / hub / fleet".
- **`.claude/` pointers**: evaluation/gates §6 (L153) and §10 (L212), learning/reference §1 (L24), simulator/reference
  §1 (L26), in all three locales.
- **`tmp/` pointers** (not in git): `tmp/notes/*` (index §6, environments/information §6, headless §5, output x2,
  learning/loop §12, evaluation/index §3, champions §3 x2), `tmp/decompiled` (index §6, game), `tmp/build.sh` and
  `tmp/builds/REV/sim.sh` / `tmp/home` (simulator/reference §1, mod/index §4, glossary §7), `tmp/assist/` (sl-record §8,
  auto §3).
- **The docs are also stale against the code**, and "as it is now" means these are fixed in the same pass:
  1. *The brain link.* The mod starts its bundled brain as a child process and talks JSON lines over its stdin/stdout;
     `run --brain`, `brain --socket`, `BrainServer.Listen`, `BrainSocket` and the Install target are gone
     (`BundledBrain.cs`, `BrainCommand.cs:9-10, 25`). About 30 sentences on 12 pages still describe a Unix socket and a
     brain the player starts.
  2. *The champion* is c9-2 + v9-2 (`eval/champions/sl.json`, `nosl.json`); about 20 places still call c9-1 / v9-1
     the champion, with their numbers (74 % Silent, etc.; now 92 / 82 / 68 / 71 / 47 of 100).
  3. *The gate procedure*: both pairs play 100 at medium on `promotion-1-100` per character, same commit, no low
     budget; four pages still describe `promotion-1-300` + a low-budget part.
  4. *Self-play*: medium budget, tiers 0.3 / 0.1 / 0.02, A0-A10; pages say low budget and list old tiers.
  5. *Combat prompts* are `first` and search branches; figures and labels still say `p0`.
  6. *Figures from old champions*: the book's example run and most figure data come from c2 + v6-3-2 (V valued in
     floors, a unit the system no longer has); PanelMock's answers come from a different, newer play of the same seed.
  7. Smaller facts listed in the reports: drifted line citations (`BrainServer.cs`, `Dispatcher.cs`, `RunCommand.cs`,
     `Host.cs`, `learn/v/train.py`), GateTests 8 not 10, no Logging.Abstractions, one board per character, `docs/README.md`
     pointing at `weights/silent/v8-3.json` and a `src/lib/v/` that do not exist, the pimc/index intent claim (57.7 %)
     for an output the combat net no longer has, a "report pending" that exists, and others.

## 1. The rule, made concrete

Goes: commit hashes; old net, generation and dataset names; plan names; dates of events and decisions; "no longer",
"used to", "at first", "since X", "was replaced", "an earlier V / combat net"; bug stories ("this happened when ...");
experiment results that decided something (a gate that passed, a panel that measured an old net); lineage tables;
"the ledger"; remote hosts, the hub, the fleet, time boxes and deadline files, `box.py`; Colab; the LLM gateway; `.claude/`;
pointers to `tmp/`; "Claude reads them as its design manual".

Stays: present-tense design and its rationale ("X, because Y"), including why an alternative is not used, told as an
argument and not as the story of trying it; prior art and credit (Stockfish, spire-mentor, bottled_ai, STS2MCP);
measured numbers of the current build and champion, stated without a date; current file names (`weights/all/*.json`,
`eval/champions/*.json`); formats with placeholders (`DATE`, `NAME`); the word "host" in its protocol sense (the process
holding the true run: the simulator or the mod); `data/` as the folder where outputs go (not in git).

## 2. Decisions

**D1. Planned content: keep it, marked, without history (recommended).** The rule is about history; a design that is
not built yet is a fact about the system ("not built"), and much of the book's design thinking lives in it (combat
`TurnEnds`, iterative widening, run worlds, the event stream, narration, TD(λ) / ExIt, the auto-run driver, open
questions). So planned sections stay where they are, under the existing `planned` badge, with every date, plan name,
"order of work" and old measurement removed, and anything already built rewritten as built. Consequence: **no section
is deleted or renumbered anywhere**, so every `#sec-N` anchor and every code comment's `docs: PAGE §N` stays valid.
- *Alternative A (strict)*: the book shows only what is built; planned sections, planned rows and open-question
  lists go. That removes about 340 English lines in planned sections and paragraphs (23 sections; learning/loop 69,
  macro-brain/next 53, sl-planner/beam 41), more with planned table rows, and renumbers sections on about ten pages
  (learning/loop loses §4, §5, §7, §11, §12; combat-brain/shared §5 -> §4, cited by 7 links and 2 code comments;
  output §6-§8, gamebridge/index §6-§7, ...). If chosen, the
  renumbered anchors, their inbound links and the code comments that cite them change in the same commit, and the
  check of D9 proves it.

**D2. Name the current nets in one place.** Only evaluation/champions and glossary §7 name `c9-2` / `v9-2` (and the
pages that are about one file: combat-brain/network §8, walkthrough, macro-brain/network §6 and §9, learning/reference §6
and §7, which say "the champion's file, `weights/all/c9-2.json`"). Everywhere else: "the champion's V", "the champion's
combat net". The win counts appear only in the overview's "How well it plays" paragraph and in champions §2. A promotion
then changes two pages and the regenerated figures, not twenty.

**D3. One home per procedure.** The gate procedure is stated once, in evaluation/gates §10, with the commands (it
replaces the two `.claude` pointers): both pairs play `eval/seeds/promotion-1-100.txt` with every character at the champion
file's options, only `--value` / `--combat-net` changed, same commit, no `--explore`; then `learn/gate.py --against`.
learning/loop §8, evaluation/index §4 and metrics §5 link to it. The self-play procedure is stated once, in
macro-brain/selfplay; learning/loop §2 and §9 link to it.

**D4. Titles that were history get present titles, same numbers.** learning/reference §1 "Compute" -> "Processes and
outputs", §5 "The ledger and the seeds" -> "Seeds"; evaluation/champions §2 "History and reports" -> "Reports";
sl-planner/budget §5 and pimc/budget §6 "Results" -> "The champion"; mod/sl-record §8 keeps its title (the round trip),
its results go; simulator/combat §6 and §7 keep their titles.

**D5. The status page is deleted** (all three locales). It is dated, its §4 is a changelog, its numbers are stale, and
everything current in it already lives in a chapter. The sidebar's appendices become one appendix (the glossary).

**D6. No link to `eval/ledger.md`.** It is history by nature (and holds hashes, host names, dates). Whether the file
stays in the public repo is the repo session's call (section 9); the docs stop pointing at it either way.

**D7. Figures are regenerated from the current champion, not hand-patched.** One SL run of the Silent on the book's
example seed `0SSL7NS01MU6`, played by `eval/champions/sl.json` with records and a decision log, becomes the source of
every example-run figure, so the book's example run is one play again. Report-based figures read the champion's five
reports. Each data file's `source` keeps only what remakes it (the command with `eval/champions/sl.json`, the seed),
never a rev, a date, a job folder or a net name. A small `docs/scripts/book-data.py` (uv, `learn/` project) writes them,
so the next promotion reruns one script. One run at medium takes about 6 minutes locally (a smoke, not data generation).

**D8. Maintainer rules leave the book.** index §6 "Maintaining it" moves into `docs/README.md` (English is the reference,
three locales in one commit, stable `#sec-N`), without "Claude reads them as its design manual". README also drops the
dead `weights/silent/v8-3.json` / `src/lib/v/` text and says what `pnpm check` checks now.

**D9. The check enforces the public rules.** `docs/scripts/check.mjs` gains, with generic patterns only (no host name
may appear in a public script):
- a lint over the built pages' text and over `docs/src/**` sources (mdx, svelte, ts, json): commit-like hex (7-12 or 40
  characters with a letter and a digit; `abc1234` allowed as the documented placeholder; 16-hex run ids pass by length),
  `.claude/`, `tmp/`, ISO dates `20NN-NN-NN`, generation names `vNgM`;
- every `docs: PAGE §N` in git-tracked code (`git ls-files`, docs excluded) resolves to a built page and anchor;
- the combat walk is checked against the net that `eval/champions/sl.json` names, not a fixed `c9-1.json`.
Host names are checked by a local, untracked scan (names from the private fleet notes) before each commit and deploy.

**D10. `docs/wrangler.jsonc` loses `account_id`** and the account comment: wrangler reads `CLOUDFLARE_ACCOUNT_ID` from
the environment (`.env`). The site URL in `astro.config.mjs` stays (it is where the public site is served).

**D11. Badges.** Sidebar badges are states and stay ("partly built", "built, redesign planned"), except environments'
"decided", which becomes "built". Inline `planned` / `verified` / `not used` badges stay.

## 3. Table of contents

`book` in `docs/astro.config.mjs`: the last group becomes `{ label: '附录', translations: { 'zh-TW': '附錄', en:
'Appendix' }, items: ['glossary'] }`; nothing else moves (D1). The overview's doc map (index §5) follows: "four parts and
an appendix", the status links go to evaluation and evaluation/champions. The file comment above `book` says
"the appendix". If D1's alternative A is chosen, `macro-brain/next` keeps only §1 and is renamed "Known weaknesses"
(`next.mdx` -> `limits.mdx`, sidebar and links updated).

## 4. Page by page

Fates: KEEP (no change or a word), EDIT (sentences), REWRITE (whole sections, numbers kept), DELETE. Every fate applies
to the page's zh-CN and zh-TW twins (the audits found the twins identical in content, line for line). "Planned" below
means it stays under D1, cleaned of dates and old numbers.

### Introduction, Part I, appendix

| page | fate | what changes | why |
|---|---|---|---|
| `index` | EDIT | intro: "first target ... then A10" -> one set of nets for five characters, any ascension; results -> the champion's five reports (92/82/68/71/47 of 100, the Silent's interval), drop the A10 and old-V sentences; §3 mermaid edge "Unix socket" -> "stdin / stdout"; §4: `data/` "where outputs go, not in git" (not "the storage host"), add `distro/` (the mod's packaging), no `pool/`; §5 one appendix, status links retargeted; §6 drop `tmp/decompiled` and `tmp/notes` pointers, move "Maintaining it" to README (D8) | stale results, history, hosts, tmp, maintainer text |
| `arch/game` | EDIT | `tmp/decompiled/sts2` -> "the decompiled game, not in the repository"; "trains first", "so far" -> present; §1, §8 numbers (decisions a run, combat share) from the regenerated example run and the champion's reports (D7) | history, tmp, an old champion's numbers called "the champion" |
| `environments/index` | EDIT | Status aside: drop "today"; §3 "stepping stone" and "considered and rejected ... not to be built" -> rationale ("there is no whole-run environment: ..."); §5 three citations fixed; badge "decided" -> "built" | history wording, stale citations |
| `environments/information` | EDIT | §2 drop the "Rejected" Aside (repeats the overview); §6 `tmp/notes/fair-play-spec.md` -> "rulings the whitelist implements"; §7 relic story -> the rule (the checkpoint carries relics' plain fields, `run --check` compares every node); "today" wording; guard 5 vs glossary contradiction fixed; planned guards and §9 stay (D1) | story, tmp, contradiction |
| `environments/worlds` | EDIT | §6 c9-1 -> "the champion's combat net"; §7 false claim fixed (the NoSL file names no report, so a NoSL gate refuses every character); "an earlier combat net" captions (Determinize) -> "a real start of a round from NoSL self-play" | stale champion, false claim, version |
| `glossary` | EDIT | Silent "trains first" -> present; §3 whitelist entry fixed; §4 "a decision by a placeholder voids an evaluation"; §6 `eval/CHARACTER/leaderboard.md`; §7 names -> the champion's files (`v9-2.json`, `c9-2.json`), drop the ledger row and the `tmp/build.sh` row, `data/CHARACTER/{sl,nosl}/` "not in git" (not "the storage host"); `brain` entry (L37) -> stdin/stdout | versions, hosts, tmp, stale |
| `status` | DELETE | three files; links from index §5 retargeted; `book` updated (section 3) | dated changelog, stale (D5) |

### Part II: deciding

| page | fate | what changes | why |
|---|---|---|---|
| `combat-brain/index` | EDIT | §3: keep the argument why PIMC on SL's one world plays like the planner, drop the 2026-10-28 panel numbers and the link to them; §5: drop the pointer to old panel results; planned §3.1 / §6 stay (D1) | experiment results |
| `combat-brain/leaf` | EDIT | §4 weights file -> the champion's (`c9-2.json`); §7 "earlier nets" -> "the nets that played its training games"; OutcomeHead data from the regenerated walk | stale champion |
| `combat-brain/network` | EDIT | §8 file -> the champion's; the "What this file holds" paragraph -> c9-2's own counts as facts (rows at zero etc.), no "went on from" lineage, no "first half / second half" widening story | lineage, stale numbers |
| `combat-brain/walkthrough` | REWRITE (prose) | regenerate `combat-walk.json` from the champion's combat net, then rewrite the ~40 hand-written lines that describe the state (character, encounter, relics, numbers, arithmetic); §1-§16 keep their numbers | the file walked is not the champion's |
| `combat-brain/shared` | EDIT | §1 "today" wording; §3 drop the 6-combat panel trace and the stale worker profile (48/38/7/16/5 %, measured when restores compared the build) or re-measure; §5 "no longer tells apart" -> "does not tell apart"; planned §4 stays | stale measurement, history wording |
| `sl-planner/index` | EDIT | intro example: the fight and plan value from the regenerated champion play (D7), no "an earlier V", no "300-run evaluation"; §4 drop the Lava Rock bug story and the old evaluation's replan numbers (state the champion's: 0 replans, from its reports) | story, old V |
| `sl-planner/turn-search` | KEEP | §5 "as before" -> present | wording |
| `sl-planner/beam` | EDIT | §5 drop the hash (and the 23-of-24 count measured with `p0`); drop experiment numbers in §3 (99.8 %, 14-16 %), §4 (9.5 s vs 13.8 s, the narrower-beam try), §5 (21 plans), §6 (6.2 plans, 241 s; 2.95); delete §6's dated panel table (an earlier net, 2026-10-30), keep its rules; planned §7 stays, its "460 nodes a second on the panel" goes | hash, experiments, old net |
| `sl-planner/budget` | REWRITE | §1 keep the effort table (B / E / L), drop the results column and the "macro brain of the time" paragraph; §2 the low budget as an option a run may take (its command), not "the data was played at"; name the two low budgets apart (`--effort low` vs the cheaper self-play command); §3 drop the old-V table and bullets (keep the restore share only if re-measured on the current build); §5 "The champion": its five reports only (delete the 2026-10-28 panel and the whole-run gate tables); links from combat-brain/index §3 and §5, gates §5 and pimc/budget §2 retargeted | experiments, old V, stale |
| `pimc/index` | EDIT | §1 drop the 57.7 % intent claim (the net has no intent output); §3 drop the date, "still approximates" -> "approximates", drop "five of the first 400 runs failed ... all three fixed"; §4 "will" -> "does"; planned §5 and the counterfactual test stay | stale claim, date, story |
| `pimc/search` | EDIT | §1 drop the dated probe; §3 "an earlier V" -> no name; §4 keep the ISMCTS argument, drop the "first try on 36 decisions" paragraph; planned §7 stays | date, experiment |
| `pimc/budget` | REWRITE | §2 drop the failed gate (−0.72 floors); §4 -> "every room gets 8 x 300", drop the dated gate; §5 one timing, not two; §6 "The champion": the NoSL champion is PIMC with the champion's combat net and L0 with its V, it has no NoSL report; delete the panel and whole-run history tables; §7 open questions and planned §8 stay | experiments, old nets, stale |
| `macro-brain/index` | KEEP | the Status aside's "where run worlds do not exist yet" -> "in NoSL it predicts each afterstate from the screen" | wording |
| `macro-brain/afterstates` | KEEP | — | clean |
| `macro-brain/search` | EDIT | §1 "Until run worlds exist" -> present; §6 one current cost number, not two; drop "17 runs ... an earlier combat net", say what `run --check` checks | old measurement, version |
| `macro-brain/network` | EDIT | §6 the champion's file (`v9-2.json`): its `fit` (21,272 runs, 5,608 won), μ / σ as facts, drop "every width has learned" and "kept from the first net of its line"; §7 the value sizes (no longer "mostly below 0.1"); §9 NetWalk from the champion's `tests[0]` and its "Six numbers" step without lineage | stale champion, lineage |
| `macro-brain/value` | EDIT | §2 run and win counts from the champion's training data (or none), "its time box ended" -> "its job was stopped" | stale, host |
| `macro-brain/selfplay` | REWRITE | §1 mermaid "gate 100 paired seeds per character"; §2 the current command (medium, `--asc N`, the champion's nets); §3 tiers 0.3 / 0.1 / 0.02, no per-generation list; §4 a generation is a job per character, tier and ascension, train seeds from their own block, never a test seed, no deadline, no size table; §5 the gate as in gates §10, no "v9-1 passed its gate" | generation history, hosts, stale |
| `macro-brain/next` | EDIT | §1 the stale "route encoder / max" limitation -> what network §8 says (the map bag holds no links, the map ends with the act); §2 the stale code-block citation (`harness §4`) and "fallback count is 0"; §2 and §3 stay (D1) | stale facts |

### Part III: learning and evaluation

| page | fate | what changes | why |
|---|---|---|---|
| `learning/index` | EDIT | §1 "on other machines" -> "in separate processes"; "a language model was to take part" -> planned; §4 the reference row matches its new titles; the champion sentence -> "the champion's two nets", no ledger | hosts, history, stale |
| `learning/combat-net` | EDIT | §3 the data paragraph -> the champion's data as a fact (all five characters, A0, medium, 21,272 runs; 430,142 held-out round starts), drop the c8 lineage, the `promotion-1-500` story and the `c7` set statistics; §4 the `--list` example as a pattern, one "about 1.3" loss, drop the c8-3 epoch story; §5 every number from the champion's `fit`; drop "against the net it went on from" and the v9-0 / v9-1 win comparison | lineage, stale numbers |
| `learning/loop` | EDIT | §1 "on machines separate" -> "in processes separate"; §2 a generation is played by the champion (link selfplay), no dataset names, no time boxes, no budget per dataset; §3 drop the floor proxy, the "design had a rule from r33hab" story (keep the weight-3 rationale, `learn/v/train.py:44, :144`), the GuanDan proxy paragraph; §4 TdTargets regenerated with the champion's V (chance to win, G = 1), no "an earlier V ... in progress"; §7 "the project's LLM gateway" -> "an LLM API"; §8 links gates §10 (D3); §9 tiers 0.3 / 0.1 / 0.02, drop the old test's numbers; §12 drop the `tmp/notes` pointer; planned sections stay | datasets, story, hosts, stale |
| `learning/reference` | REWRITE (§1, §3-§6), EDIT (§7-§9, §12) | §1 "Processes and outputs": `run --records --log` (N processes, `--workers`) -> `data/CHARACTER/ENV/JOB/` -> list pair -> `v.train` / `combat.train` (PyTorch, GPU when present) -> `weights/all/NAME.json` -> the brain (ONNX Runtime, CPU); process scaling (1,860 vs 275 combats/s) stays, per-host times, time boxes, deadline files, storage host, `.claude` pointers and `git archive` go; §3 the folder and manifest formats only (drop gauntlet / fork / g3 rows, `box` and fetch fields); §4 the list format, drop the table of old lists; §5 "Seeds": `--seed 1 --start K`, a block of 100,000 indices per dataset, the 40,134,134 bound, `common.train_lists`, no ledger, no `box.py`; §6 `weights/all/` holds v9-0..2 and c9-0..2, both champion files name v9-2 / c9-2, no "earlier files in git's history" and no hash; §7 NetFileAnatomy from the champion's files; §8 drop the hashed timing (re-measure with the champion's V or drop); §9 patterns and fixed citations; §12 open questions without the v8-3 A10 story | hosts, skills, lineage, hash |
| `evaluation/index` | EDIT | §1 drop "a report of the current champion is pending" (five exist); §3 drop the `tmp/notes` citation (cite spire-mentor itself) and the "nearest pair is one earlier V" sentence; §4 links gates §10 (D3); §5 "where the history and the reports live" -> "the champion's reports" | false line, tmp, story, stale |
| `evaluation/metrics` | EDIT | the worked example -> one of the champion's reports (recompute §1, §3 numbers, §8's loss counts, DeathsByFloor); §2 "a machine whose memory flips bits" -> "a crashed process or a machine fault"; §5 seed-set table's "used for" column current, drop `box.py`, the "line of c8-1 ... c9-1" and "rotate to promotion-2" history; §6 no ledger; §7 drop "`r1` in older names", fix the example name; §8 drop the missing-`end` history; §9 one board per character; §10 `abc1234` stays as "a placeholder commit", citation `Fixtures.cs:31` | old example, hosts, history |
| `evaluation/gates` | EDIT | §2 "three earlier nets" -> nets A-E (anonymous illustrations stay); §4 drop the re-baseline story; §6 delete the "Time boxes" paragraph (L153, `.claude` pointer; a gate has no deadline, each side plays the whole seed file), "a time-boxed challenger" -> "a challenger that played only the head of the set"; §7 drop "passed such a gate, on floors, after 33 pairs"; §9 delete the NoSL re-baseline story paragraph; §10 the procedure with its commands in the page (D3), replacing the `.claude` pointer (L212) and the stale 200-low / 100-medium sentence; no ledger | skills, hosts, stories, stale |
| `evaluation/champions` | EDIT, §2 REWRITE | intro "where the history ... lives" -> "the champion's reports"; §1 no ledger; §2 "Reports": `eval/CHARACTER/reports/` and the boards, the champion's five reports (table: wins of 100, mean floor, Wilson interval), how a promotion rewrites both files (`gate --promote`, or by hand for the wins gate), the NoSL file names none; delete the v9-0 -> v9-1 lineage, its table and "the record of what came before"; §3 "from published results", no `tmp/notes` pointers; §4 "has a champion" (not "again") | lineage, ledger, tmp |

### Part IV: the machinery

| page | fate | what changes | why |
|---|---|---|---|
| `simulator/index` | EDIT | Status aside and §3: no `run --brain SOCKET`; the brain is a process of its own that the mod starts and talks to on stdin/stdout; `run --check` plays over the wire in-process | stale |
| `simulator/headless` | EDIT | §4 SapphireSeed as an example of the risk, not as what "once did"; §5 drop the `tmp/notes/testmode-audit.md` pointer; §6 the JIT rationale without the WriteReplay incident; §8 "the one fixed so far" -> "the group holds one fix" | stories, tmp |
| `simulator/combat` | EDIT, §6-§7 REWRITE | §2 drop the date; §5 drop "failed one run in 165 of generation 1" and "failed one run in 452"; §6 the fidelity as current facts (one column, no "without the record -> with it", no dates, no "On 2026-10-27 every check passed": `selftest` fails on any wrong build), drop the CORPSE_SLUG story; §7 no dates, no "an earlier combat net", no commit, no "no longer compares ... 7 % faster", the profile re-measured or reduced to "restoring worlds is the largest share", drop "`perf` not installed" | hash, dates, stories, stale profile |
| `simulator/runs` | EDIT, §7 REWRITE | §4 SurfaceCounts from the champion's reports, "over the 100 runs of the champion's Silent report"; "seen in runs at 9999 HP" -> present; §6 the determinism guarantees as properties (`--check` fails a batch on any difference), drop the dated 200-seed batch; §7 `brain` serves the host that started it on stdin/stdout until stdin closes; only `run --check` uses an in-process abstract socket; drop `--socket`, `run --brain`, `Wire.cs:100-104`; §5 citation `RunCommand.cs:140` | stale, dates |
| `simulator/worlds` | EDIT | §1 "every world today" -> present; §2 drop the date; §6 the relic rule and the check as facts (drop the 148 / 120 replans story, "before the checkpoint carried ..." and the dates); §7 drop the date; planned §8 stays | story, dates |
| `simulator/pool` | EDIT | §6 "two gaps remain" -> "two gaps"; §7 drop "on the fleet's hosts" and the old 300-run numbers: the champion's reports' `ms_per_decision` / `seconds_per_run` without saying where they ran; drop the unverifiable PIMC 19.9 s | hosts, stale |
| `simulator/reference` | EDIT, §1 launcher REWRITE | intro "checked at the commit this page was written at" -> "where the page and the code disagree, the code is right"; §1 build and run from the repo (`dotnet build src/Sts2Ai.SimWorker -c Release -p:Sts2BinDir=DIR`, run the dll with `DOTNET_ReadyToRun=0`, the stamped `SourceRevisionId` and `git archive` for another commit), `tmp/gamebin/v0.111.0` named only as the default folder you create; drop `tmp/build.sh`, `tmp/builds`, `tmp/home`, the fleet and the `.claude` pointer; the tiering rationale without "seen after a few thousand combats"; §4 `run` without `--brain`, `brain` on stdin/stdout, `selftest` takes `--value` with `--combat-net`; §5 drop the dated selftest results, "up to 60 build checks"; §6 no `--brain`; §7 counts verified at the rewrite | tmp, hosts, skills, dates, stale |
| `harness/index` | EDIT | Status aside: over the wire only under `run --check`; §2 the mod starts `brain` and talks on its stdin/stdout, link idea 3 at `/#sec-1`; §5 "Listening" -> `Serve` over stdin/stdout, `Bind` only for `run --check`'s abstract socket, no logger, citations `BrainServer.cs` fixed; §6 no Logging.Abstractions | stale |
| `harness/routing` | KEEP (3 small edits) | §1 also refuses `l0` in SL without the clone; §5 planned sentence stays; §6 an I/O error ends the brain's `Serve` | stale facts |
| `harness/decision-log` | EDIT | §1 and §8 drop `--brain`; §3 `pimc` / `plan` with the champion's net name pattern, no `c9-1`; §8 GateTests 8, add ExplainTests; DecisionLog figure regenerated (chance to win, `first`, the champion's names, no "an earlier V and combat net played") | stale, version |
| `protocol/index` | KEEP | §1 mermaid "over the brain's stdin and stdout"; "another commit" -> "another build" | stale |
| `protocol/wire` | EDIT, §1 REWRITE | §1 the connection: the brain is the mod's child; JSON lines on stdin/stdout, stderr to `brain.log`; one host, until stdin closes; `run --check` binds an abstract socket in-process; §5 citation; §8 "the simulator (`run --check`)"; WireExchange: the champion's names, `combat prompts first`, recommendations outside combat carry `candidates` | stale |
| `protocol/observation` | KEEP | ObservationExplorer caption "a real start of a round from NoSL self-play"; §7 "not included yet" -> "not included" | version |
| `protocol/worlds` | KEEP | — | clean |
| `gamebridge/index` | KEEP | — (planned §5 stays) | clean |
| `gamebridge/observe` | KEEP | §6 drop "This happened headless with SapphireSeed." | story |
| `gamebridge/actions` | KEEP | — | clean |
| `mod/index` | EDIT, §3-§4 REWRITE | Status aside without the 89-of-90 story; §1-§2 the bundled brain on stdin/stdout, mermaid edge; §3 installing: `distro/build.sh` builds the item into `out/Sts2Ai/` (mod, manifest 0.2.0 and its text, `brain/RID/` with the champion and the nets), copied into `<game>/mods/`; the macOS launcher needs signing on a Mac (no address); "on this machine" -> "on Linux, by default"; §4 "The brain it ships": what starts it (`brain --champion champion.json --workers N`, working folder, `STS2_BIN_DIR`, `DOTNET_ReadyToRun=0`, `SENTRY_GODOT_LIB_PATH` removed, as a rule with its reason), restarted 2 s after it ends, its stderr in `brain.log`; drop the `tmp/builds` bullet; §5-§6 "beside the brain's log", panel messages from `BrainLink.Where` / `BundledBrain.Run`; §7 the code table (17 files, BundledBrain.cs, line counts) | stale design, story, tmp |
| `mod/loop` | EDIT | §1 the pipes, mermaid edge, `Host.cs` citations; §8 "The brain link": child process, writer thread, restart, settings and log in `$XDG_STATE_HOME/sts2ai`; drop the socket paragraph and the pressure-vessel rationale; the NoSL switch sentence follows the follow-up in section 9 | stale |
| `mod/sl-record` | EDIT | §3 drop "this settled the open question"; §6 the gap paragraph rewritten for the shipped SL champion (clone afterstates); §8 keep what the `clones` round trip checks and how a capture is made, delete the results (455 decisions, 89 of 90, the 2026-10-28 capture in `tmp/assist/`, the earlier capture, the timings with an earlier net) | experiments, date, tmp |
| `mod/auto` | EDIT | §2 "the user's own value" -> "the player's"; §3 "on Linux", the bundled brain's `brain.log`, drop the dated `tmp/assist/` capture; §4 the brain is a child process, nothing leaves the machine; §5 drop the SapphireSeed and tiering incidents (keep the rules); §6 "the mod opens no port"; §7 "built" rows only stated as built, "are checked against the decompiled game"; §8 delete the settled open question 1 (a list item; question 2 becomes 1, the in-page link follows) | stories, date, tmp, stale |
| `output` | EDIT | Status aside: drop the `tmp/notes` pointer; §2 "beside the brain's log", drop the "zero-UI variant was considered"; §3 drop the decompiled line citation and the `tmp/notes` pointer, the "order of work" line; §5 drop "the project's LLM gateway"; §6 the wire is the brain's stdin/stdout, the mod also writes `brain.log`; §7 drop "ran in the game in the session of 2026-10-28"; PanelMock and MapRoute regenerated (D7): no hash in the caption, `first`, the bundled brain's starting message | tmp, story, date, stale |

Totals: 1 page deleted (x3); 7 pages REWRITE in part; about 40 EDIT; 10 KEEP. Roughly 600 English lines touched, so
about 1,800 lines across the three locales, plus the figure data.

## 5. Figures and data (D7)

| file | used by | action |
|---|---|---|
| `data/book/example-run.json` | RunTimeline (game) | from the new champion play of `0SSL7NS01MU6` |
| `data/book/example-nodes.json` | NodeTimeline (environments/information) | same play |
| `data/macro-examples.json` | DecisionLog, TdTargets | same play; V's values as chances to win |
| `data/book/learning-td.json` | TdTargets (learning/loop §4) | the champion's V along the same run, G = 1 |
| `data/book/panel-examples.json` | PanelMock, MapRoute (output, sl-planner) | same play, one source for all four entries |
| `data/combat-examples.json` | PlanTimeline (`sl`), WorldSampler / WorldsTable (`nosl`) | `sl` from the same play's fight; `nosl` kept, `source` reduced; `panel` and `sl.stats` deleted with the tables they fed |
| `data/book/surface-counts.json` | SurfaceCounts (game, simulator/runs) | from the champion's reports (`runs[].surfaces`); "answered by" says `first` and the searches |
| `data/book/round-start.json` | ObservationExplorer, Determinize | kept; `source` without `weights/silent/c2.json` |
| `data/combat-walk.json` | walkthrough, OutcomeHead | `combat-walk.py` from the champion's combat net (read from `eval/champions/sl.json`) |
| `data/book/learning-netfiles.json` | NetFileAnatomy | from the champion's two files, keyed by role (`v`, `c`), names read from the data |
| `data/book/eval-reports.json` | DeathsByFloor | from the champion's report used as metrics' example |
| `data/book/eval-pairs.json` | PairedScatter, SprtReplay | anonymous nets A-E kept; `rev` and the dated file names go |
| components | — | NetWalk (champion's `tests[0]`), WireExchange, DecisionLog, PanelMock (`first`, no hash, starting message), SurfaceCounts (no hash), RestoreCost and Scaling (no dates), ObservationExplorer, Determinize, WilsonInterval captions (no "earlier nets"), OutcomeHead comment |

## 6. Order of work

Preconditions: the rewritten history is pushed (met: origin is the private repository, main at 8d2b9ed, this
checkout on it, per the repo session at 2026-11-07 ~22:50 Taiwan time); no other session edits `docs/` meanwhile. The
only thing left to wait for is the user's approval. Each step is one commit on main with all three locales (English written first, then zh-CN and zh-TW),
`pnpm --dir docs check` at 0 problems, and the local host scan clean.

1. **Front matter**: delete `status` (x3), `book` to one appendix, index (all of its row), glossary, `docs/README.md`
   (D8), `wrangler.jsonc` (D10).
2. **The brain link**: every socket / `run --brain` / Install / player-started-brain sentence -> the bundled brain on
   stdin/stdout (index, simulator index/runs/reference, harness index/routing/decision-log, protocol index/wire,
   mod index/loop/auto, output, glossary). One pass, because the same fact is on twelve pages.
3. **The champion and the procedures**: D2's naming everywhere; gates §10 with the commands (both `.claude` pointers
   gone); selfplay REWRITE; loop §2, §8, §9, evaluation/index §4, metrics §5 link them; champions §2 "Reports";
   sl-planner/budget §5 and pimc/budget §6 "The champion".
4. **Part I** history pass (game, environments).
5. **Part II** history pass (combat brain, SL planner, PIMC, macro brain), without the figure data.
6. **Part III** history pass (learning, evaluation), learning/reference's REWRITE included.
7. **Part IV** history pass (simulator, harness, protocol, GameBridge, mod, output).
8. **Figures**: the champion play of `0SSL7NS01MU6` (local, about 6 minutes), `docs/scripts/book-data.py`, every data
   file and component of section 5, `combat-walk.py` and `check.mjs` reading the champion, the walkthrough prose and
   macro-brain/network §9.
9. **Checks** (D9) in `check.mjs`, then the whole book once more against them; `docs/README.md` names the checks.
10. **Deploy**, only on the user's word: an export of HEAD, the host scan on it, `pnpm --dir docs run deploy`.

## 7. How it is checked

- `pnpm --dir docs check`: build, links, anchors, locale triplets, unrendered bold, the combat walk against the
  champion's net, and from step 9 the public lint and the code citations: `... 0 problems`, three pages fewer than
  today.
- The local host scan over `docs/` (and the deploy export): 0 hits for any host name, address, "hub", "fleet",
  "Colab", "storage host", "remote host".
- No section number changes (D1), so the anchor list of every surviving page before and after is the same; checked by
  diffing the built pages' `sec-N` ids against today's build.
- Every rewritten claim about code is read at HEAD before it is written; every line citation the rewrite touches is
  re-read.
- The regenerated figures are looked at in the dev server in all three locales (no console errors, the captions
  match the data).
- Twins: the same headings and the same code spans in all three locales (the check already holds the page triplets).

## 8. Who writes

This session, after approval and the preconditions, one step at a time; or a fresh session per part if the user
prefers. Either way the steps run one after another (they touch the same files).

## 9. Found outside `docs/` (not in this plan)

For the repo session or later work:
- `eval/ledger.md` (tracked) is history with host names, hashes, dates and quoted decisions; also
  `eval/silent/gauntlet-leaderboard.md` (a deleted feature). Whether they ship is the repo's call.
- The eval reports carry `config.commit` SHAs and the boards a commit column (data, not docs).
- `docs/wrangler.jsonc`'s `account_id` is in git history too.
- `distro/build.sh`'s header cites a `plans/` file and needs `tmp/gamebin`, `tmp/item`.
- `.gitignore` cites `docs: learning §6`, which does not exist (D9's citation check will flag it; it means
  learning/reference §3).
- Code comments still say `p0` (`Mod/Assist.cs:237`, `SimWorker/Simulation/NodeProbe.cs:51`,
  `SimWorker/Commands/RunCommand.cs:11`).
- The champion's five reports name "combat prompts p0" while `sl.json` says `first`: by gates §9 they need a
  re-baseline.
- The mod ships `eval/champions/sl.json` (L0 on the clone), which refuses NoSL hosts (`ChampionOptions.cs:41`): the
  panel's NoSL switch probably answers "Brain error"; macro decisions of a node with a broken record reach the Harness;
  the bundled brain starts without `--wire`, so no in-game capture can be made. Not run in the game.
- `learn/v/train.py`'s docstring says early stopping weighs the second head; the code stops on the hazard loss.
