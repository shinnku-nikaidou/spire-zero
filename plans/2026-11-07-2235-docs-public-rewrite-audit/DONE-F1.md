# DONE F1: harness, protocol, output

All edits made in en, then mirrored in zh-CN (root) and zh-TW, same sentences, the twins' own terms (zh-CN 进程/子进程/套接字,
zh-TW 行程/子行程/socket, 頭目 for Boss). No heading renumbered, retitled or deleted; no component or data file touched;
nothing built or committed. Code read at HEAD 8d2b9ed (src/ unchanged since).

## Per page

### harness/index
- Status aside: `run` plays every run in its own process and a second time over the wire under `run --check`; the
  `brain` command serves the mod over the wire (link §5). Planned list kept (D1).
- §2 "In process and over the wire": the mod starts `brain` as a child process and sends decisions over its stdin/stdout
  (link protocol/wire §1); `run --check` replays to a `BrainServer` in its own process; "swap the world" link now
  `/LOCALE/#sec-1` (idea 3), was `#sec-3`.
- §5: no logger in the constructor sentence; "Listening" paragraph replaced by **Serving**: `Serve(wire)` over a
  `WireStream`; `brain` serves the one host that started it on its stdin/stdout and ends when stdin closes; an I/O
  error / bad JSON / malformed message / other protocol version ends `Serve` and the brain, the mod starts it again
  (link mod/loop §8); only `run --check` uses a socket (`Bind`, abstract name, a thread of the same process).
  Citations: message loop `BrainServer.cs:40-94` (was 75-136), answering `BrainServer.cs:97-113` (was 138-154).
  The `brain` command paragraph: serves its starter on stdin/stdout until stdin closes (`SimWorker/Commands/BrainCommand.cs`),
  everything else on stderr (first line with both champions, a line per run), which the mod keeps in `brain.log`
  (link mod §4); `--log` / `--wire` are for a brain started by hand, the mod starts it without them.
- §6: dropped `Microsoft.Extensions.Logging.Abstractions` (Harness.csproj references only Brain).
- Left on purpose: §4 planned `PublicRecord` and its list, §7 open questions (D1); the spire-mentor sentence (prior art);
  the quoted error text "are the mod and the brain built from the same commit?" (it is the code's message,
  `BrainServer.cs:102`); `Dispatcher.cs:197-202` and `58-217` (checked, correct).

### harness/routing
- §1 table header "brains today" -> "brains".
- §1 the champion's name: per FACTS/D2 (routing is not a page that names nets) the code block is now a format,
  `combat plan COMBAT beam 8x300 easy 4 loss plans 3, combat prompts first, macro l0 VALUE clone, macro prompts l0 VALUE clone`,
  with "where `COMBAT` and `VALUE` stand for the names of the champion's combat net and V" linking
  evaluation/champions; the sentence after it no longer names c9-2 / v9-2. (The audit said keep c9-2; FACTS wins.)
- §1 champion factory also refuses, in SL, an L0 that does not play on the node's clone (ChampionOptions.cs:42-43);
  §6 "On the wire" list gets the same refusal.
- §6 an I/O / JSON / version error ends the brain's `Serve` and the brain; one host, no next one; the mod restarts it.
- Left on purpose: §4 "Planned: budgets by mode" (with "auto-play (R4)"), §5 "champion's version planned to join the
  hash", §6 planned table (D1); run id `ce13ba5116b7f48a` (a run id, not a commit: whitelist it in D9's lint).

### harness/decision-log
- §1: dropped "`--log` is refused together with `--brain`"; `brain --log` is for a brain started by hand.
- §3 `brain` row: `pimc NAME` or `plan NAME` with the combat net's name (was `c9-1`).
- §8: dropped "(or to the `--brain` socket)" and "with `--brain` both are empty"; `GateTests` (8, was 10); new row
  `ExplainTests` (2: candidates with values, chosen first; map routes only with `explain`).
- Left on purpose: L10 / L25 narration (planned), §8 planned tests (D1). No prose sits around the figure.

### protocol/index
- Status aside: the mod advises over the wire and `run --check` plays the simulator's runs a second time over it
  (was "the simulator plays whole runs" over it).
- §1 mermaid edge: "over the brain's stdin and stdout" (zh twins keep their translated labels, as they already were).
- §4 "a host built from another commit" -> "a host from another build"; §7 table "its socket" -> "its connection".
- Left: §6 Planned and §7 open questions (D1).

### protocol/wire
- Description: "the socket" -> "the connection". Intro: the simulator host is `run --check` (was `run --brain`).
- §1 REWRITTEN (4 bullets): the brain is the mod's child (`brain/RID/`, `brain --champion champion.json --workers N`,
  working folder `brain/`, JSON lines on its stdin/stdout, `BundledBrain.cs`; pipes, no socket or port; stderr to
  `brain.log` in `$XDG_STATE_HOME/sts2ai`, default `~/.local/state/sts2ai`, beside `settings.json`); one host until stdin
  closes, a bad message or broken pipe ends it, the mod kills what is left (workers included), waits 2 s, starts it
  again; `run --check` over an abstract socket in its own process (`BrainServer.Bind`); runs one after another (kept).
- §2 and §5: "another commit / different commits" -> "another build / different builds". §5 citation
  `BrainServer.cs:62` (was 101-105). §8 header "the simulator (`run --check`)".
- Left: §7 Planned `facts` / `degraded` (D1). `Brains/RunBrains.cs:88-99`, `Dispatcher.cs:170-186`, `World.cs:106`,
  checked, correct.

### protocol/observation
- §7 "Event numbers are not included yet ... first" -> "not included: ... including them needs a whitelist per event".
- Left: §8 planned event stream and "Today the one piece of history ..." (D1). Prose before the explorer already says
  "the start of a round from NoSL self-play".

### protocol/worlds
- No change (KEEP). L57 and L124 planned sentences stay (D1).

### output
- Status aside: dropped the `tmp/notes/output-design.md` pointer.
- §2 Settings: `settings.json` in `$XDG_STATE_HOME/sts2ai` (default `~/.local/state/sts2ai`), "beside the brain's log"
  (was "beside the brain's socket"); dropped the zero-UI variant sentence.
- §3: dropped the decompiled line citation `Nodes.Cards.Holders/NHandCardHolder.cs:613-640` (class name only, as the
  aside says); anchors "verified in the decompiled game's source" (was the `tmp/notes` pointer); deleted the
  "Order of work" line (its planned rows stay).
- §5: dropped "(the project's LLM gateway, cheap models only)".
- §6: "The wire (the brain's stdin and stdout) carries only protocol messages"; the mod writes its settings file, the
  brain's log (`brain.log`, the brain's stderr) and its game-log lines.
- §7: dropped the 2026-10-28 session sentence; "the executor's and the panel's game calls are checked against the
  decompiled game" (link mod/auto §7, matching F2's planned wording).
- Left on purpose: every planned block (§1 Planned, §2 "Planned for the panel", §3 planned anchor rows, §4 driver,
  §5 Narration, §6 NumReloads row, §7 list, §8 open questions) under D1; the panel's SL/NoSL switch sentence (UI only,
  says nothing about which champion NoSL gets).

## For the figures step (captions and data)
- **DecisionLog.svelte** (decision-log §2): brain names from the regenerated play (`plan` + the champion's combat
  net, `l0` + the champion's V + `clone`; no c9-1 / v9-1); budget note "(the low effort)" with `easy_beam: 2` -> the
  medium budget (`easy_beam: 4`); rest-site note "42.62 against 42.20 floors" -> V's chances to win; caption drops
  "which an earlier V and combat net played; the brain names show the current nets"; decision numbers / floor 25 /
  38 of 70 HP follow the new play.
- **WireExchange.svelte** (wire §4): `CHAMPION` = `eval/champions/sl.json`'s string (with `combat prompts first`);
  `"brain":"l0 v9-1 clone"` x2 -> the champion's L0 name; L0's recommendations outside combat carry `candidates`
  (the L42 note "The macro brain sends no plan here; a combat search would add its plan and candidates" is wrong for
  L0); caption itself is fine.
- **PanelMock.svelte + panel-examples.json** (output §2): head champion with `first`; caption without `a3a2d15` and,
  per D2, without net names ("the SL champion", "the NoSL champion"); the "Connecting to the brain" mock's second line
  is `link.Where`, the bundled launcher's path (`.../mods/Sts2Ai/brain/linux-x64/Sts2Ai.SimWorker`), not
  `brain.sock`; the "No brain running" example reason is a `BundledBrain.Run` / `BrainLink` reason ("the brain ended
  (exit code N): ... (log: .../brain.log)"), not "Connection refused"; drop "This run was won too."; all four entries
  from one play. Output §2 prose quotes this data and waits for it: "+0.6" / "-2.6", "win chance 65.7%",
  "this fight 100% · 64 HP left", "6 more turns planned".
- **MapRoute.svelte** (output §3): data from the same play; its caption's "monster at row 10 over the rest site ...
  through an elite and a rest site" must follow the new pick. Its labels "Map route", "Off", "Next point",
  "Whole route" are hard-coded English in all locales (i18n bug, component).
- **ObservationExplorer** (observation §3): caption "a real start of a round from NoSL self-play" (prose already
  says it); data `source` without `weights/silent/c2.json`.

## Cross-page links
- Changed: harness/index §2 Introduction link `/LOCALE/#sec-3` -> `/LOCALE/#sec-1`.
- New links (all to existing anchors): protocol/wire §1 from harness/index §2; mod/loop §8 from harness/index §5 and
  wire §1; mod §4 from harness/index §5 and wire §1 (F2 rewrites mod/index §4 as "the brain it ships": it should
  name `brain.log`); evaluation/champions from routing §1; simulator/runs §7 from wire §1.
- Others must change: `simulator/runs.mdx:174` (E) still says "(or to the `--brain` given)" next to its link to
  wire §1; simulator/runs §7 and simulator/reference §4 should match wire §1's wording (one host on stdin/stdout,
  `run --check` on an in-process abstract socket).

## Where the code disagreed with FACTS or the audit
- Audit: message loop "41-94" -> the `while` starts at line 40; cited `BrainServer.cs:40-94`.
- Audit: routing L28/L31 "keep c9-2" vs FACTS D2 (routing not in the naming list): followed FACTS (placeholders).
- FACTS "When the brain ends, the mod kills it with its workers": `BundledBrain.Run` kills the process tree only if
  the brain has not exited (its `finally`), after the session ends (brain closed stdout, or an IO / JSON error);
  wrote "kills what is left of it, its search workers included".
- `brain` exits through `Cli.Run`'s handler (exit code 2 for IO / JSON / InvalidData, 70 otherwise) when `Serve`
  throws; the pages say only that the brain ends.
- `ExplainTests.cs:27` still names a prompt route `"p0"` in its fixture (test name only), besides plan §9's code
  comments (`RunCommand.cs:11` "Combat prompts go to p0").
- The bundled `champion.json` is `sl.json` (plan, `afterstates clone`): `brain`'s NoSL champion built from it is
  refused at `hello` (`ChampionOptions.cs:41`), so a NoSL host gets an `error`. Routing §1 / harness §5 describe the
  command in general ("NoSL hosts get `--policy`'s combat brain") and stay true; none of my pages describes the mod's
  NoSL switch outcome (src/Sts2Ai.Mod unchanged at the time of writing).

## Second pass: commit 70e9a25 (read `git show 70e9a25`, code at HEAD a0071e4)

All three locales.
- **harness/index §5** (`brain` command): both champions have the same macro brain whose afterstates the host's
  environment sets, whatever `--afterstates` says (screen for NoSL, node's clone for SL; `BrainCommand.Champions`; the
  clone needs a combat net, without one SL hosts are refused); so one champion file serves both: the mod ships the
  NoSL champion's file, its SL hosts get the SL planner with the same nets and L0 on the clone, under the SL
  champion's name. "`--log` / `--wire` are for a brain started by hand" -> the mod passes both only when its settings'
  `Record` is on, as a pair of files in `captures/` beside `brain.log`.
- **harness/decision-log §1**: same fix for `brain --log` (passed with `--wire` when `Record` is on).
- **harness/routing §1**: the refusals I added hold in the champion factory (`ChampionOptions.Harness`, lines 38-43)
  and therefore for `run`, which plays its options as given ("SL runs with L0 take `--afterstates clone`"); for
  `brain` they no longer arise for L0, since it sets the afterstates by environment: sentence added, "so one champion
  file serves both". §6's list of refused `hello`s is unchanged (still the factory's reasons; a NoSL host of a brain
  whose `--policy` is `plan`, e.g. `--champion sl.json`, is still refused).
- **protocol/wire**: §1 first bullet says `champion.json` is the NoSL champion's file, played for NoSL hosts and, with
  the SL planner and L0 on the clone, for SL hosts (link harness §5); new §1 bullet **A session on record**
  (`Record` -> `--wire` + `--log`, a pair of files named by the brain's start in UTC in `captures/`, which `clones`
  replays; link mod/sl-record §8). §5 "What `error` means": the example is now "a NoSL host of a brain whose
  `--policy` is the SL planner", plus "the brain the mod ships takes both environments" (a NoSL host of the bundled
  brain gets `ready`). §8 the mod's "when it asks": in SL only in a node it can clone, one with a checkpoint and an
  unbroken record (link mod/sl-record §6).
- **protocol/index §5**: the `clones` row: "over a `brain --wire` log, such as the mod's capture with `Record` on".
- **output §6**: the mod-writes row adds that with `Record` on, the brain it starts also writes the messages it gets
  and its decision log in `captures/`. §7 needed no change.
- Checked and unchanged: routing §3 (the clone for the macro side is a `Dispatcher` fact, still "with `--afterstates
  clone`"), harness §4 (an SL decision before any checkpoint throws; the mod now never asks one), output §2's switch.
- For F2: mod/sl-record §6 still says "SL combat decisions are not asked" in a broken node; since 70e9a25 every SL
  decision of such a node is held back (`Assist.cs:238-245`), and mod/index §6's message row ("SL, a combat, ...")
  likewise. My wire §8 row links sl-record §6 for this. Mod §5 / sl-record §8 may want the `Record` setting (no row on
  the settings screen, off by default).
- Code vs FACTS: none new. FACTS says "a pair of files named by the brain's start time (UTC)": `Captures()` names them
  `yyyy-MM-dd-HHmmss-wire.jsonl` and `-decisions.jsonl` in `Settings.Dir/captures/` (the folder of `brain.log`).
