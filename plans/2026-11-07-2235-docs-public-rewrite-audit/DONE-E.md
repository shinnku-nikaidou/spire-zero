# DONE: group E (simulator chapter)

Pages: `arch/simulator/{index,headless,combat,runs,worlds,pool,reference}.mdx` in en, zh-CN (root) and zh-tw. All
three locales of every page have the same line count, the same `##` headings and the same set of code spans (checked
mechanically). No heading renumbered or retitled; no section deleted. Nothing built, nothing committed. Code read at
HEAD a0071e4 (70e9a25 landed while I wrote; its brain changes are in).

## Per page

**index.** Status aside: "runs hosted against a brain over the wire" -> runs "played in-process or, under
`run --check`, again over the wire". §3: `run --brain SOCKET` sentence -> `brain` is the same Harness and brains as a
process of its own, which the mod starts and talks to on its stdin and stdout. §5 table row: "hosting runs against a
brain" -> "the brain as a process of its own". Last paragraph: the decompiled game "is not in the repository".
Kept on purpose: the `partly built` badge, "Planned: run worlds" (D1).

**headless.** §4 SapphireSeed as an example of such a method (no "once did"); §5 `tmp/notes/testmode-audit.md`
pointer dropped; §6 JIT rationale without the WriteReplay incident and without "twice as slow" (now "slower on short
runs"); §8 "The one fixed so far" -> "The group holds one fix". Kept: 1.2 s boot, ~75 dispatches per action, ~20 %
from `DOTNET_ReadyToRun=0` (the last is also printed by the code, `HeadlessGame.JitNote`).

**combat.** §2 date dropped (0.28 + 0.073 ms and 0.5 + 0.1 ms are in RestoreCost.svelte, a file in git); copy lever
"kept for later" -> "one of the levers of §7". §3 "Not modelled yet" -> "Not modelled". §5 dropped "failed one run in
165 of generation 1" and "Clamping ... failed one run in 452"; approximations line -> "§6 says where they show; they
are not checked against observations from the real game"; the dated 60/58/57/57/58 result -> "`selftest` fails if any
build does not" (`SelfTest.cs:152`). §6 REWRITTEN as present facts: a build shows the observation (BuildDifferences
empty, NoSL world refuses otherwise, selftest fails otherwise); differences by design (determinization) vs. from the
builder's approximations; the approximations show later, through monster-internal counters, with `TWO_TAILED_RAT`
verified in the decompiled game (`Models.Monsters/TwoTailedRat.cs:40-42`: `_turnsUntilSummonable`,
`_callForBackupCount`) and the builder touching monster fields only when no move shows the intent otherwise
(`CombatBuilder.Moves`); without the record, fresh move logs and approximated stun follow-ups. The fidelity table, its
method, the "at most 1 %", PARAFRIGHT / Waterfall Giant / CORPSE_SLUG are gone: no command at HEAD reproduces that
measurement and it was made on an older builder. §7 REWRITTEN: no date; the bench table (319/s, 10.3k, 3.1 ms, 379/s),
the 6.7 ms short-run line and the branching table (2.78 / 3.97 ms, 15.8 / 16.6, 329/s) are gone (older build, in no
file in git); the Combats paragraph says what `bench` prints and quotes ~275 combats/s for one process over 3,000
combats from Scaling.svelte's data; branching keeps only the restore cost; the worker profile is reduced to "most of
its time searching, restoring worlds the largest share", plus "a restore does not compare the build" (checked:
`CombatSession.Restore` passes `check: false`, `SimWorlds.NoSL` checks the root once); no hash, no "7 % faster";
"about 160 MB each" dropped; default pool "8 processes (`--workers`, one per world)"; levers: no "`perf` not
installed", copy-or-replay "is an open question".

**runs.** §4 SurfaceCounts intro -> "over the 100 SL runs of the champion's Silent report" with a link to
evaluation/champions §2; "Legitimate game behavior seen in runs at 9999 HP" -> present. §5 exit-code citation
`RunCommand.cs:146` -> `:140`. §6 "(or to the `--brain` given)" dropped; the dated 200-seed batch (and the 880-run
sentence) replaced by "What a batch that passes guarantees" (`--check` exits 1 on any difference; seeds come from the
index, not the process, so batches split over processes; `obs <digest>` compares them; MapActions sorts children;
clone check and node cost -> worlds §6); idle-wait guard "must be 0 in every batch"; inline execution kept as an
open question without "until". §7 REWRITTEN: `run` in-process; in the game `brain` is the mod's child on
stdin/stdout; the simulator plays over the wire only under `run --check`; `brain` serves the host that started it
until stdin closes, stdout is the wire, every other line on stderr; synopsis without `--socket`, with
`--sl-policy plan|pimc|play` (the code accepts pimc); the `--socket` bullet, the `run --brain` refusal paragraph and
its `inferred` badge, `Wire.cs:100-104` gone; new per 70e9a25: L0's afterstates set by the host's environment
(screen for NoSL, the clone for SL when the champion has a combat net), so one champion file serves both
(`BrainCommand.Champions`); `--log` / `--wire`: the mod passes both only when its settings file turns `Record` on, a
pair of files per brain under `captures/` beside the brain's log; the per-run line goes to stderr.

**worlds.** Description "run worlds, still planned" -> "which are planned"; §1 "Every world today" -> "Every world";
§2 the "at most 1 % ... (2026-10-27)" tail dropped; §6 relic story -> rationale (a clone loaded from the save alone
would show such a relic as new, and the SL planner would replan), no 148/120, no date, no "Before ... 35 of 7,335",
no 9,747-node batch; the check stated with its exit code (link runs §5); node costs kept, "over runs of the five
characters", no date. §7 date dropped. Planned §8 kept as is (D1).

**pool.** §2/§4 `serve` gets `--value` as well as `--combat-net` (code: `LeafChoice.WorkerOptions` passes both, and
`--combat-net` without `--value` is refused); §6 "Two gaps remain" -> "The pool has two gaps" (both re-verified in
`WorkerPool.cs`); §7: budget line says PIMC 8 x 300 and the SL planner's `medium`; PIMC "19.9 s a run" dropped; the
SL champion's speeds from its five reports (405/336, 431/286, 560/423, 1,202/906, 1,358/1,046), no hosts, no worker
count (the reports do not record it), link to champions §2; "about 160 MB per worker" dropped (follows combat §7);
§8 "Today a request" -> "A request" (planned section kept).

**reference.** Intro -> "Where this page and the code disagree, the code is right." §1: `Sts2BinDir` row says the
default `tmp/gamebin/v0.111.0` is a folder you create, not in git (the only `tmp/` left in group E; D9's lint needs to
allow it, FACTS names it); TieredCompilation row without "seen after a few thousand combats" / "2x"; ReadyToRun "set
when running the program"; the launcher paragraph replaced by FACTS's "Build and run" (`dotnet build ...
-p:Sts2BinDir=DIR`, `DOTNET_ReadyToRun=0 dotnet .../Sts2Ai.SimWorker.dll --help`, the stamped `SourceRevisionId`
with `-dirty`); no `tmp/build.sh`, `tmp/builds`, `tmp/home`, brain.sock, fleet, `.claude`. No `git archive` recipe
(I did not verify how an exported tree is stamped). §4: `selftest` needs `--combat-net` and `--value`; `run` without
`--brain` and its refusal; `brain` row: no `--socket`, serves the host that started it on stdin/stdout until stdin
closes, L0's afterstates by environment. §5: "up to 60 build checks" (cuts are `.Distinct()`), the leaf priced by
`--value`, the dated results dropped. §6: no `--brain` in the table or the trailing sentence. §7: 106 test methods
(70e9a25 added a CliTests theory; 21 classes), CombatNetTests / RunNetTests name the files they read
(`weights/all/c9-0.json`, `v9-0.json`) instead of bare net names, CliTests row gains the brain's afterstates test.
§8 Q3 without "open until". Q1 / Q2 kept (D1).

## Left on purpose

- Example-run numbers (runs L11 "848", L130 floor 48 / HP 0 of 66, L135 run line) wait for the figures step.
- Measured numbers kept without dates (older measurements I could not re-check; drop them if the lead prefers the
  strict reading): headless 1.2 s boot, ~75 dispatches/action, ~20 %; combat §7's ~275 combats/s and ~8 %
  hyperthreading (both Scaling.svelte data) and the restore costs (RestoreCost.svelte); pool §5 "Measured on 6
  combats, PIMC's trace with 8 workers diffed empty", pool §7 PIMC 68.7 vs 307.5 ms; worlds §6 node costs 8-11 ms /
  241 ms / 25-38 ms / 8.8 s; runs §4 "about 156 turns" (arithmetic).
- `c9-0` / `v9-0` appear only as file paths the xUnit tests read (reference §7), a code fact, not a champion claim.
- No local bench was run: the machine was loaded (six dotnet processes at ~80 % CPU), so a speed measurement would
  have been wrong.

## For the figures step

- runs §4 now introduces SurfaceCounts as "the 100 SL runs of the champion's Silent report" (also used by game.mdx);
  until `data/book/surface-counts.json` is regenerated from
  `eval/silent/reports/2026-11-06-sl-a0-promotion-1-100-plan-c9-2-l0-v9-2-clone-p0.json` the figure still shows the
  old 300-run data, its caption names "an earlier V's SL report on promotion-1-300" and a hash, and its label says
  "p0 in combat" (should be `first`).
- RestoreCost caption (dates 2026-10-26 / 2026-10-28) and Scaling caption (2026-10-26): drop the dates. combat §7's
  "about 275 combats a second over 3,000 combats" reads Scaling's 1-process point: change it if Scaling's data changes.
- pool §7's five speeds come from the champion's reports: a promotion changes them.

## Cross-page links and other writers' pages

- combat §6 no longer holds a measurement: pimc/index.mdx:65 (repeats the method) and :76 ("All three are fixed"),
  environments/information.mdx:144 link it; worlds §2 no longer quotes "at most 1 %".
- combat §7 no longer has the 48/38/7/16/5 % profile: combat-brain/shared.mdx:65 repeats it verbatim (group B).
- worlds §6 no longer tells the relic story or the 880-run / 9,747-node batch: environments/information.mdx:123 and
  sl-planner/index.mdx:73 tell it (groups A/B).
- reference §1 no longer has the launcher: glossary row `tmp/build.sh REV` / `sim.sh` and mod/index.mdx:113
  (`tmp/builds/REV/sim.sh`, `tmp/home`, `--socket`) point at it (groups A/F2).
- New outbound links (anchors exist): runs §4 and pool §7 -> `/LOCALE/arch/evaluation/champions/#sec-2`; worlds §6 ->
  runs §5; combat §6 -> reference §5, harness §4; combat §7 -> reference §4, worlds §2; reference §1 -> headless §6,
  runs §5.

## Where the code disagreed with FACTS.md or the audit

- `brain --sl-policy` accepts `pimc`, `plan` and `play` (`BrainCommand.cs:20-21`); the page listed `plan|play`.
- `selftest` and `serve` take `--value` with `--combat-net`: `LeafChoice.Read` refuses a combat net without V
  (`LeafChoice.cs:16`), and the pool passes both to its workers (`LeafChoice.WorkerOptions`).
- Test methods: 106 at HEAD (the audit's 105 predates 70e9a25's CliTests theory).
- FACTS's first version said the mod starts `brain` without `--log` / `--wire`; at 70e9a25 it passes both when
  `settings.json`'s `Record` is on (`BundledBrain.Captures`). FACTS's later section matches the code; the pages follow it.
- `brain`'s L0 afterstates are set by environment regardless of `--afterstates` (`BrainCommand.Champions`), so the
  Champion table's `--afterstates` row (reference §3) is true for `run`, and the `brain` row of §4 says the exception.
- The audit's fidelity numbers (combat §6) cannot be reproduced: no command at HEAD measures them.
- Code comment `Commands/RunCommand.cs:11` still says "Combat prompts go to p0" (plan §9 already lists it).
