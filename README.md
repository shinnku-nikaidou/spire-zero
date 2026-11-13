# Spire Zero

An AI for **Slay the Spire 2** that learns to play from zero by self-play and runs as a mod inside the game.

- **Assist**: the mod shows what the AI would do, its plan for the turn and the options it weighed, each with its
  chance to win the run.
- **Auto-play**: on request the AI plays a step, the rest of a turn or a room, or keeps going until paused.

Every decision is made online by searching the options and judging where they lead with learned value functions,
one for combat and one for the whole run. No strategy is written by hand and nothing is learned from human games: the
nets learn it from the AI's own runs. Training runs the game's own `sts2.dll` headless, so the rules are the real
game's, not a model of them.

This is an unofficial fan project, not affiliated with or endorsed by Mega Crit. It contains no game files: building
and running it needs your own copy of the game, pinned at **v0.113.0** (the `public-beta` branch).

## Layout

| path | what |
| --- | --- |
| `src/` | the .NET solution (`Sts2Ai.sln`): `Protocol` (the messages), `GameBridge` (reads and drives the game), `Brain` (the combat and macro brains), `Harness` (dispatch, records, reports, gates), `SimWorker` (the headless simulator and its command line), `Mod` (the in-game mod), `Tests` (the decision side's, which need no game files) and `SimWorker.Tests` |
| `learn/` | offline training in Python (PyTorch): the run value V and the combat net, from self-play logs; the gate |
| `weights/` | the nets, shared by all six characters: `weights/all/NAME.json`, kept in the repository's release `weights` and not in git (`weights/fetch.sh` downloads the champions' nets, `weights/put.sh NAME` adds one) |
| `eval/` | test seed files, champion files, reports and the ledger of nets and seed blocks |
| `pool/` | the work pool that spreads self-play and evaluation over many machines ([pool/README.md](pool/README.md)) |
| `distro/` | the script that builds the mod with its brain for every platform |
| `docs/` | the design manual, a Starlight site in Simplified Chinese, Traditional Chinese and English ([docs/README.md](docs/README.md)) |

## Building

The game's assemblies are referenced, not shipped: copy `sts2.dll`, `GodotSharp.dll`, `SmartFormat.dll` and
`0Harmony.dll` (and what they load) from the game's data folder to `tmp/gamebin/v0.113.0/`, or pass
`-p:Sts2BinDir=PATH` (see `src/GameReferences.props`).

```bash
weights/fetch.sh                         # the champions' nets, which runs, some tests and the manual's check read
dotnet build Sts2Ai.sln -c Release
dotnet test src/Sts2Ai.Tests             # the brain and Harness: builds and runs without the game's assemblies
dotnet test src/Sts2Ai.SimWorker.Tests   # the simulator's command line and loadouts
cd learn && uv sync && uv run pytest     # the training code
pnpm --dir docs install && pnpm --dir docs dev   # the manual at http://localhost:4321
```

`distro/build.sh` makes the mod in `out/Sts2Ai/`, with a self-contained brain for Linux, macOS (Apple silicon) and
Windows; installing it is copying that folder into the game's `mods/` folder.

## Private settings

Machine addresses and keys never go into git. Scripts that need them read the repository's `.env` (ignored by git):
the work pool's hub and hosts (`pool/README.md`) and `STS2_MAC`, the Mac that signs the macOS launcher
(`distro/build.sh`).

## License

MIT ([LICENSE](LICENSE)). Slay the Spire 2 and its assets belong to Mega Crit and are not covered by this license.
