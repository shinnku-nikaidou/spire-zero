# F1 audit: harness, protocol, output (English reference pages)

Read-only. Pages read in full; figures and data read in full. Code checked at HEAD (15a35eb) where a page states a fact.

**Headline finding (not in the 12 categories, but it breaks several pages):** commit 6e903b2 (after these pages were
last edited) removed the socket. Today the mod starts `brain` as a child process (`Sts2Ai.Mod/BundledBrain.cs`,
`Host.cs`) and the wire runs over the child's stdin/stdout; `brain` serves only that host and ends when stdin closes
(`SimWorker/Commands/BrainCommand.cs`); `run --brain`, `--socket`, `BrainServer.Listen`, `BrainSocket` are gone;
`BrainServer.Bind` only makes an abstract socket for `run --check`'s in-process brain. Every "Unix socket /
brain.sock / run --brain / Listen" sentence below is stale and should be rewritten to the stdin/stdout fact while the
pages are rewritten anyway.

---

### en/arch/harness/index.mdx  (lines 194)  -- FATE: EDIT
Reason: current design, clean of hashes/hosts/dates; stale socket facts and two stale code facts; planned parts go to the future list.
Sections:
  - Status aside -- edit (L17 "in process or over the wire"; Planned list -> future)
  - §1 `#sec-1` What Harness does, and what it does not -- keep
  - §2 `#sec-2` One decision, step by step -- edit (L69 socket paragraph)
  - §3 `#sec-3` What Harness keeps for a run -- keep
  - §4 `#sec-4` The public record -- keep built parts; "Planned: the rest of the record" (L102-121) -> future decision
  - §5 `#sec-5` The brain server -- rewrite "Listening" (L127) and the constructor sentence (L125); fix citations
  - §6 `#sec-6` The code -- edit (L148)
  - §7 `#sec-7` This chapter -- keep table; open questions (L191-194) -> future decision
Findings:
  - L17 stale "SimWorker's `run` plays every run through it, in process or over the wire" -> over the wire only under `run --check`
  - L69 stale "`run --brain SOCKET` and the mod send every decision ... over a Unix socket" -> rewrite: the mod starts `brain` as a child and talks over its stdin/stdout; `run --check` replays over an in-process wire
  - L69 link "[Introduction §3](/en/#sec-3)" points to "Processes and data"; the rule "Swap the world, not the brain" is idea 3 of §1 -> link `/en/#sec-1`
  - L86 "the structural bug of spire-mentor, a prior-art project" -> keep (design rationale about prior art, not our history)
  - L125 stale "optionally ... a logger" -> BrainServer(harness, ended, received) has no logger; drop
  - L127 stale whole "Listening" paragraph (`Listen(path)`, socket file replaced, directory readable by the user) -> rewrite: `Serve(WireStream)` over stdin/stdout; `Bind` only for an abstract in-process socket
  - L129 citation `BrainServer.cs:75-136` -> file is 116 lines; loop is 41-94
  - L134 quote "are the mod and the brain built from the same commit?" -> keep (it is the current error text, BrainServer.cs:102)
  - L140 citation `BrainServer.cs:138-154` -> 97-113
  - L148 stale "`Microsoft.Extensions.Logging.Abstractions` 9.0.22" -> Harness.csproj references only Brain; drop
Inbound links to deleted/renumbered sections: none (no section deleted or renumbered). §4 has 9 inbound links, §5 has 11; both keep their numbers.
Figures/data: none imported.
Twins: match (same tokens; the "the user" hit is the OS user of the socket directory, not the owner; goes with L127).
Future/next content: L17 "Planned: the rest of the public record; dropping stale requests; wall-clock budgets; degraded answers"; §4 L102-121 `PublicRecord` sketch and its priority list; L117 "Today the move queued in a world is drawn uniformly" (fact, keep); §7 open questions 1-2.
Rough size of the rewrite: ~0 removed, ~8 lines rewritten (L17, L69, L125, L127, L129, L140, L148, link fix).

### en/arch/harness/routing.mdx  (lines 169)  -- FATE: KEEP (light EDIT)
Reason: describes the current routes; the champion name and nets are the current champion; one stale wire sentence.
Sections:
  - §1 `#sec-1` The routing table -- keep (L33 small edit)
  - §2 `#sec-2` Combat routes -- keep
  - §3 `#sec-3` Macro and prompt routes -- keep
  - §4 `#sec-4` The budget -- keep facts; "Planned: budgets by mode" (L90-98) -> future decision
  - §5 `#sec-5` Seeds and determinism -- keep (L116 planned sentence -> future)
  - §6 `#sec-6` Failures -- edit L145; "Planned handling" table (L162-169) -> future decision
Findings:
  - L28 `combat plan c9-2 ... combat prompts first, macro l0 v9-2 clone, ...` -> keep: current champion; identical to `eval/champions/sl.json` "champion"
  - L31 "combat net c9-2 ... V v9-2 ... V v9-2" -> keep (current). No older net names on this page.
  - L104 hash-like token `ce13ba5116b7f48a` -> keep: it is the run id of seed `0SSL7NS01MU6` (verified: FNV-1a 64 of `"0SSL7NS01MU6"`), a current format example, not a commit. A mechanical 7-40-hex scrub will hit it: whitelist it (it also appears in figures and data).
  - L16 table header "brains today" -> "brains" (optional wording)
  - L33 incomplete "refuses `plan` and `--afterstates clone` outside SL, and `pimc` outside NoSL" -> also refuses `l0` in SL without `--afterstates clone` (ChampionOptions.cs:42-43)
  - L116 future "The champion's version is planned to join the hash once brains have versions" -> future list
  - L145 stale "an I/O or JSON error, drops the host ([Overview §5])" -> on stdin/stdout such an error ends the brain's `Serve`; there is no next host
Inbound links to deleted/renumbered sections: none deleted. If the planned blocks go, §4 and §6 keep their numbers (inbound: §4 from output.mdx:54, glossary.mdx:86, harness/index.mdx:17, decision-log.mdx:45; §6 from output.mdx:170, simulator/worlds.mdx:33, protocol/wire.mdx:129, harness/index.mdx:17, mod/auto.mdx:114). Note output.mdx:54 and harness/index.mdx:17 cite §4 for the planned anytime budget, wire.mdx:129 and harness/index.mdx:17 cite §6 for planned degraded answers: they go with the planned blocks.
Figures/data: none imported.
Twins: match (c9-2 x2, v9-2 x4, ce13ba5116b7f48a x1 in all three).
Future/next content: L90-98 budgets by mode, anytime search, "auto-play (R4)" row; L116; L162-169 planned failure handling.
Rough size of the rewrite: ~0 removed, ~3 lines edited (L33, L145, optional L16).

### en/arch/harness/decision-log.mdx  (lines 126)  -- FATE: EDIT
Reason: current format, but an older net name in the field table, stale `--brain` sentences, a stale test count, and a figure with old nets and an old V scale.
Sections:
  - §1 `#sec-1` Who writes it, and when -- edit (L16)
  - §2 `#sec-2` Three lines -- keep; figure needs rework (below)
  - §3 `#sec-3` Every field -- edit (L39)
  - §4 `#sec-4` Numbers and hashes -- keep
  - §5 `#sec-5` Candidates and plans -- keep
  - §6 `#sec-6` Explored and replanned steps -- keep
  - §7 `#sec-7` What else a line carries -- keep
  - §8 `#sec-8` Tests -- edit (L100, L106, L120); planned tests (L122-126) -> future decision
Findings:
  - L10 future "narration will read its candidates"; L25 "narration is to be built" -> future list
  - L16 stale "`--log` is refused together with `--brain`, whose decisions are logged by the brain" -> drop (`run --brain` is gone)
  - L39 version "`pimc c9-1`, `plan c9-1`" -> `pimc c9-2`, `plan c9-2` (current champion) 
  - L100 stale "(or to the `--brain` socket)" -> drop
  - L106 stale "with `--brain` both are empty, so they are not compared" -> drop
  - L120 stale "`GateTests` (10)" -> GateTests.cs has 8 tests; `ExplainTests` (2: candidates, map routes) is missing from the table
Inbound links to deleted/renumbered sections: none.
Figures/data:
  - DecisionLog.svelte (no data import; values inline):
    - displayed old net names: L28 `l0 v9-1 clone` (card prompt and macro lines), L38 `plan c9-1` with note "combat net c9-1", L73 note "L0 with V v9-1" -> c9-2 / v9-2
    - caption L107 history "from the book's example run, which an earlier V and combat net played; the brain names show the current nets" -> false (they show c9-1/v9-1) and history; drop the "earlier" clause
    - L76 note "valued healing a little above smithing (42.62 against 42.20 floors)" -> an older V's floor scale; V now gives P(win) -> replace with win chances or drop numbers
    - L44 budget note "(the low effort)" with `easy_beam: 2` -> the current SL champion plays medium (easy 4, routing L28)
    - decision numbers/floors (4, 1, 378; floor 25 38/70 HP) come from the old c2/v6-3-2 play of the seed (see cross-page 2)
    - no hashes, hosts or dates displayed; run id `ce13ba5116b7f48a` is a run id (keep)
  - Extra asked: RunTimeline.svelte (game.mdx) displays only seed `0SSL7NS01MU6`, floor 48, 848 decisions -> no hash/old name/host shown. Its data `example-run.json` `source` carries rev `1fc5f50` (hash), champion `combat plan c2 ... p0, macro l0 v6-3-2 clone ...` (old nets, old prompt name), report `eval/silent/reports/2026-10-30-sl-r1-promotion-1-300-plan-c2-l0-v6-3-2-clone-p0.json` (date, old nets), and `how` repeating that path -> strip `rev`/`report`/`champion` or regenerate.
  - Extra asked: NodeTimeline.svelte (environments/information.mdx) displays seed only (caption) and decision numbers -> clean on screen. Data `example-nodes.json` `source`: `job` `data/silent/sl/eval/2026-10-30-eval-v6-3-2-clone` (job folder: gitignored, date, old net), `rev` `1fc5f50`, champion c2/v6-3-2/p0, `how` with the dated report path -> strip.
Twins: match (c9-1 x2 in all three; socket word in L100 in all three).
Future/next content: L10, L25 narration; §8 planned tests 1-3.
Rough size of the rewrite: ~2 sentences removed, ~3 lines edited; figure: ~6 strings + caption.

### en/arch/protocol/index.mdx  (lines 120)  -- FATE: KEEP (light EDIT)
Reason: current design rules; one stale socket label in the diagram.
Sections:
  - Status aside -- keep (Planned list -> future)
  - §1 `#sec-1` Three conversations -- edit (L34 diagram edge)
  - §2 `#sec-2` An assembly that knows no game -- keep
  - §3 `#sec-3` What may be in it -- keep
  - §4 `#sec-4` JSON conventions -- keep
  - §5 `#sec-5` Tests -- keep
  - §6 `#sec-6` Planned -- future decision (whole section is planned)
  - §7 `#sec-7` This chapter -- keep table; open questions -> future
Findings:
  - L34 stale mermaid label "wire: JSON lines<br/>over a Unix socket" -> "over the brain's stdin and stdout"
  - L87 "catch a host built from another commit" -> keep or say "another build" (mechanism, no hash)
Inbound links to deleted/renumbered sections: if §6 is deleted, §7 becomes §6: no inbound links to `protocol/#sec-6` or `#sec-7` from other pages; in-page L17 links `(#sec-6)` and must go with it.
Figures/data: none imported.
Twins: match (no hashes, dates, names; zh-CN translates socket as 套接字).
Future/next content: L17 "Planned: the event stream ..., facts ..., degraded"; §6 L104-106; §7 open questions 1-3.
Rough size of the rewrite: 1 diagram line; optional 1 word; §6 (5 lines) if the future content goes.

### en/arch/protocol/wire.mdx  (lines 145)  -- FATE: EDIT (§1 REWRITE)
Reason: message formats are current; the connection section and the two-hosts table describe the removed socket and `run --brain`; figure has old names.
Sections:
  - §1 `#sec-1` The connection -- rewrite (whole section stale)
  - §2 `#sec-2` The format -- keep
  - §3 `#sec-3` The messages -- keep
  - §4 `#sec-4` One run, step by step -- keep; figure needs edits
  - §5 `#sec-5` The rules of the exchange -- edit (L72 citation)
  - §6 `#sec-6` SL: the node messages -- keep
  - §7 `#sec-7` The recommendation -- keep; Planned (L126-129) -> future
  - §8 `#sec-8` The two hosts -- edit (L133 header)
Findings:
  - L10 stale "the simulator hosting a run (`run --brain`)" -> the simulator over the wire is `run --check`; the mod starts the brain
  - L14 stale "A Unix domain socket ... `$XDG_STATE_HOME/sts2ai/brain.sock` ... (`BrainSocket.DefaultPath`) ... Steam runs the game in a container" -> rewrite: the brain is the mod's child process; JSON lines on its stdin/stdout; its stderr to `brain.log` in the mod's folder
  - L15 stale "`BrainServer.Listen` ... replaces a socket file ... refuses to start if a brain still listens" -> keep only: `run --check` binds an abstract socket (`BrainServer.Bind`)
  - L16 stale "One host at a time ... goes on listening" -> the brain serves the one host that started it, until stdin closes
  - L17 "Runs one after another" -> keep
  - L27, L73 "built from another commit / different commits" -> keep or "another build" (mechanism; no hash)
  - L72 citation `BrainServer.cs:101-105` -> now the hash check; the id check is BrainServer.cs:62
  - L126-129 future "Planned: `facts` ..., `degraded` ..." -> future list
  - L133 stale header "the simulator (`run --brain`, `run --check`)" -> `run --check`
Inbound links to deleted/renumbered sections: none renumbered. §1 (rewritten) has 1 inbound link, simulator/runs.mdx:174 (check its wording: it likely also says socket). §8 has 1 inbound link, mod/index.mdx:13.
Figures/data: WireExchange.svelte (no data file):
  - L17 displayed champion `combat plan c9-1 ... combat prompts p0, macro l0 v9-1 clone, macro prompts l0 v9-1 clone` -> older nets and the old prompt name; current is `... c9-2 ... combat prompts first ... v9-2 ...` (eval/champions/sl.json)
  - L41, L61 displayed `"brain":"l0 v9-1 clone"` -> `l0 v9-2 clone`
  - L42 note "The macro brain sends no plan here; a combat search would add its plan and candidates" + L41/L61 JSON without `candidates` -> stale: L0's recommendations outside combat carry `candidates` (routing §3 "Scored options", wire L112)
  - no hashes/hosts/dates; run id `ce13ba5116b7f48a` (keep); `game_version` `v0.111.0` (keep)
Twins: match (brain.sock x2, ce13ba... absent from text in all three).
Future/next content: L126-129.
Rough size of the rewrite: §1 4 bullets -> ~3 new; ~4 other lines (L10, L72, L133, optional L27/L73); figure: 3 strings + 1 note.

### en/arch/protocol/observation.mdx  (lines 178)  -- FATE: KEEP
Reason: field-by-field description of the current observation; no hashes, dates, names, hosts; only the figure caption carries version wording.
Sections:
  - §1 `#sec-1` One observation per decision -- keep
  - §2 `#sec-2` The run -- keep
  - §3 `#sec-3` A combat -- keep (figure caption edit)
  - §4 `#sec-4` Identity and order -- keep
  - §5 `#sec-5` Screens and their actions -- keep
  - §6 `#sec-6` Card prompts -- keep
  - §7 `#sec-7` Text, numbers and what is left out -- keep (L165 "not included yet" -> "not included", optional)
  - §8 `#sec-8` Planned: the event stream -- future decision (whole section planned)
Findings:
  - L165 future flavour "Event numbers are not included yet" -> state as fact, move the plan to the future list
  - L178 "Today the one piece of history the brain keeps" -> keep (fact) if §8 stays; else move to §7
Inbound links to deleted/renumbered sections: if §8 is deleted (last section, no renumbering): gamebridge/index.mdx:68, harness/index.mdx:116, protocol/index.mdx:104.
Figures/data: ObservationExplorer.svelte + `round-start.json`:
  - caption L117 (3 langs) version/history "one of the test states an earlier combat net's file carried" -> "a real start of a round from NoSL self-play"
  - component comment L2-3 same wording (not displayed)
  - data `source.file` `weights/silent/c2.json` (old net; file not in git) and `source.what` "the combat net c2's file" -> not displayed; strip `source` or reword
Twins: match (nothing found in any of the three).
Future/next content: §8 event stream; L165 event numbers whitelist; protocol/index open question 3 overlaps.
Rough size of the rewrite: ~1 line; caption; data `source` block.

### en/arch/protocol/worlds.mdx  (lines 124)  -- FATE: KEEP
Reason: current types and messages; nothing from any category except two planned sentences.
Sections:
  - §1 `#sec-1` The world API -- keep (L57 planned sentence -> future)
  - §2 `#sec-2` The node clone and the record of enemy moves -- keep
  - §3 `#sec-3` A scored action -- keep
  - §4 `#sec-4` The worker messages -- keep
  - §5 `#sec-5` The answers -- keep
  - §6 `#sec-6` What crosses, and what does not -- keep (L124 planned -> future)
Findings:
  - L57 future "Worlds of a whole run are planned" -> future list
  - L124 future "Planned: requests that open a world in a worker and then step it" -> future list
Inbound links to deleted/renumbered sections: none.
Figures/data: none.
Twins: match (zh-CN/zh-tw "使用者" = "user" of the world API, not the owner).
Future/next content: L57, L124.
Rough size of the rewrite: 0-2 lines.

### en/arch/output.mdx  (lines 190)  -- FATE: EDIT
Reason: the built panel/marks/auto are current; it has two `tmp/` pointers, a dated session, a dropped-variant sentence, the LLM gateway, stale socket facts, and a large planned share; figures carry a commit hash and the old prompt name.
Sections:
  - Status aside -- edit (L19)
  - §1 `#sec-1` The contract -- keep; "Planned." (L51-54) -> future
  - §2 `#sec-2` The panel -- edit (L87, L93)
  - §3 `#sec-3` Highlight -- edit (L97, L113); planned anchors rows and L130 "Order of work" -> future
  - §4 `#sec-4` Auto -- keep (L153 planned driver -> future)
  - §5 `#sec-5` Narration -- future decision (whole section planned); L161 edit regardless
  - §6 `#sec-6` Fair play in output -- edit (L173, L174); L175 planned row -> future
  - §7 `#sec-7` Tests -- rewrite (L181 date/story; list is planned)
  - §8 `#sec-8` Open questions -- future decision
Findings:
  - L19 untracked "Background notes are in the local note `tmp/notes/output-design.md`." -> drop sentence
  - L87 stale "beside the brain's socket" -> "beside the brain's log" (Settings.Dir: settings.json and brain.log)
  - L93 experiment/history "A zero-UI variant ... was considered for the earliest testing only." -> drop
  - L97 untracked "`Nodes.Cards.Holders/NHandCardHolder.cs:613-640`" -> decompiled game path not in the repo, against the aside's "class name only" rule -> keep `NHandCardHolder.UpdateCard()` only
  - L113 untracked "(local note `tmp/notes/output-design.md` §3.2)" -> drop parenthetical
  - L130 plan "Order of work: hand and targets (done); card reward; map (done); ..." -> future list / drop
  - L161 host-like "(the project's LLM gateway, cheap models only)" -> drop parenthetical (private external service); "cheap models" rule already in Introduction §2
  - L173 stale "The socket carries only protocol messages" -> "The wire (the brain's stdin and stdout) carries ..."
  - L174 stale "The mod writes only its settings file and its lines in the game's log" -> it also writes the brain's stderr to `brain.log` (Host.cs:40)
  - L181 date+story "ran in the game in the session of 2026-10-28" -> drop the session/date; "None of these is built" list -> future
Inbound links to deleted/renumbered sections: if §5 Narration is deleted, §6-§8 renumber. Inbound to `output/#sec-5`: environments/worlds.mdx:115, combat-brain/index.mdx:152, harness/decision-log.mdx:25; in-page L19, L53, L93. Inbound to `output/#sec-6`: protocol/index.mdx:72 (would become #sec-5). No inbound to #sec-7/#sec-8.
Figures/data:
  - PanelMock.svelte + panel-examples.json:
    - displayed champion in the panel head (L197, from `source.champion`): `... combat prompts p0 ...` for all three answers -> current name is `first`
    - displayed caption L341-343 (all 3 langs) hash "at 81adf16" -> drop; component comment L4 same
    - displayed message second line L101 `~/.local/state/sts2ai/brain.sock` -> stale; Assist.cs:190 shows `link.Where` (the bundled brain's launcher path); L102 "Connection refused" example is socket-era too
    - L347 "This run was won too." -> mild story; optional drop
    - data (not displayed): top-level `source` string "played locally on 2026-11-07 ... at 81adf16 through a logging proxy" (date, hash), `promotion-1-100 #12`; each answer's `source.rev` `81adf16` -> strip
  - MapRoute.svelte + panel-examples.json `map`:
    - displayed: seed, floor 43, HP 24/66, the route -> no hash/name shown, but the pick shown is the old champion's (c2/v6-3-2 play)
    - data (not displayed): `job` `data/silent/sl/eval/2026-10-30-eval-v6-3-2-clone` (gitignored job folder, date, old net), `rev` `1fc5f50`, champion c2/v6-3-2/p0, `values` `sim afterstates ... --value weights/silent/v6-3-2.json` (old net, not in git) -> strip or regenerate from the current champion
    - L48-52 labels "Map route", "Off", "Next point", "Whole route" are hard-coded English in all three locales (not a category; i18n bug)
Twins: match (2026-10-28 x1 and tmp/notes/output-design.md x2 in all three).
Future/next content: L19 "Planned: highlights on the other surfaces, narration, and the facts"; §1 L51-54 facts, anytime answers; §2 L93 planned facts and per-surface highlights; §3 L121-128 planned anchor rows, L130 order of work; §4 L153 auto-run driver; §5 whole; §6 L175 NumReloads; §7 planned tests; §8 open questions.
Rough size of the rewrite: ~3 sentences removed (L19, L93, L181 part), ~5 lines edited (L87, L97, L113, L161, L173-174); figure: 1 caption x3, champion strings in data, 1 message line.

---

## Cross-page issues

1. **Socket removed in code (6e903b2), docs still describe it.** In this group: harness/index L17, L69, L125, L127, L129, L140; routing L145; decision-log L16, L100, L106; protocol/index L34; wire L10, L14-16, L72, L133; output L87, L173-174; PanelMock L101-102. Outside the group but linked from here and likely stale too: simulator/runs §7 (wire L10 cites it for the `brain` command; runs.mdx:174 links wire §1), simulator/reference §4 (`run --brain`), mod/loop, mod/index (L13, L111), glossary.mdx:37. Suggest one owner rewrites all socket text at once.
2. **"The book's example run" is two different plays of seed 0SSL7NS01MU6.** example-run.json, example-nodes.json, DecisionLog (decision 378 at floor 25) and MapRoute (`panel-examples.json` `map`, decision 696) come from the c2/v6-3-2 play at 1fc5f50 (848 decisions, won on 48); PanelMock's three answers come from the c9-2/v9-2 play at 81adf16 (decision 869 > 848; also "won on floor 48"). Regenerate all figure data from one current-champion play, and drop `rev`/`job`/`report`/dated paths from every `source` block (`round-start.json`, `example-run.json`, `example-nodes.json`, `panel-examples.json`).
3. **Old prompt-route name `p0`** in displayed champion strings (WireExchange L17; PanelMock head via panel-examples.json x3) and data (example-run/nodes, map). Code and `eval/champions/sl.json` say `combat prompts first`. (The champion files' report names still end `-p0.json`: outside this group.)
4. **Older net names in visible text**: decision-log L39 (c9-1); DecisionLog figure (c9-1, v9-1); WireExchange figure (c9-1, v9-1). Only routing L28/L31 use the current c9-2/v9-2.
5. **Old V scale**: DecisionLog's rest-site note "(42.62 against 42.20 floors)" is a floor-valued V; current V and every page say chance to win.
6. **Hex scrub whitelist**: `ce13ba5116b7f48a` (run id, routing L104, WireExchange, DecisionLog, all four data files) is not a commit hash; `0SSL7NS01MU6` is a seed. Keep both.
7. **Stale small facts**: Harness.csproj has no Logging.Abstractions (harness/index L148); BrainServer has no logger (L125); GateTests has 8 tests not 10 and ExplainTests is unlisted (decision-log L120); champion factory also refuses `l0` without the clone in SL (routing L33); harness/index L69 cites Introduction §3 for idea 3 of §1.
8. **Overlap that could merge**: the brain server's message handling is described three times (harness/index §5 table, wire §5 rules, routing §6 "On the wire"); `Candidate` four times (wire §7 table, worlds §3, decision-log §5, output §1 table). Suggest wire §5 owns the exchange rules and worlds §3 owns `Candidate`, the others link.
9. **Planned share**: output.mdx is roughly a third planned (§5, §7, §8, half of §3, rows of §1/§6); if the future-content decision removes it, output.mdx loses ~45 lines and its §6-§8 renumber (inbound links listed above).
