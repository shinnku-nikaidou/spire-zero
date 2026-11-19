# The mod on the Steam Workshop, with its brain inside

Drafted 2026-11-07 13:33 Taiwan time for the user's ask of the same day: a mod other players subscribe to on Steam and
that just works, instead of a brain started by hand in a terminal. A plan only: nothing is built or changed yet.

Base: main at 52ea355.

**Approved by the user 2026-11-07 ~13:38 Taiwan time** ("ok"): the plan, D1 and D3 as recommended. D2 changed by the
user at 13:40: **Linux and macOS arm64 first, no osx-x64**; Windows comes later. Step 1's stub is one per platform
(the user's ask).

## What is true today (checked for this plan)

- The mod (`src/Sts2Ai.Mod`, one `Sts2Ai.dll` + `Sts2Ai.json`) is installed by `dotnet build -t:Install` into
  `<game>/mods/Sts2Ai/` and connects to `~/.local/state/sts2ai/brain.sock` (`Protocol/Wire.cs:100`), retrying every 2 s.
- The brain is SimWorker's `brain` command, started by hand (`--champion eval/champions/sl.json`). It runs the game's own
  `sts2.dll` headless and starts its search workers as child processes of itself (`Harness/WorkerPool.cs:36`,
  `Program.SelfCommand`), about 160 MB each. It must stay out of the game's process: a process holds one live game.
- The brain finds the game's DLLs through `STS2_BIN_DIR` or the path baked in at build time (`Program.cs:37-41`). It needs
  the .NET 9 SDK's shared runtime on this machine, ONNX Runtime's native library, and the two nets
  (`weights/all/c9-2.json` 8.8 MB, `v9-2.json` 5.3 MB).
- The headless engine's Godot stub is linux-x64 only: it mmaps a page and writes x64 machine code into it
  (`SimWorker/Runtime/GodotNativeStub.cs:26`). No Windows, no macOS arm64 (where a code page needs `MAP_JIT`).
- The game loads Workshop mods itself (`ModManager.ReadSteamMods`: every subscribed item's folder, scanned like `mods/`),
  and also treats `<game>/mods_STEAMTEST/` as a Workshop source, which lets us test the Workshop layout without
  uploading. A mod that is both local and on the Workshop: the game keeps the higher `version` and disables the other.
- The loader reads **every `.json` file** under a mod's folder as a possible manifest (`ReadModsInDirRecursive`). A file
  with a `name` but no `id` is logged as an error ("looks like a mod manifest but is missing the 'id' field"); our net
  files start with `{"name": "c9-2", ...`. So nothing shipped may end in `.json` except the manifest.
- Workshop items can declare the Steam branches they support; the game checks it at start and shows
  `MOD_ERROR.STEAM_BRANCH_UNSUPPORTED` on another branch (it still loads the mod; our own version guard then turns it off).
- Branches (api.steamcmd.net, 2026-11-07): `public` build 23811903 of 2026-06-19, `public-beta` build 24724944 of
  2026-08-14. This machine is on `public-beta`, which is v0.111.0, the version everything here is written for. So a
  player on the default branch has an older game, and the mod's version guard would switch itself off.
- The Mac (`STS2_MAC` in `.env`, M4, 10 cores, 24 GB, macOS 27.2) has the game at v0.111.0, with
  `data_sts2_macos_arm64/` holding its own .NET 9.0.7 runtime and `sts2.dll`. Its `sts2.dll` is a different build from
  Linux's (`main_assembly_hash` 1172974615 against 1579942752), so the headless engine must be checked on it.
- The manifest's description still says "Assist only: it never acts on the game", untrue since auto-play.

## Out of the box (the user, 2026-11-07 14:05: "我需要玩家電腦上面mod開箱即用")

A player subscribes, starts the game, and the AI works: nothing to install, no terminal, no setting. The game's own
one-time "agree to mods" click stays (the game asks it, not us). What that takes, each item a step below or a check:

- No .NET on the player's machine: the brain is self-contained (step 3).
- No terminal: the mod starts the brain itself and restarts it when it dies (steps 5, 6).
- No game files shipped: the brain loads the player's own `sts2.dll` (step 2).
- The brain's file runs as downloaded: the mod sets its executable bit before starting it on Linux and macOS (Steam's
  download is not relied on to keep it); macOS: signed (step 3) and not quarantined (checked with a real subscription,
  step 15).
- First start: the panel says the brain is starting (nets loading, workers booting) until it answers.
- The game's version: **the player's, not ours**. Steam news (2026-11-07): the default branch is v0.107.1 (Major Update
  #2, 2026-06-19), v0.108.0-v0.111.0 were public-beta patches, and on 2026-10-30 the developers said updates return in
  two weeks. A mod that works only on public-beta is not out of the box (D1 below).

## Decisions for the user

- **D1, which game version.** Approved first as public-beta only; reopened by "out of the box" (above): the item goes
  public on the version the default branch runs. Recommended: build and test everything on v0.111.0 now, port to the
  next public version when it lands (announced for about mid-November), and make the item public then.
- **D2, platforms and order.** Decided: Linux and macOS arm64 first (tested here and on the Mac); no osx-x64. Windows
  later; its test route then: the game's Windows build under Proton on this machine, and a real Windows PC if one exists.
- **D3, defaults for strangers.** Recommended: keep today's defaults (SL, the champion's effort), and say plainly in
  the listing what SL means (the AI plans each room on its true clone, as save and load would). Workers default to the
  physical cores minus 2, a settings row to change it.

## Steps

### A. A brain that runs anywhere, from a folder

1. **Done, e81920f**: the Godot stub is one C# function (`[UnmanagedCallersOnly]`) on every platform, no code written
   at run time (the user: "不要jit"). macOS arm64 refuses a read-write-execute page without `MAP_JIT` (probed on the
   Mac). On linux-x64: selftest 46 checks ok, bench 269.9 combats/s against the machine code's 270.0 and 262.7. Not yet
   run on macOS or Windows; the docs' simulator/headless §3 still describes the machine code.
2. **The game's DLLs from the player's install.** The published brain gets `STS2_BIN_DIR` from whoever starts it (the
   mod, step 5); no game file is ever shipped. Today's baked-in path stays for development builds.
3. **Done, 3bc35ec: the build for all platforms** (the user, 2026-11-07: the standard way, all at once).
   `MAC=user@host distro/publish-brain.sh [REV]` makes `tmp/brain/REV/{linux-x64,osx-arm64,win-x64}/`, 108 / 127 / 95 MB,
   about 227 files each, and signs the Mac launcher ad-hoc on the Mac (unsigned, macOS kills it: exit 137). Checked at
   602ec89 (same source): linux-x64 selftest on the Steam game's DLLs 46 ok, 0 failed; osx-arm64 on the Mac on the Mac
   game's DLLs 46 ok, 0 failed, output identical to Linux line for line; win-x64 built, not run. The selftest takes
   ~135-155 s at this commit (10 s at 52ea355): its cycle check spends it in `LoopSearch.Cash`, from 7d6df94, the
   no-hand-values session's part A, not from publishing. What was planned:
   - The mod: one build. `Sts2Ai.dll` is IL and runs in the game's own .NET on every platform.
   - The brain: `dotnet publish` once per runtime identifier, all from this Linux machine. Cross-publishing is the
     normal .NET route for an app that runs on the JIT; only Native AOT needs the target OS, and Native AOT cannot be
     used here: the brain loads the player's `sts2.dll` at run time and Harmony rewrites game methods at run time.
   - Settings in a publish profile, `src/Sts2Ai.SimWorker/Properties/PublishProfiles/brain.pubxml`, so the fleet's
     builds do not change: `SelfContained` (players have no .NET), a folder per platform; tiered compilation stays off
     (the project's setting); no trimming (`sts2.dll` needs the whole framework); `DOTNET_ReadyToRun=0` is set by the
     mod when it starts the brain.
   - **Not a single file** (tried 2026-11-07): Harmony patches through the JIT's own library (`libclrjit`), which a
     single file builds into the executable; its selftest stopped at the first patch with "Could not locate clrjit
     library". The folder's `deps.json` and `runtimeconfig.json` have no manifest field, so the game's scan skips them
     with an info line.
   - One script, `distro/publish-brain.sh` (602ec89): `dotnet publish -r RID -p:PublishProfile=brain` for
     `linux-x64`, `osx-arm64`, `win-x64` one after another (they share the libraries' outputs), each into its own
     folder, all from one commit. win-x64 is built from the start (it costs nothing) and tested later (D2).
   - One-time downloads from nuget.org into `~/.nuget/packages`, at the SDK's runtime 9.0.22: the runtime packs for
     linux-x64 (37.1 MB), osx-arm64 (34.4 MB), win-x64 (37.2 MB) and the launcher packs for osx-arm64 (4.8 MB) and
     win-x64 (5.4 MB). ONNX Runtime's native libraries for all three are already in the cache (1.31.0).
   - Checks: each folder's size; linux-x64's selftest with `STS2_BIN_DIR` on the Steam game's folder (not the
     snapshot); osx-arm64 copied to the Mac, its signature read (`codesign -dv`: Apple silicon runs only signed code,
     ad-hoc is enough; if the Linux-made file is unsigned, the script signs it on the Mac with `codesign -s -`), then
     its selftest against the Mac's own game DLLs, the stub's first arm64 run.
4. **Nets and champion without `.json`.** The item carries the champion's two nets and its options under names the
   loader skips (e.g. `brain/c9-2.net`, `brain/champion.txt`), with paths relative to the brain. Only the
   champion's nets go: c9-2 and v9-2, 14 MB.

### B. The mod starts its own brain

**Done in code, 6e903b2 (2026-11-07); not yet run in the game.** The socket is gone (the user: "停止 socket"; the
hand-started brain at 5dbfff9 was stopped and its build deleted): the mod always starts the brain in its folder
(`BundledBrain`), over the child's stdin and stdout, `brain.log` beside the settings, started again 2 s after it ends;
`brain` serves only that host; `run --brain`, `BrainServer.Listen`, `BrainSocket` and the mod's Install target are
removed. Workers: half the logical processors, at most 8 (no settings row yet). Step 7 (per-OS folders) not done: the
state folder stays `$XDG_STATE_HOME/sts2ai` or `~/.local/state/sts2ai` on every OS. `distro/build.sh` (the user: the
finished item in `out/`, ignored) makes `out/Sts2Ai/` (343 MB at 6e903b2); a copy sits in the game's
`mods_STEAMTEST/Sts2Ai/` for the game test (0.2.0 there wins over the old 0.1.0 in `mods/`). Checked: 115 C# tests; the
brain on stdin with 20 messages of a real SL game: ready plus 6 recommendations, nothing else on stdout, exit 0 at EOF.


5. **Spawn the brain.** On start the mod finds its own folder (the Workshop item's or `mods/Sts2Ai/`), picks
   `brain/<os>-<arch>/`, and starts the brain with `STS2_BIN_DIR` = the running game's data folder (from
   `OS.GetExecutablePath()`), the champion, `--workers` from the settings, and a log file in the mod's state folder.
   The wire runs over the child's stdin and stdout instead of a socket: no socket file, no stale socket, and the brain
   ends when the game does (stdin closes, then each worker's stdin closes). Development stays as today: if a brain is
   already listening at `brain.sock`, the mod uses that one and starts none.
6. **Lifecycle on screen.** New panel states: starting the brain (loading the nets, booting N workers), the brain
   ended (its last log lines' reason and the log's path), with a restart button. Checked by killing the brain, a
   worker, and the game (`kill -9`) and seeing no process left behind.
7. **Per-OS folders.** Settings and logs under `%LOCALAPPDATA%\sts2ai` on Windows, `~/Library/Application Support/sts2ai`
   on macOS, `~/.local/state/sts2ai` on Linux as now.
8. **Manifest.** New description (assist and auto-play, single-player, starts a background process), version bump.

### C. Check each platform with the Workshop layout

9. **Linux**: the staged item in `<game>/mods_STEAMTEST/Sts2Ai/` (local `mods/Sts2Ai` removed meanwhile), the game
   started from Steam (so inside Steam's runtime container): decisions on every surface, auto-play through a room, SL
   decisions replayed with `clones --wire` as before.
10. **macOS** over ssh on the Mac: first the brain alone (`selftest`) against the Mac's `sts2.dll`, then in the game with
    `mods_STEAMTEST`. Check whether a Linux-built `osx-arm64` apphost runs or needs an ad-hoc `codesign -s -` on the Mac.
11. **Windows**: later, per D2.
12. **Speed on an ordinary PC**: seconds per decision at low and medium with 4 and 6 workers while the game renders, to
    set the defaults (D3).

### D. Upload

13. **Uploader.** A small tool (`tools/workshop/`) that uses the game's own `Steamworks.NET.dll` and `steam_api` from
    the local install with appid 2868840 and the user's running, logged-in Steam client, so no password is typed
    anywhere: create the item once, then upload the staged folder, preview image, title and description (English and
    Chinese), change note, visibility, and the supported branch range (D1). SteamCMD's `workshop_build_item` is the
    alternative, but it needs a login the user would type in.
14. **The user's part**: accept the Steam Workshop agreement on the website the first time; approve each upload.
15. **First upload unlisted**, subscribed from the Mac to test the real Workshop path, then public.

### E. Every later release

16. **One release script**: build the mod and the brains at one commit, stage the item, run step 9's check,
    upload. Mod and brain always ship together, so the wire never mismatches.
17. **New champion** (new nets): re-run the script; subscribers get it through Steam's own update.
18. **New game version**: the version guard turns the mod off with its message until the port is done; port, gate,
    upload with the new branch range.

### Docs

The mod chapter's §3 (installing) and §4 (running it with a brain) change, and the release gets its own page. Who
writes each change is asked when the code lands.

## Not in this plan

Paid distribution, multiplayer, an installer outside Steam, NoSL-only builds, the auto-run driver.

## Progress (Taiwan time)

- 2026-11-15 15:47: the item carries the Rust engine for linux-x64, osx-arm64 and win-x64
  (`plans/2026-11-15-1240-mod-engine.md`), staged in `mods_STEAMTEST`; C (the real game on each platform) still to do.
- 2026-11-15 (api.steamcmd.net): the default branch is still build 23811903 of 2026-06-19 (v0.107.1); public-beta is
  build 26660232, v0.113.1. So the item supports public-beta only (`--branches public-beta:public-beta`, D1).
- 2026-11-15 16:20, step 13 done: `src/Sts2Ai.Workshop`, the `workshop` tool (`check`, `create`, `upload`;
  `distro/workshop/README.md`), with the game's Steamworks.NET and `libsteam_api.so` copied beside it from the snapshot.
  `workshop check` here: Steam logged on, the game on public-beta build 26660232, no item yet. The texts are
  `distro/workshop/LANGUAGE.bbcode` in the game's 17 languages (the user: the listing in many languages; only what was
  done, "沒做過的事情直接不寫"), with each character's SL wins at A0 and A10 for v10-2 + c10-2.
  Step 14 as the user put it: the Steam website steps are mine in Chrome, the user only accepts the agreement.
- 2026-11-15 16:24: the item is 3840142917 (`workshop create`; Steam asked for no agreement, the account had accepted
  it). 0.3.0 built at aefa47a0 (378 MB) and uploaded private with `--branches public-beta:public-beta` in 45 s, the 17
  languages' texts after it; `workshop check`: private, 394.1 MB. The page renders the lists and the table; no preview
  image yet. Subscribed with the user's account in Chrome: Steam downloaded it to
  `steamapps/workshop/content/2868840/3840142917`, the same files as `out/Sts2Ai` (`diff -rq`), the Linux launcher and
  engine still executable. The `mods_STEAMTEST` copy moved to `tmp/modeng/old-installs/`, so the game loads the
  Workshop's. Next: C, the real game on each platform through the subscription, then a preview image and public.
- 2026-11-15 ~16:45, the first real-game test (the user, Linux, SL, through the subscription): the mod loaded from the
  Workshop folder and reached the brain; floor 2's first combat decision failed, the engine refusing the export
  (`EncounterModel._backgroundAssets`, the background scenes the screen picks, null in the simulator). Fixed in
  port.toml at afa491a2 (BackgroundAssets is presentation). 0.3.1 built there (with the panel work of 3727b316): the
  item's Linux brain on `parity --world engine`, the same 720 combats as before, 0 differences. Installed locally in
  `mods_STEAMTEST` (the user: no upload for each fix): the game keeps the higher version, 0.3.1, over the Workshop's
  0.3.0. The Workshop gets 0.3.1 after the real game passes.
- 2026-11-15 ~16:55: the second try failed the same way: the game had loaded another session's 0.3.1 from `mods/`
  (its panel work, with the brain from before afa491a2), which wins a tie over `mods_STEAMTEST` (a Steam source to the
  game). The user: a session whose version another took goes one up; old local copies are deleted, the Workshop
  subscription left alone. 0.3.2 at 515a5187 (the panel commits and afa491a2) is now the only local copy, in `mods/`.
- 2026-11-15 ~17:05, the third try (0.3.2): a combat (NIBBITS_WEAK) with no engine error, then the game died entering a
  treasure room: SIGSEGV in the mod's `Field<bool>(room, "_hasChestBeenOpened")`, a field v0.113.0 dropped (the core
  dump's code is a null virtual call then an unbox to bool). Fixed at fb104fbf (the chest's button's IsEnabled; a missing
  field throws a named exception); every other member the mod reads by name exists in v0.113.1. Reinstalled in `mods/`.
- 2026-11-15 ~17:15, the fourth try (0.3.2 at fb104fbf): from the treasure room to floor 15, 85 decisions, no engine
  error. Then the user closed the map and went back to the elite's rewards, which the simulator never does (it travels
  once the room is done); the next decision's clone ran the record on the map while the game asked on rewards
  (`reward:1` against the map's surface). Fixed at 76050b51: going back into a room from the map it was left for breaks
  the node's SL record, so the rest of the room is decided without the clone. Reinstalled in `mods/` (17:22). Seen in
  the same run, sent to its own session: potions left on the rewards with free slots, a Duplication potion drunk and
  the turn ended with energy unspent.
- 2026-11-15 ~17:27, the same game (0.3.2 at fb104fbf still loaded) on to floor 33: after THE_INSATIABLE_BOSS, the
  rewards' first decision failed on the clone, "combat policy chose potion:0:1, which is not legal". The player had
  clicked the Weak potion in the enemy's turn; the game puts such a request aside and calls `RequestEnqueue` again at
  the player's turn, and NodeRecord took both, so the clone drank it and then found slot 0 empty (the save the game
  keeps from the room's arrival, `current_run.save.backup`, held one Weak potion in slot 0). Fixed in 0.3.3 at bef85f60:
  a put-aside request is skipped and recorded when it comes again.
- 2026-11-15 ~17:50, the user (still on 0.3.3, floor 33): a run continued after THE_INSATIABLE_BOSS had no SL clone
  on its card reward ("the run was continued from a save made after this room began"); then on floor 35 the map-return
  stopgap broke the record again. Both are what plans/2026-11-15-1745-player-interaction.md (approved 17:50) is for.
- 2026-11-15 18:22: 0.3.4 at d62ea0b2 (that plan's record, log and protocol 3: a checkpoint at every save the game
  makes and at the save a continued run loads, steps taken where they take effect, going back followed, the AI
  stopping when the player acts, a log folder per game session). Built with distro/build.sh (378 MB, three
  platforms). The item's Linux brain on `run --check` against the Steam install's game files (REGENT A10, low, seed
  1136): over the wire identical to in-process, 49/49 nodes and 24/24 saves inside a finished room replay exactly, 0
  game errors. Installed in `mods/Sts2Ai` by swapping the folder (0.3.3 moved to `tmp/modeng/old-installs/`); the game
  was running 0.3.3 and loads 0.3.4 once restarted. Logs: `~/.local/state/sts2ai/logs/<UTC start>/`.
- 2026-11-15 18:41: 0.3.5 at 5ee23fa6 (0.3.4's code, the champion now v10-2-2 + c10-2-2 since bb91bdab; the user,
  through the capacity session: "通知mod也升級一下版本，用v10-2-2"). Built for the three platforms; the item's
  `brain/champion.json` names v10-2-2 / c10-2-2 and its nets are byte for byte `weights/all/`'s. Installed in `mods/`
  over 0.3.4 (the game still running 0.3.4's code until restarted). The listing's win table is v10-2 + c10-2's: it
  needs the new pair's numbers before the next upload.
- 2026-11-15 19:01: 0.3.7 at 48cae06c (0.3.6 with chest relics in the brain's clones: a relic picked from a chest is
  the player's, as the game's treasure screen obtains it; the user, through the mod session: "修"). Built with
  distro/build.sh (378 MB, three platforms; the champion v10-2-2 + c10-2-2). The item's Linux brain on `clones` over
  the user's 0.3.4 game log (18:22–18:45, floors 35–49) against the Steam install's game files: 266/266 SL decisions
  asked the same (265 before the fix). Installed in `mods/Sts2Ai` by swapping the folder (0.3.6 moved to
  `tmp/modeng/old-installs/`); the game was running 0.3.6 and loads 0.3.7 once restarted.
- 2026-11-15 19:04: the listing's wins are v10-2-2 + c10-2-2's (8f298dc4: the test's SL runs, medium, the old table's
  seeds; A0 348 of 450, A10 288 of 1,200), and the item has a preview image, `distro/workshop/preview.png` (2649447a;
  1024 x 1024, 591 KB, no game art), both for the next upload, which waits on the real-game tests.
- 2026-11-15 19:1x–19:17, the user: "完整搞好就可以公開了" (public once everything is done, without asking again). The
  0.3.7 item's brains (48cae06c) off the game, each platform with its own build:
  - macOS (the Mac, M4, its Steam install at v0.113.1, its own `sts2.dll`): `parity --world engine` (FOOL, DEFECT,
    A0 and A10, seed 1132, `--continue 4`): 720 combats, 16,749 decisions, 0 differences, 363 won 357 lost, as on Linux;
    exports 0.93 ms each. `run --check` cannot start there: its in-process wire uses an abstract Unix socket, which
    only Linux has (a check tool's limit; the mod talks to its brain on stdin and stdout).
  - Windows (the VM, the Linux snapshot's game assemblies): the same parity, 0 differences, 363 / 357; 2.12 ms an export.
  - One SL run (IRONCLAD A10, low, seed 1138, 4 workers) on all three: the same run to the byte (won on floor 49, 896
    decisions, the same deck, relics and maps), 26 s on the Mac, 48 s in the VM.
  Left for going public: the real game on Linux (the player-interaction checklist) and on the Mac, both the user's;
  then the tested build uploaded with the new texts and preview, and set public.
- 2026-11-15 19:31–19:36: the item is **public** (the user: "順便把圖片上傳上去，可以公開了，反正沒人看得到", ahead of the
  real-game tests on Linux and the Mac). 0.3.7 (48cae06c, the build checked on three platforms) with the 17 new texts
  and the preview; the page shows them (Chrome, logged out), 394.2 MB. Two things learned:
  - the uploader runs as the game, and Steam lets one machine of an account play at a time: with the game open on the
    Mac, Steam logged this machine off ("Logged In Elsewhere") and the upload failed. Close the game everywhere first.
  - with `--note`, the update's commit timed out three times (k_EResultTimeout, the content and preview already up);
    without it, the same update went through at once. The 0.3.7 change note is not on the page.
  Still to do: the real game on Linux (the checklist) and on the Mac through the subscription.
- 2026-11-15 19:36: 0.3.8 at c54bb4b4 (0.3.7 with SL wins worth exactly the same planned by the fewest turns ended,
  3fcdef51; the user: "好，升版"). Built with distro/build.sh (378 MB, three platforms; the champion v10-2-2 + c10-2-2,
  nets byte for byte 0.3.7's). The item's Linux brain on the user's SLIMES_NORMAL decision (A10 SILENT, floor 6, the
  0.3.7 log of 10:49 UTC, planned at round 1 and played for 23 rounds) plans a win with 9 turns ended, the same value,
  56 HP and the Flex Potion kept. Installed in `mods/Sts2Ai` by swapping the folder (0.3.7 moved to
  `tmp/modeng/old-installs/`). The local 0.3.8 is above the Workshop's 0.3.7, so the game loads 0.3.8: a real-game
  test from now on tests 0.3.8, not the uploaded build.
- 2026-11-15 20:42: 0.3.9 at 84edc72a (an act's last rewards_done is final in the record, so the Architect's room is
  asked on the right clone; no decision is read while a relic flashes; both from the user's 0.3.8 game). Linux only:
  the Mac did not answer (no route to host), so `distro/build.sh` stopped before macOS's engine and launcher; the Linux
  part was put together by the script's own steps in `tmp/player-interaction/item-0.3.9-linux/`, and `out/` still holds
  0.3.8's full item. The item's Linux brain on `clones` (the user's game, decision 779 with the step kept): the same.
  Installed in `mods/Sts2Ai` (0.3.8 moved to `tmp/modeng/old-installs/`). Not uploadable: the full 0.3.9 needs the Mac.
- 2026-11-15 21:04: version numbers are this session's from now on (the user: "你就負責一直發版本號，我告訴你要上傳workshop再說");
  uploads wait for the user's word. 0.3.10 at 1b62acaa: 93b43011's shop fix (the AI buys only once the shop has been
  open half a second; the game died at 20:52 in NMerchantCard.DoRelicFlash) on top of 0.3.9. The Mac still unreachable,
  so built with the new `LINUX_ONLY=1` of `distro/build.sh` (973790c7: linux-x64 alone into `tmp/item/REV/Sts2Ai`, `out/`
  untouched; 51 s). Its brain on `run --check` (IRONCLAD A10, low, seed 1138): identical over the wire, 31/31 nodes and
  18/18 saves in a finished room replay exactly, 0 game errors. Installed in `mods/` over 0.3.9 with the game closed.
  The game compares manifest versions as numbers (SemanticVersion), so 0.3.10 is above the Workshop's 0.3.7.
- 2026-11-15 21:16: **0.3.11 uploaded** (the user: "Spire Zero：殺戮尖塔 2 AI 軍師 ok, 正式發版本吧"). 77e58d1f: 0.3.10's code,
  the game's mod list names it Spire Zero (was STS2 AI); the Workshop title in the 17 languages is "Spire Zero: AI
  Strategist for Slay the Spire 2" (zh-TW 殺戮尖塔 2 AI 軍師, e5a87fa1), the descriptions unchanged. The Mac answered again,
  so distro/build.sh built all three platforms (378 MB, 3 min 47 s; the champion v10-2-2 + c10-2-2, nets byte for byte
  `weights/all/`'s). The brain and engine sources are 0.3.8's (only the mod's dll changed since). The game closed on all
  three machines; the first upload's commit timed out (k_EResultTimeout, without `--note`, the item untouched), the same
  command again went through. Public, 394.2 MB; the page shows the new title. Not uploaded: the preview (unchanged).
- 2026-11-15 22:08: 0.3.12 at 25689778, febb79d8 (card plays recorded off the game's combat history: in the user's
  0.3.11 game the Harmony prefix on `CardModel.OnPlayWrapper` went quiet after 60 plays; a failed SL decision on the
  clone breaks that room's record, not the run's). The Mac back, so the full three-platform item, in `out/` (3:04).
  Its Linux brain on `run --check` (IRONCLAD A10, low, seed 1138): identical over the wire, 31/31 nodes and 18/18
  saves in a finished room replay exactly, 0 game errors. Installed in `mods/` over 0.3.11 with the game closed; it
  wins over the Workshop's 0.3.11. Not uploaded (the user's word first).
- 2026-11-16 08:41: 0.3.12 (25689778, three platforms) uploaded on the user's word ("Upload it"), public, with the
  preview and the 17 texts. Steam was not running (closed the night before); started with `steam -silent`, logged on
  in 6 s. The first update (new content and the preview) timed out at its commit, k_EResultTimeout, with the content
  already up; the same update again, without the preview, went through in a second. Last night's timeouts were the
  same: the first commit carrying new content timed out, a repeat passed (so not \`--note\`'s doing, as written above).
  The page (logged out): 394.189 MB, updated now.
- 2026-11-16 09:37: 0.3.13 at d2f59348, asked by the potion session for the user's real game: the potion rules
  (f372e07c a potion reward the belt has room for is always taken; d3e2c1a2, 26947aad the Foul Potion; 9a631812 free
  disposal) and the champion v11-0 + c11-0 (c4014213, v10-2-2 / c10-2-2 carried to the Dread input). Three platforms
  (the VM started for it and shut down after; 3:53). The item's nets are `weights/all/`'s byte for byte. Its Linux
  brain on `run --check` (IRONCLAD A10, low, seed 1138): won, identical over the wire, 49/49 nodes and 29/29 saves in a
  finished room replay exactly, 0 game errors. Installed in `mods/` over 0.3.12 with the game closed. Not uploaded.
- 2026-11-16 09:41: 0.3.13 (d2f59348) uploaded, public. The user's word came in the potion session ("上傳", 09:38,
  read in that session's transcript before acting). The first commit timed out, the repeat passed, as before. For a
  minute after, the page said the item was hidden; Steam's API said public and not banned, and the page came back on
  its own (394.190 MB, updated 09:40). 24 subscribers.
- 2026-11-16 11:04: 0.3.14 at de5bb713, asked by the auto-play session for the user's real game ("請它建置"): 902a9d47
  (SL checkpoints from the game's `Saved` event, not the `SaveRun` prefix that went quiet after 30 calls in the 0.3.11
  and 0.3.13 games; a worker that reported an error is replaced; auto-play stops where SL has no clone). Three
  platforms (the VM started and shut down again; 3:23). Its Linux brain on `run --check` (IRONCLAD A10, low, seed
  1138): won, identical over the wire, 49/49 nodes and 29/29 saves in a finished room replay exactly, 0 game errors.
  Installed in `mods/` over 0.3.13; the game was closed by then. Not uploaded. To check in the real game: the number of
  "record    checkpoint" lines in mod.log equals the game's writes of current_run.save in godot.log, past 30 too.
- 2026-11-16 11:26: 0.3.15 at 8acb59c7, asked by the discard session: da26190b (a turn start's choice asked where the
  real game asks it, so the SL plan stops searching again at each Tools of the Trade discard; a relic's counter read as
  it settles after its flash). Three platforms (3:25; the VM started and shut down). The engine changed, so its Linux
  brain ran `parity --world engine` (FOOL, DEFECT, A0 and A10, seed 1132, `--continue 4`): 720 combats, 0 differences,
  363 / 357 as before; and `run --check` (IRONCLAD A10, low, seed 1138): won, identical over the wire, 49/49 nodes and
  29/29 saves replay exactly, 0 game errors. Installed in `mods/` over 0.3.14 with the game closed. Not uploaded.
- 2026-11-16 11:45: 0.3.16 at 8538a7fb (the user: "建"), from the auto-play session's 3f95f9e3 and 63e83938: on Linux
  where SELinux enforces and .NET's W^X is on, MonoMod's rewrite of a patch after a tier-1 recompile is denied
  (execheap) and the patch is silently lost, so the mod turns itself off and shows a notice over the game with the launch
  option `DOTNET_EnableWriteXorExecute=0 %command%` to copy. Three platforms (3:56). Its Linux brain on `run --check`
  (IRONCLAD A10, low, seed 1138): won, identical over the wire, 49/49 nodes and 29/29 saves replay exactly, 0 game
  errors. Installed in `mods/` over 0.3.15 with the game closed. The game's Steam launch options (localconfig.vdf) are
  empty, so its next start shows the notice. Not uploaded.
- 2026-11-16 12:05: 0.3.16 (8538a7fb) uploaded on the user's word ("可以發版本了"), public, three platforms. The first
  commit timed out, the repeat passed. Steam's API: public, not banned, 394.459 MB, updated 12:04, 26 subscribers.
- 2026-11-16 19:08: 0.3.17 at 70aa0862, Linux only (the Mac unreachable, "No route to host"), for the user's game: the
  champion v11-0-td4 + c11-0; the panel's head names the nets and the search size ("net v11-0-td4 + c11-0 beam 16x300",
  8949b0b6: `Ready.Shown`, wire v4, the card 380 wide); a chest's relic always taken (49ac513d); a card reward or the
  map the player opened can be gone back from (f6df7313). First built at f6df7313, then again at the latest HEAD (the
  user: "永遠用最新的，涉及到升級mod的，先全部提交"; the engine's formatting leftovers committed first). Its Linux brain on
  `run --check` (IRONCLAD A10, low, seed 1138): won, identical over the wire, 49/49 nodes and 28/28 saves replay
  exactly, 0 game errors. Installed when the game closes. The listing gets a Dread column and the high report's
  numbers once it ends.
- 2026-11-16 19:22: 0.3.17 at d1ff1da3 (the same code as 70aa0862, plans only between) uploaded on the user's word
  ("Mac開了，可以上傳了"), public, three platforms (the VM started and shut down again). Its Linux brain on `run --check`
  (IRONCLAD A10, low, seed 1138): won, identical over the wire, 49/49 nodes and 28/28 saves replay exactly, 0 game
  errors; the nets are `weights/all/`'s byte for byte. Installed in `mods/` over 0.3.16 with the game closed. The
  upload's commit timed out three times, but the first had gone through: the item updated at 11:22:45Z and its change
  notes carry 0.3.17's. The texts are 0.3.16's for now; the listing's Dread column and the high report's numbers go up
  in a second upload once the report ends.
- 2026-11-16 19:33: the listing, on the user's word ("就依照100局上傳吧", then "依比例向上取整" and "就100/100"): a Dread
  column (the game's own name for the mode, English where the game has none: Arabic, Thai), "with Search effort set to
  High" in the intro, the high report's runs finished by 11:27 UTC (about 100 of each 200) as whole percents rounded up,
  100 % where one run of about 100 was lost too; no run counts shown (the user: "簡介註明「每格約 100 局」 不需要").
  Uploads with the content timed out at their commit four more times, and only the English texts went through; `upload`
  now leaves the content out without `--content` (d130525e), and the 17 languages' texts went up that way at once.
- 2026-11-16 20:29: 0.3.18 at ff0413d6, Linux only, asked by the Fairy-in-a-Bottle session for the user's game ("等 orb
  好了一起裝", no upload): auto-play stops at the run's end by RunManager.HasRunEnded (4d37eebd: a death Fairy in a
  Bottle or Lizard Tail prevents sits at 0 HP until the heal); the AI orb closes the combat marks and map route with
  the card (a61228ff). Its Linux brain on `run --check` (IRONCLAD A10, low, seed 1138): won, identical over the wire,
  49/49 nodes and 28/28 saves replay exactly, 0 game errors. Installed in `mods/` over 0.3.17 at 20:30, once the game
  had closed. Not uploaded.
- 2026-11-16 20:34: 0.3.18 at 30d20ef5 (the same code as ff0413d6, plans only between) uploaded on the user's word
  ("打包 mod 上傳 Workshop", in the Fairy-in-a-Bottle session), public, three platforms (the VM started and shut down
  again). The commit timed out once, but the content had gone through: the item updated at 12:34:46Z and its change
  notes carry 0.3.18's. The 17 languages' texts then went up without `--content`, all OK.
- 2026-11-16 20:45: the listing again, once the high report ended (20:42), on the user's word ("報告跑完再用 200 局更新簡介",
  "寫局數"): 200 runs of each, the Defect's Dread 199 (one failed run left out), with the run counts and one decimal
  where needed (rounding up would contradict the counts: 199/200 is not 100 %). Sent without `--content`: the 17
  languages at once, no timeout. Checked on the public page in English, Traditional Chinese and German.
- 2026-11-16 20:48: 0.3.19 at 947ddc07, Linux only, for the user's game: on a new profile the first act has no ancient
  (Neow, its only one, waits for its epoch), and reading it threw "RoomSet.Ancient not set! You must call
  GenerateRooms", which stopped the mod at the run's first map (the user's screenshot, A0 never played); the bridge now
  asks the room set first, as the game does. Its Linux brain on `run --check` (IRONCLAD A10, low, seed 1138): won,
  identical over the wire, 49/49 nodes and 28/28 saves replay exactly, 0 game errors. Installed when the game closes
  (it was running). Not uploaded: a new profile's run is to be tried in the real game first.
- 2026-11-16 21:56: 0.3.20 at 38042c90 (the same code as 38bf5dc2, plans only between), three platforms (3:25; the VM
  started and shut down again), for an upload on the user's word ("mod可以新版本了，包括docs部署"): 0.3.19's new-profile
  fix and the mod off for a run that started without Neow. Its Linux brain on `run --check` (IRONCLAD A10, low, seed
  1138): won, identical over the wire, 49/49 nodes and 28/28 saves replay exactly, 0 game errors; the nets are
  `weights/all/`'s byte for byte. The listing opens with multiplayer support coming and an invitation to subscribe and
  favorite the mod now (the user: "mod描述在最前面加上多人之後會支持，可以先關注訂閱加上這個mod"), all 17 languages.
  Upload waits for the game to close here (it was running).
- 2026-11-16 21:58: 0.3.20 at 38bf5dc2, Linux only, installed in `mods/` once the game had closed: the AI is off for a run
  that started without Neow (the game's StartedWithNeow, false on a new profile until the timeline reveals Neow's
  epoch), on the user's word ("照這樣做"): V valued such a run badly (a won floor-1 fight at 2 enemy HP dragged out for
  44 HP instead of 74, V-td4 not rising with HP there). Its Linux brain on `run --check` (IRONCLAD A10, low, seed
  1138): won, identical over the wire, 0 game errors. Not uploaded. The listing's IRONCLAD numbers and the Defect's
  Dread are from reports the docs session voided (game errors in 6 IRONCLAD runs, an engine failure in one Dread run);
  it sends the re-played numbers once the two fixes run.
- 2026-11-16 21:58: 0.3.20 (38042c90) uploaded on the user's word ("已關遊戲，上傳"), public, three platforms. The commit
  timed out, but the content had gone through: the item updated at 13:58:10Z and its change notes carry 0.3.20's. The
  17 languages' texts (multiplayer coming, at the top) then went up without `--content`, all OK; checked on the public
  page in English, Traditional Chinese and German. Installed in `mods/` over 0.3.19 with the game closed. The listing's
  IRONCLAD numbers are still the void reports' (200 / 168 / 70) until its replay at 800111d7 ends.
- 2026-11-16 22:12: the listing's Ironclad numbers from its reports played again at 800111d7 once the hook action's game
  error was fixed (step report-v11-0-td4-c11-0-ironclad, 600 runs, no game error, none failed): A0 199/200, A10 172/200,
  Dread 76/200, in place of the void 200 / 168 / 70; the Defect's Dread stays 44/199 (the game's own failure, per the
  docs plan). Texts sent without `--content`, the 17 languages at once; the item stays 0.3.20.
- 2026-11-17 16:22: 0.3.21 at b5e176d9, three platforms (4:12; the VM started and shut down again), for an upload on
  the user's word ("修完之後直接發版新的mod上傳", in the NoSL session; the number taken at HEAD by the switch session,
  the release session being idle): the panel no longer switches SL/NoSL, the environment is the last setting in
  Settings → AI under its own header; and what reached main since 0.3.20: NoSL's worlds drawn from the real combat in
  the engine (the mod sends the live combat in NoSL too), the champion pair carried to v12-0-td4 + c12-0 (the same
  play). Its Linux brain on `run --check` (IRONCLAD A10, low, seed 1138): won, identical over the wire, 49/49 nodes and
  28/28 saves replay exactly, 0 game errors; in NoSL (same seed, pimc 8x300): lost on floor 33, identical over the
  wire, 33/33 nodes and 18/18 saves replay exactly, 0 game errors. The nets are `weights/all/`'s byte for byte. Upload
  waits for the game to close here (it was running); the Mac's is closed.
- 2026-11-17 16:25: 0.3.21 (b5e176d9; since then plans and learn/ only, nothing in the item) uploaded on the user's word
  once the game had closed here, public, three platforms. The commit timed out, but the content had gone through: the
  item updated at 08:25:56Z (396.8 MB) and its change notes carry 0.3.21's. The texts were not sent again: none
  changed since their last upload. Installed in `mods/` over 0.3.20 with the game closed.
- 2026-11-17 16:46: 0.3.22 at 2693f7e0, three platforms (the VM started and shut down again), uploaded on the user's
  word ("得最快速度升級版本"), public. 0.3.21 threw at every event in the real game, seen on the Mac ("The type
  initializer for 'Sts2Ai.GameBridge.Actions.EventActions' threw an exception"): the mod compiles GameBridge's sources
  into Sts2Ai.dll, but EventTexts.json (embedded since the NoSL shared-nets step, bbdd890a) was a resource of
  GameBridge's project alone, so the mod's assembly held none; the brain references GameBridge and had it, which is why
  `run --check` passed. Sts2Ai.Mod.csproj now embeds it too; the brain is 0.3.21's. The commit timed out with the
  content through (updated 08:46:45Z, 396.9 MB, the change note on the changelog page). Installed in `mods/` over 0.3.21
  (moved to `tmp/modeng/old-installs/`) with the game closed.
- 2026-11-18 12:46: 0.3.23 at 3bd8582a, three platforms (4:14; the VM started and shut down again), uploaded on the
  user's word ("那d2, 開一個session更新gate,champion and mod"), public: the champion d2 of test v12-1,
  v-sl-td4-12-1-d2 + c-sl-12-1-d2 (A10 +10.0 and the Dread mode +9.0 points of wins against v12-0-td4 + c12-0). Its
  Linux brain on `run --check` (IRONCLAD A10, low, seed 1138): won, identical over the wire, 49/49 nodes and 27/27 saves
  replay exactly, 0 game errors; in NoSL (same seed, pimc 8x300): lost on floor 43, identical over the wire, 43/43 nodes
  and 24/24 saves replay exactly, 0 game errors. The nets are `weights/all/`'s byte for byte. Steam was logged on as
  another account at first; the user switched it back. The commit timed out with the content through (updated
  04:46:01Z, 397.1 MB, the change note on the changelog page); the texts were not sent again (none changed). The
  listing's numbers are still v11-0-td4 + c11-0's. Installed in `mods/` over 0.3.22 (moved to `tmp/modeng/old-installs/`)
  with the game closed.
- 2026-11-18 12:48: the listing's numbers are the champion d2's (the user: "換成 d2 的"), from test v12-1's reports at
  high: A0 / A10 / Dread, Ironclad 199 / 175 / 92 of 199, Silent 198 / 151 / 50, Defect 199 / 161 / 61 of 198, Regent
  196 / 144 / 43, Necrobinder 193 / 127 / 40, the Fool 187 / 74 / 15 (of 200 but where named). Texts sent without
  `--content`, the 17 languages, all OK; checked on the public page in English, Traditional Chinese and German. The
  item stays 0.3.23.
- 2026-11-20 01:14: mod 0.3.29 uploaded (the user: 升级mod+推送): Stop at Ancients and Stop at combat rewards (two
  Workshop players' requests, both off by default), the panel's SL/NoSL switch back, search effort Low / Medium / High /
  Max with Medium the default and no Brain option, the 3-point stop removed. Built at HEAD for all three platforms; the
  packed brain answers the SL and NoSL wires exactly as 0.3.28 did (d2 and v0-nosl-1-2). The Mac's game was closed by
  the user first. The first commit timed out with the content through (updated 17:14:38Z, the change note on the
  changelog page); the texts were not sent again (none changed). This machine's subscribed copy equals out/Sts2Ai;
  installed in `mods/` over 0.3.28 (moved to `tmp/modeng/old-installs/`). The Mac has no local copy and takes the
  subscription.
