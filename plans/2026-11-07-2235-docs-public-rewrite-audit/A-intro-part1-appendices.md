# Audit A: introduction, Part I, appendices (English reference)

Pages: `en/index.mdx`, `en/arch/game.mdx`, `en/arch/environments/{index,information,worlds}.mdx`, `en/glossary.mdx`,
`en/status.mdx`. All read in full; the six figure components and their four data files were checked; twins compared by
grep (hashes, dates, version names, host words, `.claude`, `tmp/`) and by diffing every code span and every link.

Facts checked against the repo (HEAD 15a35eb plus working tree): `eval/champions/sl.json` and `nosl.json` name
`weights/all/v9-2.json` + `weights/all/c9-2.json`. The SL file names five 2026-11-06 reports (A0, `promotion-1-100`, medium). The
NoSL file has `"reports": {}`. The current reports give: Silent 92/100 won, floor 46.45 (Wilson 85.0–95.9 %); Ironclad 82, 46.00;
Defect 68, 46.44; Regent 71, 45.57; Necrobinder 47, 41.55. Silent averages 830 decisions a run, 73 % of them combat.

---

### en/index.mdx  (lines 153)  -- FATE: EDIT
Reason: a sound overview with a stale results paragraph, two history/plan sentences, a stale transport label, two untracked
`tmp/` pointers, Status links, and a "Maintaining it" block that duplicates `docs/README.md`.
Sections:
  - intro (L7-16, no anchor) -- edit (L10 future, L12 history/plan, L14 stale results + version + history)
  - §1 `#sec-1` Five ideas -- keep
  - §2 `#sec-2` Seven rules -- keep (L24 / L36: see cross-page 9 on what voids a report)
  - §3 `#sec-3` Processes and data -- edit (mermaid L55 transport is stale)
  - §4 `#sec-4` The code -- edit (L88 host; `pool/` and `distro/` not listed, see cross-page 8)
  - §5 `#sec-5` This book -- edit (L92, L108, L126, L128, L135)
  - §6 `#sec-6` About this book -- edit: keep "Reading the pages" without the two `tmp/` pointers, and drop "Maintaining it" (L149-153)
Findings (line, category, short quote, proposed action):
  - L10 future "Playing whole runs with no one at the keyboard is planned" -> move to the future list, or drop the sentence
  - L12 history/plan "The first target is the Silent ... Ascension 0, then Ascension 10." -> "Single-player only. One set of
    nets plays all five characters at every Ascension." (`status.mdx` L11 says the same, and the nets are `weights/all/`)
  - L14 story/stale "wins **74%** ... 46%, 35%, 31% and 9%" -> these are v9-1 + c9-1's numbers. The champion files' reports
    (v9-2 + c9-2) give 92 % (92 of 100, 95 % interval 85.0–95.9 %), Ironclad 82 %, Regent 71 %, Defect 68 %, Necrobinder 47 %.
    Update the numbers and say they come from the champion file's reports
  - L14 version/history "the nets it went on from won 8.5% (17 of 200 seeds)" -> drop (earlier nets at A10)
  - L14 history/version "best report reached floor 26.2 ... with an older value function" -> "NoSL: the champion file names no
    report" (matches `nosl.json` and `evaluation/champions.mdx:50`)
  - L55 stale fact `"JSON lines<br/>Unix socket"` (game <-> brain) -> the mod starts the bundled brain as a child process and talks
    to it on its stdin/stdout (`src/Sts2Ai.Mod/BundledBrain.cs:7-11`, `BrainLink.cs:17-22`). Only `run --brain` uses a Unix
    socket (`SimWorker/Brains/RunBrains.cs:184-193`). Relabel the edge "JSON lines<br/>stdin/stdout"
  - L88 host "kept on the project's storage host, not in git" -> "`data/`: where self-play and evaluation output goes; not in git"
  - L92 "four parts and two appendices" -> "four parts and an appendix" once status.mdx is deleted
  - L108 future "self-play, next steps" (the `macro-brain/next` page) -> follows the decision on that page
  - L126 future "the planned auto loop and narration" -> future list
  - L128 link "[Status and roadmap](/en/status/) and the [Glossary]" -> "**Appendix**: the [Glossary](/en/glossary/)."
  - L135 link "[Evaluation] and [Status](/en/status/)" -> "[Evaluation](/en/arch/evaluation/) and [Evaluation: champions and
    baselines](/en/arch/evaluation/champions/)"
  - L144 untracked "The decompiled code stays local (`tmp/decompiled/sts2/`)" -> "The decompiled game is not in the repository."
  - L147 untracked "Background notes, in Chinese, are kept locally under `tmp/notes/`" -> drop the bullet
  - L149-153 "Maintaining it" -> drop it from the book; `docs/README.md` already states every rule in it.
    - "Claude reads them as its design manual" describes the development tooling, not the system. It belongs in CLAUDE.md or
      the README, not the public book.
    - "Change all three in the same commit" is a maintainer rule. It is fine in a public repo only in README or CONTRIBUTING,
      with the maintainers syncing translations; an outside contributor cannot be required to write zh-CN and zh-TW.
    - "Keep section numbers stable: code comments cite them" is useful to readers too. Fold it into the L146 bullet, which already
      explains `#sec-N` and code comments.
What must change in index.mdx and around it if status.mdx is deleted:
  - index L92, L128 and L135 in all three locales (zh-CN `index.mdx:128,135`, zh-tw `index.mdx:128,135`);
  - `docs/astro.config.mjs:61` `items: ['status', 'glossary']` -> `['glossary']`, label "Appendices"/附录/附錄 -> singular;
  - delete `status.mdx` in all three locales (`pnpm check` checks locale triplets);
  - optionally `docs/README.md` "an overview, four parts and the appendices".
  - The doc map in §5 matches `book` in astro.config.mjs today (Parts I–IV in the same order; the combat brain row does not name its
    walkthrough page, which is acceptable).
Inbound links to deleted/renumbered sections: none. No section is deleted or renumbered. For reference: §2 is linked from
  `arch/environments/information.mdx:123`, `protocol/index.mdx:13`, `simulator/headless.mdx`, `simulator/index.mdx:26`, and §3
  from `harness/index.mdx` and `simulator/index.mdx:41`.
Figures/data: no Svelte figures (only Badge and LinkCard). The mermaid diagram (L42-59): the "Unix socket" label is stale (above).
Twins: match. zh-CN and zh-TW carry the same `tmp/` lines (L144, L147), storage host (L88), the "Claude" sentence (L151) and
"Unix socket" (L55).
Future/next content: L10 unattended whole runs; L108 "next steps"; L126 "planned auto loop and narration".
Rough size of the rewrite: ~8 lines rewritten, ~6 removed (one paragraph trimmed, the "Maintaining it" block).

---

### en/arch/game.mdx  (lines 190)  -- FATE: EDIT
Reason: the game reference is present-tense facts. What remains: one untracked path, "trains first" and "so far" history
wording, and numbers and figures taken from an earlier champion's 2026-10-30 report that are presented as "the champion".
Sections:
  - intro (L12-14) -- edit L14
  - §1 `#sec-1` A run -- edit L27 (and RunTimeline data)
  - §2 `#sec-2` The map -- keep
  - §3 `#sec-3` Rooms -- keep
  - §4 `#sec-4` Combat -- keep (edit the ObservationExplorer caption)
  - §5 `#sec-5` Characters -- edit L126
  - §6 `#sec-6` Ascension -- edit L130
  - §7 `#sec-7` Randomness, and what the player sees -- keep
  - §8 `#sec-8` Why it is hard for an AI -- edit L181, L186, L190 (and SurfaceCounts)
Findings:
  - L14 untracked "relative to the decompiled tree `tmp/decompiled/sts2` (local)" -> "relative to the decompiled game (not in
    the repository)"
  - L27 version/stale "won on floor 48 by the SL champion" -> the run was played by an earlier champion. `example-run.json` source:
    "combat plan c2 ... macro l0 v6-3-2", report 2026-10-30. Rewrite as "an SL run of the Silent on seed `0SSL7NS01MU6`, won on
    floor 48", or regenerate the example with the current champion. The macro-brain chapter follows the same run (other group).
  - L126 history "The Silent is the character this project trains first." -> "The Silent is the character of the book's example
    run." (one set of nets serves all five)
  - L130 history/stale "This project plays at level 0 so far." -> "The champion's reports are at level 0; a run can take any level
    (`run --asc N`)." (`RunCommand.cs:22`; self-play covers A0–A10)
  - L181 stale/version "about 555 decisions on average (166,527 decisions in 300 SL runs of the champion)" -> these are the c2 +
    v6-3-2 report on `promotion-1-300`. The current champion's Silent report averages 830 a run (83,026 in 100 runs); all five
    characters average 755. Recompute from the champion file's reports, or drop "of the champion"
  - L186 stale "in the 300 runs of the SL champion's report" -> same source; regenerate (see SurfaceCounts)
  - L190 stale number "Combat is 71% of all decisions" -> current Silent report 73 % (characters 71–77 %); take it from the
    regenerated data
Inbound links to deleted/renumbered sections: none. The glossary links §1–§7, and a code comment cites `docs: game §1`.
Figures/data:
  - RunTimeline.svelte -> the visible caption is clean. `data/book/example-run.json` holds `source.rev: "1fc5f50"` (hash),
    `source.champion` naming c2 / v6-3-2 / p0 (version) and a 2026-10-30 report path. These are not rendered; strip `rev` and
    `champion` (hash rule), or regenerate.
  - ObservationExplorer.svelte -> caption L117-119 "one of the test states an earlier combat net's file carried" (version/history)
    -> "A real start of a round from NoSL self-play, as Protocol serializes it"; the L2-3 comment says the same.
    `data/book/round-start.json` `source.file: "weights/silent/c2.json"` names an old net and a file no longer in git
    (`weights/` holds only `all/`).
  - SurfaceCounts.svelte -> the caption L67-69 renders "an earlier V's SL report on promotion-1-300 ... simulator at
    `${data.source.commit}`" = **1fc5f50**, a hash shown on the page (hash + version/history).
    - The "answered by" cell L26 "p0 in combat, macro brain outside" is stale. p0 is gone: combat prompts go to route `first` and
      to search branches (`SimWorker/Brains/ChampionOptions.cs:28,49`; the champion name says "combat prompts first").
    - The L2-3 comment says "an earlier V's".
    - `data/book/surface-counts.json` source `commit`, `report` and `champion` name c2, v6-3-2 and p0.
    - Regenerate from the champion file's reports (`runs[].surfaces` exists in them), drop the commit, and fix "answered by".
Twins: match. The L14 `tmp/` is in all three; no extra hashes, dates or versions.
Future/next content: none.
Rough size of the rewrite: ~6 lines rewritten in the page; three components' captions and four data files to edit or regenerate.

---

### en/arch/environments/index.mdx  (lines 101)  -- FATE: EDIT
Reason: present-tense design, apart from a status Aside with "planned" items, a "was considered and rejected" decision story,
two "planned/yet" clauses and three stale code-line citations.
Sections:
  - Aside "Status" (L16-20, no anchor) -- edit: keep "Decided, and in force ..." and "four of the ten guards have tests"; move "the
    counterfactual tests and the reload count are planned" to the future list
  - §1 `#sec-1` The problem -- keep
  - §2 `#sec-2` The two environments in one minute -- keep
  - §3 `#sec-3` Why two environments -- edit (L58, L60, L62)
  - §4 `#sec-4` What both environments share -- edit (L69)
  - §5 `#sec-5` How a run picks its environment -- keep, but fix the citations (L88, L90) and move L89
  - §6 `#sec-6` This chapter -- keep
Findings:
  - L18 future/"today" "four have tests today ... the counterfactual tests and the reload count are planned" -> "four have tests";
    planned part to the future list
  - L58 plan "SL is a stepping stone, and a measure." -> "SL is the training environment, and a measure." (the rest is design
    rationale)
  - L60 "today's two champions differ in their combat brains, so it shows none yet" -> "the two champions differ in their combat
    brains, so it shows none"
  - L62 history "A whole-run horizon was rejected ... was considered and rejected ... It is not to be built." -> "There is no
    whole-run environment: knowing the whole order of a run would make the run itself a deterministic puzzle and teach nothing
    that carries over to fair play." (`information.mdx` §2's Aside repeats it; keep one copy)
  - L69 future "a check that is planned for the mod's logs" -> "nothing in the mod logs it" + future list
  - L88 stale cite `Commands/RunCommand.cs:66` -> `:61` (the line `plan plays in SL only`)
  - L89 misplaced: "SL and NoSL play the same combat net file" sits under "What refuses the wrong environment" but is not a
    refusal -> move to §4
  - L90 stale cites:
    - `Harness/Dispatcher.cs:173`, `:174` -> `:173`, `:181` (node_action is refused at 181);
    - `Harness/BrainServer.cs:112-123` -> the file has 116 lines, and the refusal path is L39-80.
Inbound links to deleted/renumbered sections: none. For reference: §3 from `worlds.mdx:114`; §5 from `information.mdx:125`,
  `worlds.mdx:76`, `harness/index.mdx`; code comment `docs: environments §2`.
Figures/data: none (Aside only).
Twins: match.
Future/next content: L18 counterfactual tests and the reload count; L69 `NumReloads` in the mod's logs.
Rough size of the rewrite: ~6 lines rewritten, 0 removed (the citation fixes come on top).

---

### en/arch/environments/information.mdx  (lines 161)  -- FATE: EDIT
Reason: the rules, the node and the clone are present-tense design. The page also holds a "Rejected" Aside, an untracked note
pointer, a bug-fix story in §7, many "Planned" guards, and an all-future §9.
Sections:
  - intro -- keep
  - §1 `#sec-1` NoSL: the public history -- keep
  - §2 `#sec-2` SL: save & load inside the node -- edit (drop the "Rejected" Aside L44-48, which duplicates overview §3)
  - §3 `#sec-3` The node -- keep (L67 "No search does this today." -> "No search does this.")
  - §4 `#sec-4` Why the node, and not the game's own saves -- keep. L79 is rationale ("chosen anyway, for two reasons"; could read
    "is the boundary because"). Its "open question 1 (§9)" link follows §9's fate.
  - §5 `#sec-5` The information table -- edit L101
  - §6 `#sec-6` Grey zones -- edit L107
  - §7 `#sec-7` Where the SL clone comes from -- edit L123
  - §8 `#sec-8` Guards -- edit: keep the guards as design; existing tests stay, "Planned" items go to the future list (numbered
    list items, not headings, so no anchors change)
  - §9 `#sec-9` Open questions -- delete (all future; move to the future list)
Findings:
  - L44-48 history Aside "considered and rejected. Do not build it." -> drop; the overview states it (rewritten above)
  - L67 "today" "No search does this today." -> "No search does this."
  - L101 future "Today NoSL's worlds draw ...; a full posterior is planned" -> keep the present half; the posterior goes to the
    future list
  - L107 untracked "the fair-play spec, a local note (`tmp/notes/fair-play-spec.md`)" -> "Rulings for cases the table does not
    settle, which the observation whitelist implements:"
  - L123 history "The relic fields mattered: ... no longer matched ... planned again at every step ... `run --check` now finds no
    node" -> "So the checkpoint carries each relic's plain fields (numbers, flags and enums) and puts them back after loading, and
    `run --check` compares every node's clone with its run ([Simulator: worlds and the SL clone §6])." Keep the rule-7 sentence.
  - L131 future "has, or will have, a test ... 'Exists' names where it runs today" -> "Each rule has a guard; 'Exists' names its
    test."
  - L138 guard 4 "Planned ... nothing reads it today" -> future list
  - L139 guard 5 "Planned" -> future list. The glossary L62 says the opposite ("each field has a test"); see cross-page.
  - L143 guard 6 "Planned" -> future list
  - L149-152 guard 9 "Planned" -> future list. The verified list of perturbable state is useful design analysis; keep it as
    rationale or move it with the guard.
  - L153 guard 10 "Planned. Today `brain --wire FILE` logs ..." -> keep the present half; the planned part goes to the future list
  - L159-161 §9 future (three open questions) -> delete the section; list them under future
Inbound links to deleted/renumbered sections:
  - §9 `#sec-9` is linked from `arch/environments/information.mdx:79` (own page), `arch/macro-brain/afterstates.mdx:54`
    ("open question 1") and `arch/sl-planner/budget.mdx:86` ("Equal wall-clock time is the fairer budget"). All three must be
    reworded. No code comment cites §9.
  - §8 keeps its number. It is linked from `index.mdx:32`, `environments/index.mdx:18,69`, `worlds.mdx:72`,
    `evaluation/metrics.mdx:40`, `harness/decision-log.mdx:125` and `mod/auto.mdx:92`.
Figures/data: NodeTimeline.svelte -> the visible text is clean (caption: "The won SL run of seed 0SSL7NS01MU6"; no champion
  claim). `data/book/example-nodes.json` (not rendered) holds `source.rev: "1fc5f50"` (hash), `source.champion` naming c2,
  v6-3-2 and p0, and `source.job: data/silent/sl/eval/2026-10-30-eval-v6-3-2-clone` (date + version) -> strip them.
Twins: match. The L107 `tmp/` is in all three.
Future/next content: guards 4, 5, 6, 9, 10; the full move posterior (L101); §9's open questions (post-combat rewards rule; equal
wall-clock budget for SL; real reloads to verify clones).
Rough size of the rewrite: ~10 lines rewritten, ~10 removed (§9, the Aside).

---

### en/arch/environments/worlds.mdx  (lines 129)  -- FATE: EDIT
Reason: present-tense algorithms page. It names superseded nets (c9-1, v9-1), carries a false claim about the NoSL file, an
"earlier combat net" figure source, and four future clauses.
Sections:
  - intro -- keep
  - §1 `#sec-1` Worlds -- edit L16
  - §2 `#sec-2` Determinization -- edit L41, L43
  - §3 `#sec-3` Strategy fusion -- edit L62
  - §4 `#sec-4` SL: one exact world -- keep (L72 trim "today")
  - §5 `#sec-5` One macro brain, two combat algorithms -- keep
  - §6 `#sec-6` One V for both environments -- edit L109
  - §7 `#sec-7` Leaderboards, gates and explanations -- edit L113, L115
  - §8 `#sec-8` What each subsystem does per environment -- keep
Findings:
  - L16 future "run worlds for NoSL ... are planned" -> future list
  - L41 "Not modelled yet:" -> "Not modelled:" (a present gap)
  - L43 version/history "from the training data of an earlier combat net" -> "a real start of a round from NoSL self-play"
  - L62 future "If the remaining error turns out to matter, the alternative is ISMCTS" -> future list, or a short design note
    without "turns out"
  - L72 "Nothing searches past the exit today" -> "Nothing searches past the exit"
  - L109 version/stale "share one net file, c9-1, ... never been trained or gated there" -> "share one net file, the champion's
    combat net, which learned from SL's games; in NoSL it loads and plays, never trained or gated there". Name `c9-2` if a name is
    wanted.
  - L113 stale/false "Both files name the same V, v9-1, and the same combat net, c9-1. Until a NoSL report of them exists, the
    NoSL file names the SL reports" -> "Both files name the same V and the same combat net. The NoSL file names no report, so a
    NoSL gate refuses every character." (`eval/champions/nosl.json` has `"reports": {}`, and `evaluation/champions.mdx:50` already
    says so.)
  - L115 future "Explanations will differ ... has no facts yet; when it does" -> future list
Inbound links to deleted/renumbered sections: none. For reference: §2, §3, §5, §6 and §7 are linked from 15 pages, and code comments
  cite `environments/worlds §2, §3`.
Figures/data:
  - Determinize.svelte -> caption L120-122 "one of the held-out round starts in an earlier combat net's file" (version/history) ->
    drop the clause; the L2 comment says the same. Its data `round-start.json` points at `weights/silent/c2.json` (see game.mdx).
  - StrategyFusion.svelte -> clean (illustrative values).
Twins: match. All three name c9-1 twice and v9-1 once.
Future/next content: L16 NoSL run worlds; L41 known top card and history-weighted moves (gaps); L62 ISMCTS; L115 explanation
facts.
Rough size of the rewrite: ~8 lines rewritten.

---

### en/glossary.mdx  (lines 131)  -- FATE: EDIT
Reason: the definitions are present-tense. §7 names superseded nets, a host, an untracked script and the history ledger; a few
entries are "Planned".
Sections:
  - §1 `#sec-1` The game -- edit L23
  - §2 `#sec-2` Project components -- keep
  - §3 `#sec-3` Environments and information -- edit L62
  - §4 `#sec-4` Decisions and search -- edit L76, L81, L82
  - §5 `#sec-5` Learning -- keep (L98-L100 depend on `learning/loop` §5, §6, §12)
  - §6 `#sec-6` Evaluation -- edit L108
  - §7 `#sec-7` Names -- edit L120-128
Entries naming history, old nets/generations, hosts, the hub, local `tmp/` scripts or `.claude` (the requested list):
  - L23 history "Silent | The character this project trains first" -> "One of the five characters; the book's example run is hers"
  - L120 version/stale "v9-1 | V, the one value net ... played by both champions" -> v9-2, or "the champion's V
    (`weights/all/v9-2.json`)"
  - L121 version/stale "c9-1 | The combat net ..." -> c9-2
  - L123 stale "the champions play `v9-1.json` and `c9-1.json`" -> `v9-2.json` and `c9-2.json`
  - L126 history "`eval/ledger.md` | The lineage, and the only history of the nets" -> the ledger is pure history (hashes, host
    names, dates and "the user" on nearly every line, e.g. `eval/ledger.md:10`, `:55`). Keep this row only if the ledger ships
    public; otherwise drop it.
  - L127 host "`data/silent/{sl,nosl}/…` | ... on the storage host, not in git" -> "`data/CHARACTER/{sl,nosl}/…` | where
    self-play and evaluation output goes; not in git" (the folders are per character now)
  - L128 untracked "`tmp/build.sh REV`, `sim.sh` | The local launcher ..." -> delete the row (`tmp/` is gitignored)
  - The hub, the fleet and `.claude` are not named in any entry.
Other findings:
  - L62 contradiction "whitelist ... each field has a test" -> information §8 guard 5 says it is planned. Use "...; JSON round trips
    check every observation (`run --check`, `selftest`)".
  - L76 incomplete "a decision by `random` voids an evaluation" -> `play` voids one too (`Harness/Reports.cs:39-41`); "a decision
    by a placeholder voids an evaluation"
  - L81 future "TurnEnds | Planned" (links `combat-brain` §3.1) -> keep or drop with the decision on that section
  - L82 future "iterative widening | Planned" (links `sl-planner/beam` §7) -> same
  - L99 "SPSA | ... the project does not use it" -> it exists only because `learning/loop` §6 describes an unused method; drop it
    if that section goes
  - L108 "`eval/silent/leaderboard.md`" -> "`eval/CHARACTER/leaderboard.md`"
  - L125 "and its report of each character" -> the NoSL file names none; "and the reports behind it"
Inbound links: page links only (`index.mdx:128`, `environments/index.mdx:101`); no `#sec-N` link to the glossary. No section is
deleted.
Figures/data: none.
Twins: match. The zh twins carry the same `tmp/build.sh` row, storage-host row and v9-1/c9-1 names.
Future/next content: L81 TurnEnds, L82 iterative widening; L98 (ExIt) and L100 (privileged information) define planned methods.
Rough size of the rewrite: ~9 rows edited, 1 row removed (2 more if the ledger and SPSA go).

---

### en/status.mdx  (lines 87)  -- FATE: DELETE
Reason: it is a dated progress report ("As of 2026-11-04"), and its headline numbers are already stale. §4 is a changelog of old
nets and datasets. Every present-tense fact left in it already lives in a chapter.
Decision, DELETE rather than a short "current state" page. A current-state page would duplicate:
  - the index's "How well it plays today" paragraph;
  - Evaluation §4 "Where it stands" (a built/planned table);
  - the chapters' status Asides and the badges in index §5.
It would also go stale at every promotion, exactly as this one has. §1 still shows v9-1 + c9-1 at 74 %, while the champion files
name v9-2 + c9-2 at 92 %.
Where the remaining content goes:
  - The results sentence is already in index.mdx (update it as above), and the champion table belongs in
    `evaluation/champions`.
  - Every §3 gap is already stated in the chapter it links to. Spot-checked: `simulator/runs` (UnlockState),
    `simulator/headless` (TestMode guard), `harness/index` (uniform move draws / posterior), `mod/auto` (tiered JIT),
    `combat-brain/shared` (FirstPicks for prompts no search weighs), `learning/loop` §9 (labels carry later draws),
    `mod/sl-record` (previous node's clone).
  - If a one-glance list of limits is wanted, add it to Evaluation §4, not a new page.
Sections:
  - §1 `#sec-1` In one paragraph -- delete (stale v9-1/c9-1 numbers; `v9g0` and "learned first from the Silent's runs at
    ascension 10" history)
  - §2 `#sec-2` Each part -- delete (duplicates the chapters; L26 future "combat redesign"; L31 host "compute" row)
  - §3 `#sec-3` Main gaps and risks -- delete (each item is in its chapter; L52 item 7 is partly stale; L54 item 9 is v9g0 history,
    stale after v9g1)
  - §4 `#sec-4` What comes next -- delete (L61-81 dated history; L83-85 future)
Findings (by category):
  - L7 date "As of 2026-11-04."
  - L11 version/stale "c9-1, and one V, v9-1 ... then from `v9g0` ... 74 % ... 9 %"
  - L25 story "on a fixed panel of 120 combats it won every combat PIMC won and 9 more"
  - L26 future "<Badge planned> SL = pruning + net, NoSL = Monte Carlo + net, on one `TurnEnds`"
  - L28 future "Planned: TD(λ), expert iteration, LLM patch proposals"
  - L30 history "89 of 90 SL decisions ... (the other one's cause is fixed)"; future "the auto-run driver planned"
  - L31 host "time-boxed jobs on several remote hosts ... a storage host ... a GPU machine"
  - L33-42 stale table "plan c9-1 ... L0 v9-1": 74/46/31/35/9 won, floors 45.40/41.39/42.81/42.30/35.94. The current reports give
    92/82/68/71/47 and 46.45/46.00/46.44/45.57/41.55.
  - L52 stale "a later prompt takes its first options" -> since the prompt rework, `FirstPicks` answers only prompts no search
    weighs (`combat-brain/shared.mdx:20`)
  - L54 version/story "`v9g0`, in which they won 76 runs of about 24,000"
  - L61-81 date/version/story: 11 dated "done" steps naming v7g2, v8-1, c8-1, v8-2, c8-2, v8g2, v8-3, c8-3, c9-0, v9-0, v9g0, v9-1
    and c9-1, plus gate stories ("c8-2 passed by floors; v8-2 failed by floors and became champion for its wins")
  - L83-85 future: NoSL tuning, measuring strategy fusion, the auto-run driver, the R4 pipeline, R3 ≡ R4
Inbound links (grep `/en/status` over all English pages): `index.mdx:128` "[Status and roadmap](/en/status/)" and `index.mdx:135`
  "[Status](/en/status/)". No English page links a `status/#sec-N` anchor, and no code comment cites status.
  Elsewhere: zh-CN `index.mdx:128,135`, zh-tw `index.mdx:128,135`, and `docs/astro.config.mjs:61` (`items: ['status', 'glossary']`).
Figures/data: none (Badge and Steps only).
Twins: match. The same dates, versions and stale table appear in all three; only the "storage host" string differs, by translation.
Future/next content: L26 combat redesign on `TurnEnds`; L28 TD(λ), expert iteration, LLM patches; L30 auto-run driver; L83-85
NoSL tuning, strategy-fusion measurement, the R4 pipeline, R3 ≡ R4.
Rough size of the rewrite: 87 lines removed in each of three locales; 3 lines in each index; 1 line in astro.config.mjs.

---

## Cross-page issues

1. **The champion's nets are stale across this group.** `index.mdx` L14, `worlds.mdx` L109/L113, `glossary.mdx` L120-125 and
   `status.mdx` name v9-1 / c9-1 and their numbers. `eval/champions/*.json` name v9-2 / c9-2, and `evaluation/index.mdx:82` and
   `evaluation/champions.mdx:50` already say so. Suggestion: name the current nets only in `evaluation/champions` and glossary §7.
   Elsewhere write "the champion's V / combat net", so the next promotion does not stale five pages.
2. **The mod's link to the brain is not a socket.** The mod starts the bundled brain on stdin/stdout (`Mod/BundledBrain.cs`,
   `Mod/BrainLink.cs`). The index's §3 mermaid (L55), `mod/index.mdx:26` and `harness/index.mdx:69` say the mod uses a Unix
   socket; only `run --brain SOCKET` does.
3. **The example run and four figure data files come from an earlier champion.** `example-run.json`, `example-nodes.json` and
   `surface-counts.json` come from c2 + v6-3-2 at `1fc5f50` (the 2026-10-30 report). `round-start.json` comes from
   `weights/silent/c2.json`, which no longer exists.
   - `game.mdx` L27, L181 and L186 call this "the (SL) champion", and SurfaceCounts renders the hash.
   - The macro-brain chapter (`macro-examples.json`, other group) follows the same run.
   - Either regenerate from the current champion's reports (they carry `runs[].surfaces`), or describe it as "an SL run" and strip
     rev/champion/job from the JSON.
4. **p0 is gone, but text still names it.** Combat prompts take route `first` (`SimWorker/Brains/ChampionOptions.cs:49`). Still
   naming p0: SurfaceCounts' "answered by" (`SurfaceCounts.svelte:26`), and code comments `Mod/Assist.cs:237`,
   `SimWorker/Simulation/NodeProbe.cs:51` and `SimWorker/Commands/RunCommand.cs:11` ("Combat prompts go to p0").
5. **Stale line citations** in `environments/index.mdx` §5:
   - `RunCommand.cs:66` -> 61;
   - `Dispatcher.cs:174` -> 181;
   - `BrainServer.cs:112-123` -> 39-80 (the file has 116 lines).
   The citations spot-checked in `information.mdx` and `worlds.mdx` still hold (RunSession.cs:228, NodeProbe.cs:29,
   Dispatcher.cs:116, HiddenState.cs:19/36, CombatSession.cs:230, Gate.cs:35-41, Assist.cs:275, RunSession.cs:137-142).
6. **`docs/README.md`** already holds the editing rules, so index §6 "Maintaining it" duplicates it. The README itself says
   `pnpm check` values `weights/silent/v8-3.json`, a file that is gone (stale version name and path).
7. **`eval/ledger.md`** is referred to as "the only history of the nets" (glossary L126; also in the index §4 `eval/` bullet,
   implicitly). It breaks every rule of the rewrite: hashes, host names (pushqkzh), dates, "the user", `plans/...` paths and
   Colab. Decide whether it ships in the public repo, is rewritten, or is dropped; the docs pointers follow that call.
8. **Two tracked top-level folders are missing from index §4:**
   - `pool/` (box.py, hub.sh, deal.py, dist*.py, on.sh, worker.py; staged in the working tree as a move from
     `.claude/skills/fleet/`) is the hub's work pool, category-8 machinery. Decide whether it goes public at all; if it does,
     the book still should not describe it.
   - `distro/build.sh` (mod packaging) is also tracked and unlisted.
9. **What voids a report.** Index L24/L36 and glossary L76 say a *random* decision voids an evaluation. `Harness/Reports.cs:39-41`
   counts every placeholder (`random` and `play`), and `information.mdx` L137 says it correctly.
10. **Contradiction about whitelist tests.** Glossary L62 says every observation field has a whitelist test; `information.mdx`
    guard 5 (L139) says it is planned.
11. **Overlaps to merge:**
    - The whole-run horizon rejection appears twice (`environments/index.mdx` L62 and the `information.mdx` L44-48 Aside); keep
      one.
    - Status is tracked in four places: the index §5 badges, the `environments/index` "Status" Aside, `status.mdx` §2 and
      `evaluation/index.mdx` §4. Deleting status.mdx removes one; consider keeping only the badges plus each chapter's own Aside.
12. **spire-mentor's 22.8–25.5 pp.** It is stated in `environments/index.mdx` L60 without a source. `evaluation/index.mdx:84`
    cites it from `tmp/notes/refs/strong-bots.md:42`, which is untracked (other group). Cite the public project instead, or
    neither.
