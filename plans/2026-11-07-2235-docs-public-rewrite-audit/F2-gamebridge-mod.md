# F2 audit: GameBridge + Mod (English pages, read in full)

Headline: the GameBridge pages are almost clean. The mod pages carry little history, but they are **stale against the
code at HEAD**. Since the "mod starts its own brain" change (`src/Sts2Ai.Mod/BundledBrain.cs`), the mod no longer
connects to a socket and the player no longer starts a brain: the mod starts the brain it ships, in `brain/RID/`, as a
child process and talks to it over stdin/stdout. The `-t:Install` target is gone, and `distro/build.sh` makes the
Workshop item in `out/Sts2Ai/`. Every page in this group still tells the socket / `dotnet build -t:Install` /
`brain --champion eval/champions/nosl.json` story. In a rewrite to "the system as it is now", these pages need
rewriting, not only cutting. Twins (zh-CN, zh-tw) match English on every risky token, every code ref and every anchor,
so they carry the same staleness.

---

### en/arch/gamebridge/index.mdx  (lines 91)  -- FATE: EDIT
Reason: clean of history and hashes. Only the planned content (event stream, mod check) needs the later future decision.
Sections:
  - Status Aside (L15-19) -- edit only if §5 goes ("Planned: the event stream (§5)")
  - §1 `#sec-1` Three jobs -- keep
  - §2 `#sec-2` The rules -- keep
  - §3 `#sec-3` Who uses it -- keep
  - §4 `#sec-4` Following the game -- keep
  - §5 `#sec-5` Planned: the event stream -- future (whole section; keep/delete decided later)
  - §6 `#sec-6` Tests -- keep; L79 is future
  - §7 `#sec-7` This chapter -- keep (open questions are current questions, fine)
Findings:
  - L17 future "Planned: the event stream of the public history ([§5](#sec-5))" -> goes with §5 if §5 goes
  - L59-68 future: the whole §5 ("would come from the game's own hooks", Badge inferred)
  - L79 future "Planned: a check in the mod that every enabled control..." -> future list
Inbound links to deleted/renumbered sections: if §5 is deleted, §6->§5 and §7->§6. Inbound to `gamebridge/#sec-5`: `en/arch/protocol/observation.mdx:178` ("GameBridge would produce them from the game's hooks ([GameBridge §5])"), plus the self-link at L17. No inbound links to `gamebridge/#sec-6` or `#sec-7`.
Figures/data: none (Starlight Aside, Badge only).
Twins: match.
Future/next content: §5 (event stream, L59-68); L17; L79 (mod control check and same-seed R3=R4 check).
Rough size of the rewrite: 0 lines if the future content stays; about 12 removed if it goes.

### en/arch/gamebridge/observe.mdx  (lines 140)  -- FATE: EDIT
Reason: clean except one bug anecdote in §6.
Sections:
  - §1 `#sec-1` The entry points -- keep
  - §2 `#sec-2` The run and its map -- keep
  - §3 `#sec-3` Cards, powers, relics and intents -- keep
  - §4 `#sec-4` A combat -- keep
  - §5 `#sec-5` Card prompts in canonical order -- keep
  - §6 `#sec-6` The prompt's context -- edit (one sentence)
  - §7 `#sec-7` The SL checkpoint -- keep
  - §8 `#sec-8` Hidden-state keys -- keep
Findings:
  - L115 history/story "This happened headless with SapphireSeed." -> drop the sentence; the rationale before it ("the inlined copy bypasses the prefix") stays
  - (not history) L103 "since the node's entry" means "counted from", fine; L64 "goes stale after a downgrade" is rationale, fine
Inbound links: none affected.
Figures/data: none.
Twins: match.
Future/next content: none.
Rough size of the rewrite: 1 sentence removed.

### en/arch/gamebridge/actions.mdx  (lines 119)  -- FATE: KEEP
Reason: describes current classes, legal rules and entry points. No hash, date, version, host, plan or story.
Sections:
  - §1 `#sec-1` Combat -- keep
  - §2 `#sec-2` Map -- keep
  - §3 `#sec-3` Rewards, card rewards and bundles -- keep
  - §4 `#sec-4` Events and the crystal sphere -- keep
  - §5 `#sec-5` Shop, rest site and treasure -- keep
  - §6 `#sec-6` The potion belt -- keep
  - §7 `#sec-7` Naming what an action acts on -- keep
Findings: none. ("none is used in this game version" at L46 is a current fact; L52 "test mode would always take the first pack" is rationale.)
Inbound links: n/a.
Figures/data: none.
Twins: match.
Future/next content: none.
Rough size of the rewrite: 0.

### en/arch/mod/index.mdx  (lines 198)  -- FATE: REWRITE
Reason: §3 (install) and §4 (running with a brain) describe a removed design (socket, Install target, a brain the player starts), and the code table is out of date. Also one untracked `tmp/` pointer and one story in the Status box.
Sections:
  - Status Aside (L15-19) -- edit (story + future)
  - §1 `#sec-1` What the mod does, and what it does not -- edit (socket, "brain the player starts", "beside the brain's socket")
  - §2 `#sec-2` One decision, from the screen to the panel -- edit (mermaid `brain.sock`)
  - §3 `#sec-3` Installing and loading -- rewrite (install = `distro/build.sh` item copied into `mods/`; FileTree gains `brain/`; manifest values)
  - §4 `#sec-4` Running it with a brain -- rewrite whole (the bundled brain: what starts it, its args, env, log, restart)
  - §5 `#sec-5` Controls and settings -- edit (L129 "beside the brain's socket")
  - §6 `#sec-6` What the panel says -- edit (L154 second line; Host.cs ref)
  - §7 `#sec-7` The code -- edit (file list and line counts)
  - §8 `#sec-8` This chapter -- edit (L197 "how it was checked in the game"; L198 "planned auto-run driver" is future)
Findings:
  - L3 stale "how to run it with a brain" -> "the brain it ships and starts"
  - L13 stale "asks the brain over a local socket" -> "asks the brain it started, over the brain's stdin and stdout"
  - L17 story "in the real game, 89 of 90 SL decisions replayed ... the other one's cause is fixed" -> drop
  - L17 status "The mod has not been watched through a whole real run yet." -> keep or move to status.mdx (perishable)
  - L17 future "Planned: playing whole runs ... (the auto-run driver) and a decision log of its own" -> future list
  - L26 stale "one `decision` message at a time over a Unix socket" -> "over the brain's stdin/stdout"
  - L33 stale "The brain is a separate process, `brain`, which the player starts" -> "which the mod starts from its own folder"
  - L34 future "the auto-run driver is planned" -> future list
  - L36 stale "It writes only its settings file, beside the brain's socket" -> settings.json and brain.log in `~/.local/state/sts2ai/` (`Settings.Dir`); the brain writes only its stderr there
  - L47 stale mermaid edge `"JSON lines<br/>brain.sock"` -> "JSON lines<br/>stdin / stdout"
  - L60, L62 wording "holds ... on this machine", "The game folder on this machine is ~/.local/share/Steam/..." -> "on Linux, by default" (public reader)
  - L64-71 stale FileTree: only Sts2Ai.json + Sts2Ai.dll -> add `brain/` (`RID/` for linux-x64, osx-arm64, win-x64; `champion.json`; `nets/*.net`)
  - L73 stale "`dotnet build ... -t:Install` ... (`Sts2Ai.Mod.csproj:23-26`; ... `Sts2GameDir`)" -> the target and property are gone. Replace with `distro/build.sh [REV]` (in git), which writes the item to `out/Sts2Ai/` (ignored; fine as "where output goes"), installed by copying into `<game>/mods/` (or `mods_STEAMTEST/`, per the csproj comment). Mention that the osx-arm64 launcher is signed ad hoc on a Mac reached by ssh, with **no address** (`STS2_MAC` in `.env` in the working tree)
  - L80 stale manifest description "Shows the AI's recommended action. Assist only: it never acts on the game." -> the current text in `src/Sts2Ai.Mod/Sts2Ai.json` ("An AI for single-player runs: ... plays it when asked. Starts its own brain ...")
  - L81 stale `version` `0.1.0` -> `0.2.0`
  - L94 stale ref `Host.cs:23-42` -> `Host.cs:23-46`; the steps omit `BundledBrain.Find` (L40 of Host.cs)
  - L98 stale "The brain link, which starts connecting on its own thread" -> "starts the bundled brain on its own thread"
  - L103 stale "The mod needs a brain listening at its socket, `~/.local/state/sts2ai/brain.sock` ... tries again every 2 s until it connects" -> it starts the brain itself and starts it again 2 s after it ends
  - L105-107 stale `dotnet run ... -- brain --champion eval/champions/nosl.json` -> the mod runs `brain --champion champion.json --workers N` (N = half the logical processors, 1-8) with working dir `brain/`, `STS2_BIN_DIR` = the folder the game loaded sts2.dll from, `DOTNET_ReadyToRun=0`, and `SENTRY_GODOT_LIB_PATH` removed. State that last point as rationale, not as the crash story: "the game's Sentry extension sets it; inherited, it makes each search worker crash at boot"
  - L109-110 stale: one brain for both environments "holds for the NoSL champion's file". The shipped champion is `eval/champions/sl.json` (`afterstates: clone`), which by L110's own words refuses NoSL hosts -> rewrite (see cross-page issue 2)
  - L112 stale "`--log FILE` ..., `--wire FILE` ..." -> the mod passes neither. These flags exist only for a `brain` started by hand, which no host can reach now
  - L113 untracked + stale "launcher of a fixed build (`tmp/builds/REV/sim.sh`) has `HOME` pointed at `tmp/home`, so it needs `--socket ...`" -> delete bullet (`--socket` no longer exists)
  - L114 stale "The brain prints a line per run" -> it prints on stderr, which the mod writes to `brain.log` beside the settings
  - L116 stale "Then start the game from Steam ... The panel shows the brain's champion once the brain has read the run's hello" -> keep the second half; the first half joins the new install text
  - L129 stale "beside the brain's socket" -> "beside the brain's log"
  - L150 stale ref `Host.cs:44-65` -> `Host.cs:48-69`
  - L154 stale "`starting` | Connecting to the brain | the socket's path" -> second line is the brain launcher's path, or "no brain ships with this copy of the mod for this platform" (`BrainLink.Where`)
  - L155 stale-ish "the connection failed or dropped" -> also "the brain ended (exit code N): last line (log: PATH)" (`BundledBrain.Run`)
  - L171 stale "16 source files" -> 17
  - L173-190 stale table: add `BundledBrain.cs` (92 lines). `BrainLink.cs` is 110 lines, not 101, and is no longer "the socket: connect, reconnect" but "starts the bundled brain, a reader and a writer thread, the inbox". `Host.cs` 66->70, `UI/CombatMark.cs` 138->139, `UI/Settings.cs` 85->92 (it also holds `Settings.Dir`)
  - L192 stale "`Sts2Ai.Mod.csproj` (33 lines)" -> 28
  - L197 "and how it was checked in the game" -> follows the sl-record §8 rewrite
  - L198 future "the planned auto-run driver" -> future list
Inbound links to deleted/renumbered sections: no section is deleted or renumbered (§3 and §4 keep their numbers; §4 could be renamed "The brain it ships"). Links into the content being rewritten: `mod/#sec-4` <- `en/arch/mod/loop.mdx:138`, `:158`. `mod/#sec-3` <- `en/arch/gamebridge/index.mdx:57` (version guard, still true).
Figures/data: none (mermaid inline, FileTree; both stale as noted).
Twins: match (same tokens, same refs, same anchors; same `tmp/builds/REV/sim.sh`, `tmp/home`).
Future/next content: L17 (auto-run driver, mod decision log), L34, L198.
Rough size of the rewrite: about 5 lines removed (L17 story, L113); about 30 lines rewritten (§3 install + FileTree + manifest rows, all of §4, §7 table rows, scattered socket mentions).

### en/arch/mod/loop.mdx  (lines 160)  -- FATE: EDIT
Reason: the history-word hits are false positives. I grepped every "since/old/earlier/before/until/was/first/again" in
the page: all describe run-time order ("the old decision's answer", "an earlier prompt", "since the id is the seed's
hash"). The page holds no story of the brain process or the Sentry fix. What is wrong is the socket transport in §1
and §8, which the code no longer has.
Sections:
  - §1 `#sec-1` One thread for the game -- edit (socket wording, mermaid, line refs)
  - §2 `#sec-2` Each frame, step by step -- keep (refs `Assist.cs:162-278` still right)
  - §3 `#sec-3` One decision in flight -- keep
  - §4 `#sec-4` Idle, screen by screen -- keep
  - §5 `#sec-5` Card prompts -- keep
  - §6 `#sec-6` Answers in the game's names -- keep
  - §7 `#sec-7` When the mod waits -- keep
  - §8 `#sec-8` The brain link -- rewrite (child process on stdin/stdout, not a socket)
Findings:
  - L3 description "a socket on background threads" -> "the brain's pipes on background threads"
  - L15 stale ref `Host.cs:41` -> `Host.cs:45`
  - L17 stale "Waiting on a socket would stall the frame, so the socket's IO runs on background threads" -> "the brain's stdin/stdout"; "This is the pattern of STS2MCP" is attribution, keep
  - L21 stale mermaid `"JSON lines<br/>Unix socket"` -> "JSON lines<br/>stdin / stdout"
  - L28 stale ref `Host.cs:44-65` -> `Host.cs:48-69`
  - L138 stale "**The socket**: the Unix domain socket `~/.local/state/sts2ai/brain.sock` (... `BrainSocket.DefaultPath`) ... The mod is the client, the brain the server" -> "The pipes: the brain's stdin and stdout, carrying JSON lines; the brain's stderr goes to `brain.log`". `BrainSocket` no longer exists
  - L139 stale "**Why there.** Steam starts the game inside ... pressure-vessel ... so the socket lives under the home directory" -> delete (no socket). Keep only "settings and log live in `$XDG_STATE_HOME/sts2ai`, outside the game's profile"
  - L140 stale "A background thread connects, starts a writer thread ... waits 2 s and connects again, forever" -> "starts the bundled brain (`BundledBrain.Run`), a writer thread, reads until the brain's stdout ends; on any end it kills the brain with its workers, waits 2 s and starts it again. With no brain shipped for the platform, it reports so every 2 s"
  - L153-154, L156 wording "reconnect", "the connection drops" -> "the brain is started again" (meaning holds)
  - L158 stale "so the panel's switch needs no second brain, as long as the macro brain may play in both" -> the shipped `champion.json` is the SL champion (`afterstates: clone`), which refuses NoSL. Rewrite once the NoSL behaviour is decided (cross-page issue 2)
  - L160 stale "`brain: NoSL C; SL C; listening on PATH` ... (`Commands/BrainCommand.cs:58-63`)" -> "`... serving stdin and stdout`", printed on stderr and so in `brain.log`; refs `BrainCommand.cs:59-64`
Inbound links: none to a deleted section. Links into §8 (anchor kept, text changes): `en/arch/environments/index.mdx:70`, `en/arch/harness/routing.mdx:160`, `en/arch/mod/index.mdx:26,98,159,177`, `en/arch/protocol/wire.mdx:145`, `en/arch/simulator/runs.mdx:192`.
Figures/data: `TickDemo.svelte` (imports `book/words.ts`, `v/names.ts`) -- clean: no hash, net name or date. Its rules (3 idle frames, cancel then wait, checkpoint then actions then decision) match `Assist.cs` at HEAD.
Twins: match (zh-tw L3, L17, L21, L138, L139 carry the same socket text).
Future/next content: none.
Rough size of the rewrite: about 1 paragraph removed (L139); about 10 lines rewritten.

### en/arch/mod/sl-record.mdx  (lines 121)  -- FATE: EDIT
Reason: §1-§7 describe current code. §8's results are an experiment log, with a date, a `tmp/` path and an older net. §6 rests on a brain config that is no longer the shipped one.
Sections:
  - §1 `#sec-1` What the record is -- keep
  - §2 `#sec-2` The checkpoint -- keep
  - §3 `#sec-3` Every step, where the game commits it -- edit (L66 last sentence)
  - §4 `#sec-4` Steps that play out -- keep
  - §5 `#sec-5` Leaving a room -- keep
  - §6 `#sec-6` A broken record -- edit (L97 stale + future)
  - §7 `#sec-7` What is sent, and when -- keep
  - §8 `#sec-8` Checking it in the game -- rewrite: keep "The round trip" (what `clones` checks), delete "Results" and "The SL planner's wait"
Findings:
  - L66 history "This settled the open question of how to capture the player's actions." -> drop (the "why hooks, not a diff" rationale stays)
  - L97 stale "That suits the brain the mod usually runs, the NoSL champion's file with the SL planner (... L0 on screen afterstates ...)" -> the mod now ships `sl.json` (L0 on the clone). The "gap" this paragraph describes is now the default case (cross-page issue 3)
  - L97 future "Holding back every SL decision of a broken node would close that gap." -> future list (and arguably a bug to fix)
  - L112 stale-ish "`brain --wire FILE` writes every message the hosts send" -> true for a hand-started `brain`, but the mod's bundled brain is started without `--wire`, so no in-game wire capture can be made today. Say how a capture is made now, or mark it as unavailable
  - L116 experiment "On the simulator's own export (one SL run hosted against `brain`, 455 decisions), every decision is the same." -> drop (a past test result; the brain no longer has a socket host either)
  - L117 experiment/story "In the real game, on floors 2-5 ... 89 of 90 ... the mod now waits for the step's task" -> drop
  - L118 history "An earlier capture of floors 1-2 had found that card plays were not recorded" -> drop
  - L119 date + untracked + experiment "A later capture ... (2026-10-28, `tmp/assist/`, local) ... Whether `clones` was run on it is not recorded." -> drop
  - L121 experiment/version "replayed against a fresh brain ... (`plan` with an earlier combat net, beam 8x300 on 8 workers): ... 3-15 s ... 1-11 s ... under 50 ms" -> drop, or measure again with the current champion (c9-2) and state it as a fact
Inbound links to deleted/renumbered sections: §8 keeps its number (rewritten, not deleted). These pages cite §8 for the `brain --wire` + `clones` round trip or for in-game results, and their text must match the rewrite: `en/arch/environments/information.mdx:153`, `en/arch/gamebridge/index.mdx:77`, `en/arch/harness/index.mdx:144`, `en/arch/mod/auto.mdx:90,94,160`, `en/arch/mod/index.mdx:17,112`, `en/arch/output.mdx:181` (its "ran in the game in the session of 2026-10-28" is a date/story owned by another group), `en/arch/protocol/index.mdx:100`, `en/arch/simulator/reference.mdx:126`, `en/arch/simulator/runs.mdx:205`.
Figures/data: none.
Twins: match (same `2026-10-28`, `tmp/assist/`, 89/90, 455, 150/149/137, 56/66).
Future/next content: L97 last sentence (hold back SL decisions of a broken node).
Rough size of the rewrite: about 7 lines removed (L66 end, L114-121); about 3 rewritten (L97, L112).

### en/arch/mod/auto.mdx  (lines 170)  -- FATE: EDIT
Reason: the executor/goals/controls/patches content is current. It holds a few bug anecdotes, a dated `tmp/` capture, a settled open question and a lot of planned content, plus stale socket and `brain --log/--wire` claims.
Sections:
  - §1 `#sec-1` The executor -- keep
  - §2 `#sec-2` Playing by goal -- keep; the "Planned: the auto-run driver" block (L73-78) is future
  - §3 `#sec-3` Logs, and R3 = R4 -- edit (L82 wording, L90 date/tmp + stale; L92 and L94 future)
  - §4 `#sec-4` Controls and safety -- edit (L116 future clause, L117 stale socket)
  - §5 `#sec-5` Patches and the JIT -- edit (L130, L132 anecdotes; L134-136 future)
  - §6 `#sec-6` Prior art -- keep (design rationale and credit); edit L152
  - §7 `#sec-7` Tests -- edit (L160, L162; L163-165 future)
  - §8 `#sec-8` Open questions -- edit: delete Q1 (settled), Q2 becomes Q1
Findings:
  - L13 future "Playing whole runs with no one at the keyboard is the last rung ... R4 ... its driver is still planned" -> future list
  - L73-78 future: the whole "Planned: the auto-run driver" block
  - L77 user-word "So the user's own value is restored" -> "the player's own value" (here "user" means the player, but rule 11 makes the word risky)
  - L82 wording "(on this machine `~/.local/share/SlayTheSpire2/logs/godot.log` ...)" -> "on Linux"
  - L90 stale "A session in the real game is recorded on the brain's side instead: `brain --log FILE` ... `brain --wire FILE`" -> the bundled brain is started without `--log`/`--wire`, so no in-game session is recorded today; only `brain.log` (stderr) exists
  - L90 date + untracked + story "The 2026-10-28 capture is such a pair, kept locally in `tmp/assist/`" -> drop
  - L92 future "Planned: the mod's decision log ... `NumReloads`" -> future list
  - L94 future "Planned: R3 = R4 ..." -> future list
  - L116 future "and the planned driver would refuse any multiplayer lobby" -> future list
  - L117 stale "The socket carries only protocol messages." -> "Nothing leaves the machine: the brain is the mod's child process, on pipes". The seed/checkpoint argument stays, now about the child process
  - L130 history/story "This was seen headless, where SapphireSeed was inlined while another patch recompiled it." -> drop (the "why first" rule stays; same anecdote as observe.mdx L115)
  - L132 story "this was seen headless after a few thousand combats, which is why the simulator turns tiering off" -> "a rejit can drop a patch silently, so the simulator turns tiering off"
  - L134 status "The panel shows no warning yet." -> keep or move to the future list
  - L135-136 future "Planned: a heartbeat", "Planned: a launch flag" -> future list
  - L152 stale "The mod uses a Unix socket only." -> "The mod opens no port: it talks to its own brain over pipes."
  - L160 history-ish/stale "**SL round trip**: `brain --wire` and `clones` | built and run" -> "built"; it cannot be run against the game with the bundled brain (see sl-record L112)
  - L162 story-ish "were reviewed against the decompiled game" -> "are checked against the decompiled game" (or drop the past act)
  - L163-165 future rows (surface coverage, idle check, speed restore) -> future list
  - L169 history "1. Capturing the player's actions in assist mode: settled, with hooks ..." -> delete
  - L136 internal ref "(open question 2, [§8](#sec-8))" -> "open question 1" once Q1 is deleted
Inbound links to deleted/renumbered sections: no section deleted. Links into §3, whose text changes: `en/arch/environments/information.mdx:138`, `en/arch/learning/reference.mdx:48` (`assist/DATE-SUBJECT/`, the capture data folder), `en/arch/mod/index.mdx:17,36`, `en/status.mdx:85`. `auto/#sec-8` has only the same-page link L136. `auto/#sec-7` <- `en/arch/mod/index.mdx:192`, `en/arch/output.mdx:181`. `auto/#sec-6` <- `en/arch/mod/loop.mdx:17`.
Figures/data: none.
Twins: match (same `2026-10-28`, `tmp/assist/`).
Future/next content: L13, L73-78, L92, L94, L116, L134-136, L163-165 (auto-run driver / R4, mod decision log with NumReloads, R3=R4 test, multiplayer refusal by the driver, JIT heartbeat, `DOTNET_TieredCompilation=0` flag, surface-coverage/idle/speed-restore checks).
Rough size of the rewrite: about 4 lines removed (L90 sentence, L130, L169, half of L132); about 8 rewritten (L82, L90, L117, L136, L152, L160, L162, L77).

---

## Workshop / install check (requested)
- None of the 7 pages mentions `distro/build.sh`, `out/Sts2Ai/`, the Workshop or the shipped `brain/` folder. The only install path given is `dotnet build ... -t:Install` (mod/index L73), which no longer exists in `Sts2Ai.Mod.csproj`. The rewrite should cite `distro/build.sh` (tracked) and give `out/Sts2Ai/` only as where the item goes.
- No Mac or other host address appears in any page or twin (I grepped for IPs, `100.96`, "Mac", hub/fleet/Colab words: none). Only untracked pointers: `tmp/builds/REV/sim.sh`, `tmp/home` (mod/index L113) and `tmp/assist/` (sl-record L119, auto L90).
- Note for the release: `distro/build.sh` itself (tracked) cites `plans/2026-11-07-1333-steam-workshop-release.md` in its header (plan + untracked). It also builds against `tmp/gamebin/v0.111.0` and `tmp/item/REV`. The working tree has an uncommitted edit that moves `MAC=` to `STS2_MAC` in `.env`; it is not mine, so I left it alone.

## Cross-page issues
1. **The socket is gone in code but present in many pages.** `--socket`, `BrainSocket`, `BrainServer.Listen`, `run --brain` and `-t:Install` were removed when the mod began starting its own brain. Pages still describing them, outside this group: `en/index.mdx:55` (mermaid "Unix socket"), `en/arch/protocol/index.mdx:34`, `en/arch/protocol/wire.mdx:14` (§ on the socket path), `en/arch/harness/index.mdx:69` (`run --brain SOCKET`), `:127` (`Listen(path)`), `en/arch/simulator/runs.mdx:192-208` (`brain [--socket PATH]`; the abstract socket of `run --check` still exists), `en/arch/simulator/reference.mdx:124`. `BrainCommand` now says it "serves the host that started it ... on stdin and stdout, until stdin closes".
2. **One brain for both environments no longer holds for the shipped mod.** `distro/build.sh` ships `eval/champions/sl.json` as `brain/champion.json`, with `afterstates: clone`. `ChampionOptions.cs:41` throws "l0 plays on the node's clone in SL only" for NoSL. So the panel's NoSL switch (mod/index L109, L124; loop L158) probably gives "Brain error" with the shipped brain. Not run in the game; it needs a decision (ship nosl.json too, or drop the switch) before the docs can state it.
3. **Broken node + clone afterstates.** `Assist.cs:238` holds back only SL *combat* decisions of a node without a checkpoint or with a broken record. With the shipped clone-afterstate champion, macro decisions in such a node reach the Harness. There they use a checkpoint and steps that stop at the break, or the previous node's, or fail with "an SL decision before any node checkpoint" (`Dispatcher.cs:192`), which turns the mod off until the next hello. sl-record L97 calls this a gap of a non-default config; it is now the default.
4. **No in-game capture is possible now.** The mod starts `brain --champion champion.json --workers N` with no `--log`/`--wire`. The round trip `brain --wire` + `clones` (sl-record §8, auto §3/§7, environments/information L153, protocol/index L100, simulator/reference L126, simulator/runs L205) cannot be run against the game as the pages describe.
5. **Stale code facts in mod/index §7:** 17 files (BundledBrain.cs missing); BrainLink 110, Host 70, CombatMark 139, Settings 92, csproj 28 lines; `Host.cs` refs in index L94/L150 and loop L15/L28 are 2-4 lines off; manifest 0.2.0 with a new description.
6. **The same anecdote twice:** SapphireSeed inlining is told in gamebridge/observe L115 and mod/auto L130; keep the rule, drop both stories. The tiering "seen after a few thousand combats" (auto L132) probably also appears in simulator/headless §6 (another group).
7. **Overlap:** mod/index §5 (controls table) and mod/auto §4 (controls table) list the same keys and toggles. One could link to the other. GameBridge actions L105-117 ("The SL record" table) duplicates mod/sl-record §3's table; keep one, link the other.
8. **output.mdx:181** (another group) says "ran in the game in the session of 2026-10-28" and links sl-record §8 and auto §7: a date/story that goes with the §8 results.
