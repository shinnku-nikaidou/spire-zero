# Audit E: simulator chapter (en/arch/simulator/*), read-only

Pages read in full: index, headless, combat, runs, worlds, pool, reference (972 lines). Components read in full:
PumpDemo, RestoreCost, Scaling, SurfaceCounts (+ data/book/surface-counts.json), WorldTurns, PoolTimeline.
Twins (zh-CN `arch/simulator/`, zh-tw) have the same line counts, links, code citations and numbers on every page;
the only differences are wording ("200 of 200" vs "200 局全部相同" and similar). So "Twins: match" below means
every finding here applies to the same line numbers in both twins.

No section deletion is required by the rules. The three "planned"/"open" sections (worlds §8, pool §8,
reference §8) are future content: listed with their inbound links, decision later. No renumbering proposed.

---

### en/arch/simulator/index.mdx  (lines 90)  -- FATE: EDIT
Reason: clean design overview; only the status aside's future item and one stale `run --brain SOCKET` sentence.
Sections:
  - (intro + Status aside, L12-20) -- edit (future item; "runs hosted against a brain over the wire" now only `--check`)
  - §1 `#sec-1` Why the real game -- keep
  - §2 `#sec-2` What a worker does -- keep
  - §3 `#sec-3` The picture -- edit (L62)
  - §4 `#sec-4` The code -- keep
  - §5 `#sec-5` This chapter -- keep (table row for worlds says "run worlds": future, see below)
Findings:
  - L7 frontmatter badge "partly built" -- future/status; keep or drop with the decision on run worlds.
  - L18 future "Planned: run worlds, which would let a NoSL search look past..." -> list as future; drop if run worlds go.
  - L18 stale "runs hosted against a brain over the wire" -> code: `run` has no `--brain` since the mod starts its own
    brain (RunCommand.cs:35-36 options; BrainCommand serves stdin/stdout). Rewrite: "...and checked over the wire (`run --check`)".
  - L62 stale "With `run --brain SOCKET` the brain is a separate process instead, the same one the mod talks to"
    -> rewrite: "`brain` is the same Harness as a process of its own, which the mod starts and talks to on its stdin and stdout ([Whole runs §7])".
  - L86 future "the SL world, run worlds" (table) -> drop "run worlds" if §8 of worlds goes.
Inbound links to deleted/renumbered sections: none (nothing deleted).
Figures/data: none.
Twins: match.
Future/next content: L7 badge, L18 "Planned: run worlds", L86 "run worlds".
Rough size of the rewrite: ~3 lines rewritten.

---

### en/arch/simulator/headless.mdx  (lines 114)  -- FATE: EDIT
Reason: present-tense design page; four history/untracked phrases.
Sections:
  - §1 `#sec-1` The model layer and the nodes -- keep
  - §2 `#sec-2` Boot -- keep
  - §3 `#sec-3` The native stub -- keep
  - §4 `#sec-4` The patches -- edit (L51)
  - §5 `#sec-5` TestMode -- edit (L73)
  - §6 `#sec-6` The JIT -- edit (L86)
  - §7 `#sec-7` The Pump: one thread, one order -- keep
  - §8 `#sec-8` Game errors and logs -- edit (L112)
Findings:
  - L51 history "as SapphireSeed's planting (rewritten by the event transpiler) once did" -> rewrite present:
    "...could inline one of the small `CardSelectCmd` entries and bypass its patch; SapphireSeed's planting, which the
    event transpiler rewrites, is such a method." (keeps the rationale, drops the bug story)
  - L73 untracked "(from the local audit `tmp/notes/testmode-audit.md`)" -> drop the parenthesis.
  - L86 history/story "It happened after a few thousand combats, when `RunManager.WriteReplay` ran unpatched.
    Tiering was also twice as slow on short runs" -> rewrite present: "A rejit can silently drop a Harmony patch...;
    and Harmony's JIT hook makes every extra compile expensive, so tiering is slower on short runs." Drop the
    WriteReplay incident.
  - L112 history "The one fixed so far:" -> "The group holds one fix:" (the PopCardAfterEarlyReturn text is a current fact).
  - (ok) L37 "Booting takes about 1.2 s", L63 "about 75 dispatches per action", L88 "about 20% more combats per second":
    current measured facts, may stay.
Inbound links to deleted/renumbered sections: none.
Figures/data: PumpDemo -> clean (no dates, hashes or versions; "as Amalgamator's event code does unpatched" is a design fact).
Twins: match (both carry `tmp/notes/testmode-audit.md` at L73).
Future/next content: none.
Rough size of the rewrite: 0 removed / 4 sentences rewritten.

---

### en/arch/simulator/combat.mdx  (lines 135)  -- FATE: REWRITE (§5 tail, §6, §7); rest EDIT
Reason: the design (§1-§5) is current; the measurement parts are dated experiments with before/after columns, a
commit hash, a generation name and a stale profile.
Sections:
  - §1 `#sec-1` A combat session -- keep
  - §2 `#sec-2` Snapshots are recipes -- edit (L47 date, L51 future)
  - §3 `#sec-3` Determinize -- edit (L62 "yet")
  - §4 `#sec-4` Hidden state and transpositions -- keep
  - §5 `#sec-5` CombatBuilder: a combat from an observation -- edit (L82, L83, L88, L90)
  - §6 `#sec-6` Fidelity: how far a built combat can be trusted -- rewrite (experiment with before/after columns)
  - §7 `#sec-7` Speed -- rewrite (dates, "an earlier combat net", hash, stale profile)
Findings (every history/date/version/future hit on the page):
  - L47 date "Measured on 2026-10-26:" -> drop the date; keep "a restore costs 0.28 ms plus 0.073 ms per replayed action".
  - L51 future "and is a lever kept for later ([§7](#sec-7))" -> drop or "is the alternative ([Reference §8])".
  - L59 wording (false positive) "so the old numbers would leak the true order" -> optional: "the numbers they had would leak..."
    (not history; only to keep grep-based checks quiet).
  - L62 future "Not modelled yet:" -> "Not modelled:" (CombatSession.cs:241-242 still says "Not yet modeled"; code comment, not docs).
  - L82 history+version+story "which failed one run in 165 of generation 1's self-play before allies were built" -> drop the clause;
    keep "Any character can summon Osty with a card from another character's pool (`KALEIDOSCOPE` offers those)."
  - L83 history/experiment "Clamping the card's own cost at 0 failed one run in 452." -> drop (the rule "may be below 0" stays).
  - L88 status "they have never been checked against observations from the real game" -> present: "they are not checked
    against observations from the real game" (also listed as future/open; status.mdx:46 says the same).
  - L90 date+story "On 2026-10-27 every check passed: Silent 60 of 60 builds, Ironclad 58 of 58, ... 57, 57 and 58."
    -> drop; replace with the requirement: "`selftest` fails if any build does not show its observation."
  - L94 experiment "At every k-th decision of a set of combats, the real combat played on ... was counted" (past-tense
    method of an ad hoc measurement; no command in the repo reproduces it) -> rewrite as a present-tense claim, or keep
    one line of method in present tense ("Measured by playing...").
  - L96 date "(2026-10-27)" -> drop.
  - L98-102 history table header "without the record → with it" and "97.4% with stuns' follow-ups" -> keep only the
    current column (with the record and stuns' follow-ups): act 1 weak+regular 97.4%, act 2 99.0%, act 1 boss 96.5%,
    act 1 elite 99.0%; or reduce the whole table to one sentence: "Within the decision's turn at most 1% of
    observations differ; after it, 1-3.5% differ, from counters inside monsters." Decide whether a number measured on an
    older builder may stay at all (rule 6: only numbers of the CURRENT system).
  - L105 story "No combat was built wrong, and no action was illegal in a built one." -> present: "A built combat ...";
    L105 history "A stunned `CORPSE_SLUG` (another slug died) went on with the wrong move until stuns took their follow-up
    from the record." -> drop the sentence.
  - L109 date "Measured on 2026-10-26, a Release build..." -> "Measured on a Release build..." (drop the date).
  - L129 date+version "(2026-10-27: NoSL worlds built from observations, an earlier combat net, 4 runs of 8 workers each
    on 16 threads)" -> drop date and "an earlier combat net".
  - L129 stale profile: the breakdown includes "comparing the build with the observation 10%", which a restore no longer
    does (SimWorlds.cs:41-48 checks once; CombatSession.cs:166 only with `check`). The numbers describe an older build.
    -> drop the percentages or re-measure; at most keep "a worker searches most of the time; restoring worlds is the
    largest share".
  - L129 hash+history+story "A restore no longer compares the build, which the request's first build has checked
    (commit `5032064`): the champion's runs are the same, 7% faster under that load." -> rewrite present:
    "A restore does not compare the build with the observation; the request's first build is checked once
    ([Worlds §2])." Drop the hash and "7% faster".
  - L135 untracked/environment "(needs a profiler, `perf`, not installed)" -> drop "not installed" (a fact about one machine).
  - L135 future "and would be needed for AlphaZero-style search. Whether to copy or replay is decided once profiles show
    that restoring dominates" -> future list; keep the levers as design options in present tense or move to reference §8.
  - (ok) L31, L45 `<Badge text="verified">`: fine. L118 "Short runs (200 combats) take about 6.7 ms per combat",
    L124-127 branching table, L131 "8 physical cores ... about 160 MB each": measured facts, keep without dates.
Inbound links to sections being rewritten (anchors kept; check their claims after the rewrite):
  - `combat/#sec-6` <- arch/environments/information.mdx:144, arch/pimc/index.mdx:65 (repeats the measurement method),
    arch/pimc/index.mdx:76 ("All three are fixed" — a fix story that §6 does not even contain), arch/simulator/worlds.mdx:36
    (repeats "at most 1% ... (2026-10-27)"), status.mdx:18 (says the approximations are listed in §6; they are in §5),
    status.mdx:46.
  - `combat/#sec-7` <- arch/combat-brain/shared.mdx:65 (repeats the stale 48%/38%/7%/16%/5% profile verbatim: must change
    with L129), arch/learning/reference.mdx:23 (1,860 vs 275 combats/s: matches Scaling), arch/simulator/pool.mdx:92,
    arch/simulator/reference.mdx:116.
Figures/data:
  - RestoreCost -> caption dates "(2026-10-26)" and "(2026-10-28)" in all three languages (L58-60) -> drop dates;
    code comment L2-3 has the same dates (not visible; optional).
  - Scaling -> caption date "2026-10-26" (L116-118, three languages) -> drop; comment L2 same date (optional). Data clean.
Twins: match (both carry `5032064`, the 5 dates, "第 1 代" for "generation 1").
Future/next content: L51 copy-vs-replay lever; L62 "Not modelled yet"; L88 "never been checked against the real game";
  L135 levers (per-hook listener filter, deep copy, AlphaZero-style search, "decided once profiles show...").
Rough size of the rewrite: ~6 sentences removed (L82, L83, L90, L105b, L129 tail, L135 "not installed"); §6 table +
  L94/L105 rewritten (~12 lines); L129 rewritten; 4 dates dropped. About 25 of 135 lines touched.

---

### en/arch/simulator/runs.mdx  (lines 208)  -- FATE: EDIT (§7 REWRITE for a code change)
Reason: present-tense driver description; a dated determinism batch, a figure from an earlier champion, and §7
describes `run --brain SOCKET` / `brain --socket`, which the code no longer has.
Sections:
  - §1 `#sec-1` A run as a loop -- keep
  - §2 `#sec-2` Starting a run -- keep
  - §3 `#sec-3` Step, settle, decide -- keep
  - §4 `#sec-4` Every decision screen -- edit (L111 figure intro, L115)
  - §5 `#sec-5` Who answers, and what a run reports -- keep (L151 citation drift)
  - §6 `#sec-6` Determinism -- edit (L174, L177-186, L188)
  - §7 `#sec-7` Runs against a brain -- rewrite (stale vs code, see cross-page issue 1)
Findings:
  - L111 stale+version "over the 300 SL runs of the champion's promotion report" -> the figure's data is an earlier V's
    300-run Silent report (v6-3-2, simulator 1fc5f50); the champion's reports are 100 runs per character
    (eval/champions/sl.json). Rewrite: regenerate surface-counts.json from
    eval/silent/reports/2026-11-06-sl-a0-promotion-1-100-plan-c9-2-l0-v9-2-clone-p0.json (it has per-run `surfaces`
    and `brains`), then "over the 100 SL runs of the champion's Silent report"; or say only "over 300 SL runs" and
    drop "champion's".
  - L115 story "Legitimate game behavior seen in runs at 9999 HP" -> present: "Some game behavior at 9999 HP looks like a bug
    and is not:" (content stays).
  - L174 stale "(or to the `--brain` given)" -> drop; `run` has no `--brain` option (RunCommand.cs:35-36).
  - L177 date "Measured on 2026-10-26 with 200 seeds" -> drop the date; either present the batch as a reproducible
    example ("`run --check --n 200 --env NoSL` with the random macro policy and `play` gives ...") or condense L177-182
    to the guarantee ("`--check` fails a batch on any difference").
  - L180 experiment "the same 200 seeds as 8 processes of 25 runs and as 4 of 50 gave identical lines" -> rewrite as a
    property ("how seeds are split over processes changes no run") or drop.
  - L182 story "every surface was reached, the rarest being `bundle`, in 9 of 200 runs" -> drop or keep as part of the example.
  - L184 measurement "over every node of 880 runs on five characters" -> rewrite with worlds §6 (drop the count or keep
    without date).
  - L186 "The guard is the idle-wait count above, 0 over the 200-run batch." -> "...which must be 0 in every batch."
  - L188 future/open "is open until runs in the game can be compared with runs here" -> future list (duplicated in reference §8 Q3).
  - L192-194 stale (code) "With `run --brain SOCKET`, the brain is a separate process listening at a Unix socket" and
    "`run --brain` refuses every champion option, `--log` included (`Commands/RunCommand.cs:63-64`)... `Args.cs:178-194`"
    -> rewrite: the simulator hosts runs over the wire only in `run --check` (in-process server on an abstract socket,
    RunBrains.cs:200-220); the mod is the only external host.
  - L196 stale "serving the hosts that connect, one at a time" -> "serving the host that started it, on its stdin and
    stdout, until stdin closes" (BrainCommand.cs:8-13, 24, 60).
  - L199 stale "brain [--socket PATH] ..." -> drop `--socket` from the synopsis.
  - L202 stale "`--socket`: by default `$XDG_STATE_HOME/sts2ai/brain.sock` ... (`Sts2Ai.Protocol/Wire.cs:100-104`)"
    -> drop the bullet (no socket path in Protocol any more; Wire.cs:95-110 is now WireStream).
  - L208 keep ("`run --check` uses the same server in-process ... abstract Unix socket"): still true.
  - (ok) L11/L130/L135 the example run (848 decisions, seed 0SSL7NS01MU6): a book device, no version named here; but it
    comes from the old v6-3-2 report (see SurfaceCounts), so "the example run" and the figure must stay consistent.
  - citation drift L151 "(`Commands/RunCommand.cs:146`)" -> the exit code is at :140.
Inbound links to deleted/renumbered sections: none deleted. `runs/#sec-7` is linked from index.mdx:62,
  reference.mdx:124, harness, mod and protocol pages; anchor kept.
Figures/data:
  - SurfaceCounts (also used by en/arch/game.mdx:188) -> caption (L67-69, three languages): version "an earlier V's
    SL report on promotion-1-300" and hash "simulator at ${data.source.commit}" (= `1fc5f50`); stale label L26
    "p0 in combat" (the prompt brain is `first` / `FirstPicks` now: eval/champions/sl.json "combat prompts first").
  - data/book/surface-counts.json -> `source.commit: "1fc5f50"` (hash), `source.report` path with date and v6-3-2
    (version/date), `source.champion` "plan c2 ... l0 v6-3-2 ... p0" (version). Regenerate from the current champion's
    report and drop `commit`; keep `report` only if it names a file of the current champion.
Twins: match.
Future/next content: L188 inline execution "open until runs in the game can be compared".
Rough size of the rewrite: §7 ~10 of 19 lines rewritten; §6 ~8 lines condensed; 3 other sentences. About 25 lines.

---

### en/arch/simulator/worlds.mdx  (lines 123)  -- FATE: EDIT (§6 REWRITE)
Reason: design is current; §6 tells the relic-state bug story with dates and before/after counts; §8 is planned.
Sections:
  - (intro) -- keep; L3 description "run worlds, still planned" -> future
  - §1 `#sec-1` What a world is -- edit (L17 "today", L27 "are planned")
  - §2 `#sec-2` NoSL combat worlds -- edit (L36 date)
  - §3 `#sec-3` The node checkpoint -- keep
  - §4 `#sec-4` Loading the clone -- keep
  - §5 `#sec-5` The seal -- keep
  - §6 `#sec-6` What the save drops, and the checks -- rewrite to facts
  - §7 `#sec-7` The SL world -- edit (L112 date)
  - §8 `#sec-8` Run worlds (planned) -- future; keep/delete decided later
Findings:
  - L3 future "run worlds, still planned" -> drop with §8, or keep.
  - L17 future "Every world today is a **combat world**" -> "Every world is a combat world".
  - L27 future "Worlds of a whole run ... are planned ([§8](#sec-8))" -> future list.
  - L36 date "(2026-10-27)" -> drop; the "at most 1%" claim follows whatever combat §6 keeps.
  - L95 history/story "Every clone after that showed the relic as normal. The SL planner's steps then met observations
    they did not expect, and each later combat decision was planned again: 148 and 120 replans in 2 of 40 SL runs"
    -> rewrite as rationale: "Loaded from the save alone, a clone would show such a relic as new, and the SL planner would
    meet observations it did not expect."
  - L97 date "(2026-10-28)" -> drop.
  - L99 date+history "(2026-10-28). Before the checkpoint carried the relic fields, 35 of 7,335 nodes differed in relic
    state." -> drop the date and the "Before..." sentence; the 9,747-node count may stay as a measured fact without date,
    or go (it is a one-off batch).
  - L101 measurement "8-11 ms ... 25-38 ms (8.8 s at most...)" -> keep, tied to the check; no date.
  - L112 date "(2026-10-28)" -> drop.
  - L116-123 §8 future (whole section).
Inbound links to sections being rewritten / possibly deleted:
  - `worlds/#sec-6` <- arch/environments/information.mdx:123 (tells the same bug story: "the clone then no longer
    matched"), arch/simulator/runs.mdx:184, arch/sl-planner/index.mdx:73 (story: "The checkpoint now carries..."),
    index.mdx:38 (fine: design example).
  - `worlds/#sec-8` (if deleted) <- arch/environments/worlds.mdx:16, arch/macro-brain/next.mdx:22,
    arch/macro-brain/search.mdx:11, arch/protocol/worlds.mdx:57, arch/simulator/index.mdx:18, arch/simulator/pool.mdx:96,
    arch/simulator/reference.mdx:205.
Figures/data: WorldTurns -> clean (illustrative; uses the 0.28/0.073 model, no dates).
Twins: match (1x 2026-10-27, 3x 2026-10-28 in each).
Future/next content: L3, L17, L27, §8 Run worlds (L114-123).
Rough size of the rewrite: §6 3 sentences rewritten/removed; 4 dates; 2 "today/planned" phrases. ~10 lines; §8 (10 lines)
  if dropped.

---

### en/arch/simulator/pool.mdx  (lines 96)  -- FATE: EDIT
Reason: present-tense description of the local worker-process pool; one host phrase with stale numbers; §8 planned.
Which pool: this page's "pool" is the simulator's LOCAL worker-process pool: `WorkerPool` in
`src/Sts2Ai.Harness/WorkerPool.cs`, which starts copies of the SimWorker as `serve` and talks JSON lines over their
stdin/stdout on the same machine. It is NOT the hub's job/work pool (the repo's new top-level `pool/` folder: box.py,
deal.py, worker.py, hub.sh, ...). The page never names `pool/`, the hub or remote hosts except L90.
Sections:
  - §1 `#sec-1` Why other processes -- keep
  - §2 `#sec-2` The pool -- keep
  - §3 `#sec-3` The three requests -- keep
  - §4 `#sec-4` The worker: `serve` -- keep
  - §5 `#sec-5` Self-contained requests -- keep (L66 measurement fine)
  - §6 `#sec-6` Failures -- keep (L81 "Two gaps remain" -> "Two gaps:"; both gaps verified in WorkerPool.cs:76-84, 110-115)
  - §7 `#sec-7` Speed -- edit (L90)
  - §8 `#sec-8` Planned: worlds that stay in a worker -- future; keep/delete decided later
Findings:
  - L81 wording "Two gaps remain." -> "Two gaps:" (present; both still true in code).
  - L89 measurement of unknown age "A whole run with `pimc` takes about 19.9 s, with two runs at a time on 8 workers each"
    -> keep only if it is the current NoSL champion's speed; eval/champions/nosl.json has no reports, so it cannot be
    checked. Consider dropping.
  - L90 host+stale "over the 300 runs of its promotion report, 271 s a run and 489 ms a decision on average, on the
    fleet's hosts." -> rewrite without hosts and with the current report: "its reports record the speed: on the
    Silent's 100 promotion runs, 405 ms a decision and 336 s a run on average (`ms_per_decision`, `seconds_per_run` in
    eval/silent/reports/2026-11-06-...-c9-2-l0-v9-2-clone-p0.json)". Other characters: Ironclad 431 ms / 286 s, Defect
    560 / 423, Regent 1,202 / 906, Necrobinder 1,358 / 1,046. Note those runs were played on several machines, so a
    per-machine speed claim is not available; simplest: drop the sentence's "where" entirely.
  - L94-96 §8 future (whole section); L96 "Today a request is a whole search" -> part of §8.
Inbound links to sections possibly deleted:
  - `pool/#sec-8` <- arch/combat-brain/shared.mdx:71 (restates the plan), arch/protocol/worlds.mdx:124 ("Planned" badge).
  - `pool/#sec-7`: no inbound links.
Figures/data: PoolTimeline -> clean (illustrative, no dates/hashes).
Twins: match (zh-CN "集群", zh-tw "機群" at L90).
Future/next content: §8 worlds that stay in a worker (L94-96).
Rough size of the rewrite: 1-2 sentences (L89-90), 1 wording fix; §8 (3 lines) if dropped.

---

### en/arch/simulator/reference.mdx  (lines 206)  -- FATE: EDIT (§1 launcher REWRITE)
Reason: lookup page, mostly facts; §1 launcher is local/fleet-only; §4 `run`/`brain` rows are stale vs code; dated
selftest result; many drift-prone counts.
Sections:
  - (intro L10) -- edit
  - §1 `#sec-1` Build and run -- rewrite the launcher paragraph; edit two table rows
  - §2 `#sec-2` The command line -- keep
  - §3 `#sec-3` Option groups -- keep
  - §4 `#sec-4` Commands -- edit (`run`, `brain`, `selftest` rows)
  - §5 `#sec-5` `selftest` -- edit (L145, L159)
  - §6 `#sec-6` `run --check` -- edit (L168, L172)
  - §7 `#sec-7` The xUnit tests -- keep (drift-prone counts, see below)
  - §8 `#sec-8` Open questions -- future; keep/delete decided later
Findings (history/host/untracked/stale):
  - L10 history "Everything here was checked against the code at the commit this page was written at" -> "Where this page
    and the code disagree, the code is right."
  - L18 untracked "default `tmp/gamebin/v0.111.0`" -> it IS the code's default (src/GameReferences.props:9), but `tmp/` is
    not in git. Rewrite: "default `tmp/gamebin/v0.111.0` under the repository (not in git: a copy of the game's folder that
    holds `sts2.dll`); `-p:Sts2BinDir=DIR` points elsewhere". Optionally change the code to require the property.
  - L20 history "(seen after a few thousand combats); it was also 2× slower on short runs" -> present: "a Harmony patch can
    silently drop off, and Harmony's JIT hook makes each extra compile slow".
  - L26 host+untracked+skill (whole paragraph): "Batches and the fleet run a build of a fixed commit ... `tmp/build.sh REV`
    (a local script) ... `tmp/builds/REV/sim.sh` ... `HOME` pointed at `tmp/home` ... brain.sock ... The fleet's hosts get
    the same build and a launcher of their own (the fleet skill, `.claude/skills/fleet/SKILL.md`)." -> replace with the
    public, present-tense version proposed at the end of this block.
  - L117 `selftest` options: lists `--combat-net FILE (required)` and "pool"; the command also takes the leaf group
    (`--value`, which `--combat-net` needs: LeafChoice.cs:16). -> "leaf (`--combat-net` required, with `--value`)".
  - L123 stale `run` row: "`--brain SOCKET`" and "Refused: ... any champion option or `--log` with `--brain`" -> drop both
    (RunCommand.cs:35-36 has no `--brain`).
  - L124 stale `brain` row: "`--socket PATH`" and "serving hosts over a Unix socket" -> "`--log FILE`, `--wire FILE`,
    `--sl-policy` (`plan`); champion, champion file | the brain alone, serving the host that started it on stdin and
    stdout ([Whole runs §7])".
  - L145 drift/inexact "(so 60 build checks at the default `--n 20`)" -> "up to 60": cut points are drawn with
    `.Distinct()` (SelfTest.cs:85), which is why Ironclad had 58.
  - L159 date+story "Results of 2026-10-27: every check passed, with 60 of 60 builds for the Silent, 58 of 58 ..." -> drop;
    keep "At the end it prints draw-order statistics, which are not a check."
  - L168 stale "to an in-process brain or the `--brain` given" -> "to an in-process brain".
  - L172 stale "With `--brain`, the brain's decision log is not in this process, so the log comparison compares two empty
    logs." -> drop.
  - L202-206 §8 future (three open questions).
Drift-prone facts (true today, will change; not history):
  - L176 "21 classes and 105 test methods" (verified: 21 `*Tests.cs`, 105 `[Fact]/[Theory]`).
  - L184 "16 tests" (LoopSearchTests: 16), L192 "15 tests" (SlPlannerTests: 15), L197 "8 tests" (GateTests: 8): verified.
  - L183 `CombatNetTests` "reads c9-0's test states" and L190 `RunNetTests` "v9-0's test runs": file names in git
    (CombatNetTests.cs:43, RunNetTests.cs:35); allowed, but change with the weights.
  - L78 effort presets "low beam 8, easy beam 2, loss plans 3; medium beam 8, easy 4, loss plans 3; high beam 8; max beam 16;
    all 300 nodes" (verified CombatBrain.cs:20-27).
  - L139 "(the order is `Checks/SelfTest.cs:73-147`)" and many `Args.cs:NN-MM` ranges: line ranges drift with every edit.
  - L147 "(3 of 6)", "14 of 20", "`DeathBlow` 150"; L153 "535 HP, 300 nodes"; L155 "150 nodes, beam 4"; L157
    "4 worlds × 120 nodes": test parameters, drift with the tests.
  - L34 "System.CommandLine 2.0.14" (csproj): drifts with package updates.
Proposed public §1 launcher text (replaces L26; present tense, only what a reader can run from the repo):
  > **Building and running.** Build in Release and run the dll with `DOTNET_ReadyToRun=0`:
  > ```sh
  > dotnet build src/Sts2Ai.SimWorker -c Release -p:Sts2BinDir=DIR
  > DOTNET_ReadyToRun=0 dotnet src/Sts2Ai.SimWorker/bin/Release/net9.0/Sts2Ai.SimWorker.dll --help
  > ```
  > `DIR` is the folder of a v0.111.0 game install that holds `sts2.dll`. The build stamps the checkout's commit
  > (`SourceRevisionId`, `-dirty` when `src/` has changes), which run records and reports carry, so a batch whose results
  > are kept should run a build of a committed tree, not a working tree with changes. To build another commit, export
  > it (`git archive COMMIT | tar -x -C OUT`) and pass `-p:SourceRevisionId=COMMIT`: a tree exported inside a checkout is
  > otherwise stamped with that checkout's HEAD.
  (Drop every `tmp/build.sh`, `tmp/builds/REV/sim.sh`, `tmp/home`, brain.sock, fleet and skill mention. The brain.sock
  sentence is also stale in code: `brain` no longer listens on a socket.)
Inbound links to deleted/renumbered sections:
  - `reference/#sec-1` (anchor kept; its targets must follow the launcher rewrite): arch/gamebridge/index.mdx:53 (fine:
    `Sts2BinDir`), arch/mod/index.mdx:113 (`tmp/builds/REV/sim.sh`, `tmp/home`, `--socket ~/.local/state/...`: untracked
    AND stale), glossary.mdx:128 (row "`tmp/build.sh REV`, `sim.sh`": untracked; drop the row).
  - `reference/#sec-8` (if deleted) <- arch/simulator/combat.mdx:135, arch/simulator/worlds.mdx:123.
Figures/data: none.
Twins: match (both carry `.claude`, `tmp/build.sh`, `tmp/builds/REV/sim.sh`, `tmp/gamebin`, `tmp/home`, fleet x2 + 集群/機群 x2,
  `2026-10-27`, `c9-0`, `v9-0`).
Future/next content: §8 Open questions (copy or replay; run worlds; inline execution).
Rough size of the rewrite: L26 paragraph replaced (1 → ~8 lines); L159 removed; ~8 cells/sentences edited (L10, L18, L20,
  L117, L123, L124, L168, L172, L145).

---

## Cross-page issues

1. **`run --brain` / `brain --socket` are gone from the code** (commit of 2026-11-07 15:17 Taiwan, "mod: starts its own
   brain, no socket"): `run` has no `--brain` (RunCommand.cs:35-36), `brain` serves the host that started it on stdin and
   stdout (BrainCommand.cs:8-13, 24-31, 60), and `Sts2Ai.Protocol/Wire.cs` no longer has the socket default
   (`Wire.cs:100-104` is cited by runs.mdx:202). Only `run --check` still uses an in-process abstract socket
   (RunBrains.cs:200-220). Stale in this group: index.mdx:18, 62; runs.mdx:174, 192-202; reference.mdx:26, 123, 124, 168, 172.
   Outside it: arch/harness/index.mdx:69 ("`run --brain SOCKET` and the mod"), arch/protocol/wire.mdx:14 ("A Unix domain
   socket ... By default `$XDG_...`") and :133 ("`run --brain`"), arch/mod/index.mdx:47 ("brain.sock" in the mermaid),
   :103, :113, arch/mod/loop.mdx:138 ("The socket: ... brain.sock"), glossary.mdx:37.
2. **SurfaceCounts is fed by an old champion** (v6-3-2, simulator `1fc5f50`, 300 Silent runs on promotion-1-300) while
   runs.mdx:111 calls it "the champion's promotion report"; the same figure is on arch/game.mdx:188. Its "p0 in combat"
   label is stale (`first`). The current champion's Silent report has per-run `surfaces` and `brains`, enough to
   regenerate it. The book's example run (0SSL7NS01MU6, 848 decisions, runs.mdx:11, 130, 135) comes from that old report.
3. **Champion name mismatch**: eval/champions/sl.json says "combat prompts first", but the 2026-11-06 reports it points
   to carry "combat prompts p0" in `config.champion` (and the report file names end in `-p0`). Not a docs edit, but a
   reader comparing the two will see it.
4. **Stale worker profile duplicated**: combat.mdx:129 and arch/combat-brain/shared.mdx:65 carry the same 48/38/7/16/5%
   breakdown, measured before restores stopped comparing the build; rewrite both together.
5. **The relic-state bug story is told three times** (worlds.mdx:95-99, arch/environments/information.mdx:123,
   arch/sl-planner/index.mdx:73 "The checkpoint now carries ... In the 300 runs ..."): rewrite all three to the design
   rationale only.
6. **Fidelity measurement is repeated** at combat.mdx:94-105, arch/pimc/index.mdx:65 (method) and :76 ("All three are
   fixed": Glass orb, Sloth — a fix story whose target §6 does not contain), worlds.mdx:36. status.mdx:18 points to
   combat §6 for "the builder's approximations", which are in §5 (combat.mdx:88).
7. **Three meanings of "pool"**: the encounter pool (reference §3 "**Pool** (`Args.cs:61-96`)", `selftest`/`bench` "pool"),
   the worker pool (pool.mdx, `WorkerPool`), and the repo's new top-level `pool/` folder (the hub's work pool, which the
   docs must not name). If `pool/` ships in the public repo, a reader of "The worker pool" page will look there first;
   consider renaming the folder or never pointing at it (no docs page does today; only arch/learning/reference.mdx:17
   names a "storage hub" in a mermaid label — host category, another group's page).
8. **Future sections cross-reference each other**: worlds §8 (run worlds) <-> pool §8 <-> reference §8 Q2 <-> index status
   aside <-> combat §7 levers <-> environments/worlds.mdx:16, macro-brain/next.mdx:22, macro-brain/search.mdx:11,
   protocol/worlds.mdx:57/124, combat-brain/shared.mdx:71. Whatever is decided for run worlds should be applied to all of
   them at once.
9. **Citation drift seen**: runs.mdx:151 `Commands/RunCommand.cs:146` (exit code now at :140). Others spot-checked
   (HeadlessGame, GodotNativeStub, HeadlessPatches, Engine, RuleFidelity, Text, GameQuirks, Pump, CombatSession :73,
   :157-161, :241-242, RunSession :149, :186, :223, :226-228, :245, :298, :599, csproj :12-20, :36-48, GameReferences.props,
   Program.cs, Cli.cs, CombatBrain.cs:20-27, WorkerPool.cs) are within a line or two.
10. **The word "host"** has a protocol meaning on these pages (the process that holds the true run: "host worker", "SL host",
    "Both hosts take checkpoints" = simulator and mod). That is not the forbidden "remote host" sense; a mechanical
    scrub of "host" would break index §2-§3, runs §7, worlds §3, pool §4-§5.
