# DONE F2: GameBridge + Mod

Pages: arch/gamebridge/{index,observe,actions}.mdx, arch/mod/{index,loop,sl-record,auto}.mdx, each in en, zh-CN (root)
and zh-tw. Written against the code at HEAD a0071e4 (after 70e9a25: one bundled brain for both environments, every SL
decision of an unclonable node held back, `Record` captures). No section renumbered or deleted; every triplet has the
same line count (en = zh-CN = zh-tw: index 208, loop 159, sl-record 112, auto 169, gamebridge 91/140/119). Not built,
not committed.

## Per page

**gamebridge/index** (EDIT, plan said KEEP)
- §3 "Who uses it", the mod's `start` cell: was "never: the mod advises and the player acts" (false: `Executor.cs`
  calls `CombatActions.Start`, `PotionActions.Start`, `ShopActions.Start`, `TreasureActions.Start`,
  `CrystalSphereActions.Start`). Now: only when the player asks the AI to play, GameBridge's start where that is the
  click's call, else the screen's own handler or awaited task; it records every step, the player's or the AI's.
- §6 Tests, `clones` row: the SL records are "captured in the game with the mod's `Record` setting".
- Planned §5 and the planned line under §6 left as they are (D1).

**gamebridge/observe** (EDIT)
- §6: dropped "This happened headless with SapphireSeed." (rule kept).
- Intro: the decompiled game "which is not in the repository" (FACTS).

**gamebridge/actions**: no change (clean; its §7 "The SL record" note already says the mod starts actions only when
asked).

**mod/index** (EDIT, §3-§4 REWRITE)
- description, intro: the brain it ships and starts; asks over the brain's stdin and stdout.
- Status aside: dropped the 89-of-90 story and "has not been watched through a whole real run yet"; "starts the brain it
  ships"; the switch sentence (see below); in SL the clone also values options outside combat. Planned driver/decision
  log kept.
- §1: brain started by the mod from its folder; writes settings, `brain.log` and (when asked) captures in its state
  folder.
- §2 mermaid: `BrainLink (background threads)`, edge `JSON lines / stdin / stdout`.
- §3: rewritten: the loader (plus `mods_STEAMTEST/`, checked in the decompiled `ModManager.Initialize`), "on Linux, by
  default" game folder, FileTree with `brain/` (champion.json, nets/NAME.net, linux-x64/osx-arm64/win-x64), "Building
  and installing" (`distro/build.sh [REV]` -> `out/Sts2Ai/`, `out/commit`; copy into `mods/`; the four things the script
  does with their reasons: one mod build, self-contained unsigned/untrimmed folder publish per RID with
  `brain.pubxml`, ad hoc `codesign` on a Mac (no host, no `.env` variable named), nosl.json as `brain/champion.json` and
  nets renamed `.net` because the game reads every `.json` as a manifest), manifest table (new description, 0.2.0),
  "One DLL" gains a line on `brain/` DLLs, `Host.cs:23-46` with step 3 "The brain" (`BundledBrain.Find`) and "starts no
  brain" in the guard.
- §4 retitled "The brain it ships" (plan section 4 row and the brief name this title; it is not in D4's list, so the
  lead may want to confirm). Bullets: what it runs (launcher per RID, `--workers N` = half the logical processors 1-8,
  exec bit), its environment (`brain/` working folder, `STS2_BIN_DIR`, `DOTNET_ReadyToRun=0`, `SENTRY_GODOT_LIB_PATH`
  removed, stated as a rule with its reason), the wire, its log (`brain.log` in `$XDG_STATE_HOME/sts2ai`, truncated by
  a game session's first brain, appended by restarts), when it ends (kill with workers, 2 s, start again), the champion,
  the effort (kept), Recording (`Record` -> `--wire`/`--log` in `captures/STAMP-wire.jsonl`, `STAMP-decisions.jsonl`).
  Dropped the `dotnet run ... brain --champion eval/champions/nosl.json` block, the socket, and the `tmp/builds` /
  `tmp/home` / `--socket` bullet.
- §5: settings "beside the brain's log" (`$XDG_STATE_HOME`), one sentence on `Record` (a file field with no row).
- §6: `Assist.cs:163-280`, `Host.cs:48-69`; `starting` row (launcher path or "no brain ships ..."), `offline` row (brain
  ended with exit code / last stderr line / log path, could not start, none ships); "No SL clone of this room" row now
  "SL, and the node's record is missing or broken: no SL decision is asked"; reasons line without "socket error".
- §7: 17 files; BundledBrain.cs 104, BrainLink.cs 110, Host.cs 70, Assist.cs 488, CombatMark 139, Settings 96 (with
  `Settings.Dir`), csproj 28 (counted with `wc -l` at a0071e4).
- §8: sl-record line -> "the round trip that checks it".

**mod/loop** (EDIT)
- description: "the brain's pipes on background threads"; §1 `Host.cs:45`, `Host.cs:48-69`, pipes not socket, mermaid
  edge `stdin / stdout` (STS2MCP credit kept).
- §2: `Assist.cs:163-280`; inbox "the brain started or ended"; "No brain"; step 7: any SL decision in a node without its
  clone stops there.
- §3: `Assist.cs:248-249`, `261-267`, `428-433`, `48`; "a host's decisions", ids per run.
- §5: in SL without the clone no decision is asked, prompts included.
- §8 rewritten: the pipes (child process, stderr to `brain.log`, state folder), Starting (`BundledBrain.Run`, writer
  thread, kill + 2 s + restart, "says so every 2 s" without a brain), Sending; the pressure-vessel paragraph deleted;
  run table and paragraph say "brain ends / started again" instead of connection/reconnect; `Assist.cs:113-127`,
  `205-209`, `409-477`; "One brain for both environments" restated from `BrainCommand.Champions`; "What the brain prints"
  `serving stdin and stdout`, on stderr into `brain.log`, `BrainCommand.cs:59-64`.

**mod/sl-record** (EDIT)
- description and intro: the round trip that checks it; the clone also values options outside combat.
- §3: dropped "This settled the open question ...".
- §6 "What a broken node means": rewritten for 70e9a25: no SL decision is asked (`Assist.cs:238-246`), with the
  reason (planner and L0 both play on the clone; else the brain refuses the run, `Dispatcher.cs:189-195`, or values
  options on the record's last state). The old "gap" sentence is gone because the gap is closed in code.
- §7: `Assist.cs:398-407`; "after a new `hello` (a new run, a switch, or a brain started again)".
- §8: title kept; "Results" and "The SL planner's wait" deleted (455 decisions, 89/90, the earlier capture, the dated
  `tmp/assist/` capture, the timings). The round trip paragraph kept, plus how a capture is made (`Record` in the
  settings file -> `captures/`; `--wire FILE` on a brain started by hand) and "a clone that fails says why".

**mod/auto** (EDIT)
- intro "still planned" -> "planned"; §1: "the map" moved from GameBridge's start to the screen's own handler (code:
  `NMapScreen.OnMapPointSelectedLocally`, matching the page's own table); §2 "when the brain ends"; planned driver block
  kept, "the user's own value" -> "the player's".
- §3: "What the mod writes" (no "today"), "on Linux, by default"; log line `assist mode, ENV, brain: LAUNCHER`; the files
  paragraph: settings, `brain.log`, and with `Record` the `captures/` pair from `--log`/`--wire`; the 2026-10-28
  `tmp/assist/` capture dropped; "Nothing reads `NumReloads`".
- §4: "Nothing leaves the machine: the brain is the mod's own child process, and the pipes ...".
- §5: SapphireSeed and "a few thousand combats" stories dropped (rules kept); "The panel shows no warning."; open
  question 1 (was 2).
- §6: "The mod opens no port: it talks to the brain it started over the brain's stdin and stdout."
- §7: SL round trip row "a `Record` capture (`brain --wire`) and `clones` | built" (was "built and run"); "checked
  against the decompiled game".
- §8: settled question 1 deleted; the JIT question is now 1.

## Sentences that depend on the NoSL switch / the bundled champion (as of 70e9a25 + a0071e4)

- mod/index Status aside: "A switch on the panel picks SL or NoSL, and the one brain the mod ships plays both" and
  "assist mode on every surface, in both environments".
- mod/index §3, fourth build bullet: "copies the NoSL champion's file, `eval/champions/nosl.json`, as
  `brain/champion.json` (the brain plays it in both environments)".
- mod/index §4 "The champion" bullet (whole bullet, incl. "NoSL is not trained for now").
- mod/loop §8 "One brain for both environments" paragraph.
- mod/sl-record §6 "What a broken node means" (holding back every SL decision).
- zh-CN / zh-tw twins of the same sentences.

## Left on purpose

- Planned content (D1): auto §2 driver block, §3 planned decision log and R3 = R4, §4 "the planned driver would refuse
  any multiplayer lobby", §5 heartbeat and launch flag, §7 planned rows; gamebridge/index §5 and its planned line in §6.
- zh-CN mermaid labels in mod/index §2 and loop §1 stay Chinese as they were (only the changed parts edited).
- zh-tw manifest description stays untranslated, as it was.
- Line citations into `NodeRecord.cs` and `Screens.cs` were not changed (their files did not change).

## Figures step

- mod/index §2 (floor 43, act 3, 24 of 66 HP, `map:4,10` / `map:6,10`) is the book's example run; it waits for the
  regenerated example play. TickDemo needs nothing.

## Cross-page links / text others must change

- output.mdx:87 "beside the brain's socket" -> beside the brain's log; output.mdx:181 "ran in the game in the session
  of 2026-10-28" linked to sl-record §8, which has no results now.
- environments/information.mdx:153 ("Today `brain --wire FILE` logs ...") and :138 (NumReloads, links auto §3): the
  capture is now the mod's `Record` setting.
- learning/reference.mdx:48 `assist/DATE-SUBJECT/` "the logs of the assist mode in the real game" (links auto §3): the
  mod's captures live in `$XDG_STATE_HOME/sts2ai/captures/`, not under `data/`.
- protocol/index.mdx:100, simulator/reference.mdx:126, simulator/runs.mdx:205: `clones` over a `brain --wire` log
  still matches sl-record §8; may add "or a `Record` capture".
- harness/index.mdx:144 and harness/routing §1: the afterstates set by the host's environment (`BrainCommand.Champions`)
  is now part of how `brain` builds its two champions.
- No inbound link text uses mod §4's old title ("Running it with a brain"); anchors unchanged.

## Code vs FACTS / audit

- Audit's line counts (BundledBrain 92, Assist 486, Settings 92) are pre-70e9a25; at a0071e4 they are 104, 488, 96.
  All `Assist.cs` citations in my pages moved by +1/+2 lines (recomputed: 163-280, 113-127, 205-209, 409-477, 248-249,
  261-267, 428-433, 48, 238-246, 398-407).
- Audit cross-page issues 2, 3 and 4 (NoSL switch errors, broken node reaching the Harness, no in-game capture) are
  fixed in code by 70e9a25; the pages state the fixed design.
- gamebridge/index §3 said the mod never starts actions; the code's executor does (fixed). auto §1 listed the map
  under GameBridge's start; the code clicks through the map screen (fixed).
- `Settings.cs`'s summary calls `$XDG_STATE_HOME/sts2ai` "the mod's own folder", easy to confuse with
  `mods/Sts2Ai/`; the pages call it "the mod's state folder".
- `distro/build.sh` (outside docs) still cites `plans/...` in its header, builds against `tmp/gamebin/v0.111.0`
  and `tmp/item/REV`, and needs `STS2_MAC` in `.env`; the pages say only "against the game's DLLs as the simulator does"
  and "on a Mac".
- 70e9a25's message says the bundled brain, the NoSL switch and `Record` were not run in the game; the pages describe
  them as built and claim no in-game run.
