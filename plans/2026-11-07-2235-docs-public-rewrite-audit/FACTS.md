# Shared facts and wording for the docs rewrite (writers read this first)

The plan (approved by the user 2026-11-07): /home/shinnku/codes/sts2/plans/2026-11-07-2235-docs-public-rewrite.md.
Your audit report in this folder is your worklist (line numbers match HEAD 8d2b9ed). This file fixes the facts and the
wording every chapter must share. Where this file and an audit report disagree, this file wins; where both disagree
with the code at HEAD, the code wins (read it before you write a claim about it).

## How to write

- English first (`docs/src/content/docs/en/...`), then mirror every change in zh-CN (`docs/src/content/docs/...`, the
  root) and zh-TW (`docs/src/content/docs/zh-tw/...`): same sentence structure, the twin's own existing terms. zh-TW uses
  Taiwan wording and the game's Traditional Chinese names (Boss = 頭目, Elite = 菁英, Ascension = 晉階).
- **Never change a heading's number** and never delete a numbered heading (no renumbering anywhere). A heading's title
  may change only where the plan says so (D4). Keep every `#sec-N` anchor that others link.
- Present tense, the system as it is now. Remove: commit hashes; old net / generation / dataset names (c7-2, v8-1,
  v8g2, v9g0, "generation 1" ...); plan names; dates of events (2026-10-28 ...); "no longer", "used to", "at first",
  "earlier V / combat net", "the old ...", "was replaced"; bug stories; experiment results that decided something; lineage;
  `eval/ledger.md` (never link it); remote hosts, "the hub", "storage host", "the fleet", time boxes, deadline files,
  `box.py`, Colab, "the project's LLM gateway"; `.claude/`; `tmp/` pointers; "the user" as the project owner.
- Keep: design rationale told as an argument ("X, because Y"; "Y is not used: ..."), prior art and credit, measured
  numbers of the current build or champion without a date, formats with placeholders (`DATE`, `NAME`, `CHARACTER`),
  "host" in its protocol sense (the process holding the true run: the simulator or the mod), `data/` as where outputs go
  (not in git).
- **Planned content stays** (plan D1): planned sections, rows and open questions keep their place and their `planned`
  badge; inside them remove dates, plan names, "order of work", old measurements and old net names; anything already
  built is rewritten as built.
- Mermaid labels, code blocks and code comments in pages are English in all three locales (as today).
- Do not touch figure components or data files (`docs/src/components/**`, `docs/src/data/**`) and do not change the
  numbers that come from the book's example run (seed `0SSL7NS01MU6`: decisions per run, its floors, its plan values,
  "won on floor 48", PlanTimeline / DecisionLog / PanelMock / MapRoute / RunTimeline / NodeTimeline / TdTargets values)
  or from figure data: a later step regenerates them from the current champion and rewrites those sentences. You may
  drop a claim such as "played by the SL champion" or "an earlier V" next to them.
- Do not run `pnpm check` / `astro build` (several writers share `docs/`; the lead builds once). Do not commit.
  Edit only your own pages (listed in your brief).
- Markdown bold in Chinese: write `**入口**：到达`, never `**入口：**到达` (the check flags unrendered bold).

## Facts (checked at HEAD 8d2b9ed)

**The champion.** `eval/champions/sl.json`:
`combat plan c9-2 beam 8x300 easy 4 loss plans 3, combat prompts first, macro l0 v9-2 clone, macro prompts l0 v9-2 clone`
(options: env SL, policy plan, macro l0, effort medium, value `weights/all/v9-2.json`, afterstates clone, combat-net
`weights/all/c9-2.json`). `eval/champions/nosl.json` names the same two nets (PIMC 8x300 in combat, L0 on afterstates
predicted from the screen) and **no report** (`"reports": {}`), so a NoSL gate refuses every character.
- **SL and NoSL play the same V and the same combat net. NoSL is not trained for now**: both nets learned from SL's
  games only, and neither has been trained or gated in NoSL. (The user's direction, 2026-11-07: state it as the
  current design, not as a story.)
- Naming rule (plan D2): only evaluation/champions, glossary §7 and the pages about one net file (combat-brain/network §8,
  combat-brain/leaf §4, the walkthrough, macro-brain/network §6 and §9, learning/reference §6-§7, learning/combat-net
  §3/§5) name `c9-2` / `v9-2` (as "the champion's file, `weights/all/c9-2.json`"). Everywhere else write "the champion's
  V", "the champion's combat net", "the champion".
- `weights/all/` holds `v9-0.json`, `v9-1.json`, `v9-2.json`, `c9-0.json`, `c9-1.json`, `c9-2.json`; a net's name is
  never reused.
- The champion's reports: five files `eval/CHARACTER/reports/...-promotion-1-100-...json`, named by `sl.json`; each
  100 SL runs at ascension 0, medium budget, on `promotion-1-100`. Wins of 100 (95 % Wilson interval) and mean floor:
  Silent 92 (85.0-95.9 %), 46.45; Ironclad 82 (73.3-88.3 %), 46.00; Regent 71 (61.5-79.0 %), 45.57;
  Defect 68 (58.3-76.3 %), 46.44; Necrobinder 47 (37.5-56.7 %), 41.55. 0 random decisions, 0 failed, 0 truncated, not void.
  Speed (`ms_per_decision` / `seconds_per_run`): Silent 405 ms / 336 s; Ironclad 431 / 286; Defect 560 / 423;
  Regent 1,202 / 906; Necrobinder 1,358 / 1,046. Do not say where they ran.
- The win counts appear only in the overview ("How well it plays") and evaluation/champions §2; elsewhere link there.
- The champion's two nets learned from SL self-play of all five characters at ascension 0, medium budget: 21,272 runs
  (5,608 won; V's `fit`), 4,310 held out; the combat net 430,142 held-out round starts. Read further numbers from the
  files' own `fit` / report fields (`weights/all/c9-2.json`, `v9-2.json`), never from the old pages.

**The brain link (the socket is gone).** The mod starts the brain it ships (`brain/RID/` in the mod's folder) as a child
process: `brain --champion champion.json --workers N` (N = half the logical processors, 1 to 8), working folder
`brain/`, `STS2_BIN_DIR` set to the folder the game loaded `sts2.dll` from, `DOTNET_ReadyToRun=0`, and
`SENTRY_GODOT_LIB_PATH` removed (the game's Sentry extension sets it; inherited, it makes each search worker crash at
boot). They talk JSON lines over the brain's stdin and stdout; the brain's stderr goes to `brain.log` in
`$XDG_STATE_HOME/sts2ai` (by default `~/.local/state/sts2ai`, `Settings.Dir`), beside the mod's `settings.json`. When the
brain ends, the mod kills it with its workers, waits 2 s and starts it again (`BundledBrain.cs`, `BrainLink.cs`,
`Host.cs`). `brain` serves the one host that started it, on its stdin and stdout, until stdin closes
(`SimWorker/Commands/BrainCommand.cs`). There is no `run --brain`, no `brain --socket`, no `BrainServer.Listen`, no
`BrainSocket`, no Install target. `run --check` plays each seed again over the wire to a brain server in its own process,
on an abstract Unix socket (`BrainServer.Bind`). `brain --log FILE` / `--wire FILE` exist for a brain started by hand;
the mod starts it without them unless the settings' `Record` is on. `distro/build.sh` builds the Workshop item into
`out/Sts2Ai/` (not in git).

**Since commit 7adf7fb (landed during the rewrite; read `BrainCommand.cs`, `BundledBrain.cs`, `Assist.cs`,
`UI/Settings.cs`, `distro/build.sh` at HEAD):**
- `brain` sets L0's afterstates by the host's environment: predicted from the screen for a NoSL host, played on the
  node's clone for an SL host (`BrainCommand.Champions`). So one champion file serves both kinds of host: the mod ships
  `eval/champions/nosl.json` as `brain/champion.json`; NoSL hosts get its champion (PIMC + L0 on the screen), SL hosts
  the SL planner with the same nets and L0 on the clone, under the same champion name as `sl.json`'s. The panel's
  SL/NoSL switch therefore works with one brain. `distro/build.sh` says in a comment that NoSL is not trained for now
  and plays SL's nets.
- The mod holds back every SL decision (not only combat ones) of a node without a checkpoint or with a broken record
  ("No SL clone of this room · reason · the next room has one").
- Capture: `settings.json`'s `Record` (off by default, no row on the settings screen) gives each brain the mod starts
  `--wire` and `--log`, a pair of files named by the brain's start time (UTC) in the settings folder's `captures/`, which
  `clones` replays.

**Combat prompts** go to the route `first` (`FirstPicks`) and, where a search weighs them, become search branches.
There is no `p0` (the champion's report files still end in `-p0.json`: that is a file name, not a brain).

**Gates (plan D3).** evaluation/gates §10 holds the procedure, with commands: the champion's pair and the challenger's
pair both play `eval/seeds/promotion-1-100.txt` with every character, at the champion file's options with only
`--value` and `--combat-net` changed, no `--explore`, at the same commit; each character's runs pair by seed; the new
pair is promoted when the five characters together say so (`learn/gate.py --against`). There is no low-budget part and
no deadline. Others link `/LOCALE/arch/evaluation/gates/#sec-10` instead of restating it.

**Self-play (plan D3).** macro-brain/selfplay holds the procedure: the champion plays whole SL runs at its medium budget,
ascensions 0 to 10, with even draws at chance ε in three tiers, a third of the runs each: 0.3, 0.1 and 0.02; one job per
character, tier and ascension; the number of runs is not fixed; train seeds are `run --seed 1 --start K`, a block of
100,000 indices per dataset, never meeting an evaluation seed; V and the combat net train on the newest generation
alone, `--init` from the champion's files. learning/loop §2 and §9 link it.

**Retitled sections (plan D4), numbers kept.** learning/reference §1 "Processes and outputs", §5 "Seeds";
evaluation/champions §2 "Reports"; sl-planner/budget §5 and pimc/budget §6 "The champion".

**Deleted page.** `status.mdx` (three locales). Links to it go to `/LOCALE/arch/evaluation/` or
`/LOCALE/arch/evaluation/champions/`.

**Build and run (simulator/reference §1).** `dotnet build src/Sts2Ai.SimWorker -c Release -p:Sts2BinDir=DIR`, where DIR
is a v0.111.0 game folder holding `sts2.dll` (default `tmp/gamebin/v0.111.0`, a folder you create: not in git); run the
dll with `DOTNET_ReadyToRun=0`. The build stamps the checkout's commit (`SourceRevisionId`, `-dirty` when `src/` has
changes) into run records and reports.

**Decompiled game.** Paths such as `Runs/RunManager.cs` are in the decompiled game, which is not in the repository; say
so instead of `tmp/decompiled/sts2`.
