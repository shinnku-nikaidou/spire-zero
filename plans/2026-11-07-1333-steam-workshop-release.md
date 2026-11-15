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
