# The work pool

Self-play, reports, gates and other batch jobs of the simulator (`run`) can run on
one work pool spread over many machines. One server, **the hub**, holds every open job's queue of chunks and deals the
next chunk to whichever worker thread anywhere is free; each machine runs one worker, and its number of threads is its
only knob.

| file | runs on | what it does |
| --- | --- | --- |
| `box.py` | this checkout | starts and stops workers, opens jobs on the pool, moves deadlines, shows status, fetches results |
| `worker.py` | every machine | one worker: its threads ask the dealer for chunks, play them, and send them to the hub |
| `deal.py` | the hub | the dealer (`hub.sh serve` installs it as the service `sts2-deal`) |
| `dist.py` | the hub | the HTTPS file service: builds, inputs, the worker's code in; finished chunks out (`dist-admin.py setup`) |
| `dist-admin.py` | this checkout | the file service's key and certificate, the build bundles workers download, the install line for a new machine |
| `install.sh` | a new machine | puts the simulator under `~/sts2` and lets this checkout in by ssh (served by `dist.py`) |
| `hub.sh` | this checkout | the data channel to the hub (rsync over ssh) and the hub's upkeep |
| `on.sh` | this checkout | runs a script on a machine of the pool, as our user there |

Each script's usage is at its top. Standard library Python 3.10+ (3.11+ for `box.py`, which reads TOML) and bash; the
machines also need `zstd`, `tar` and `rsync`.

## Settings

The site's private settings live in the repository's `.env` (not in git):

```sh
STS2_HUB=203.0.113.10               # the hub's address
STS2_HUB_DEAL_PORT=39230            # deal.py's port
STS2_HUB_DIST_PORT=39231            # dist.py's port
STS2_ON_hub="203.0.113.10"          # optional: hosts whose shell goes through nyctl (tmp/nyctl), as
STS2_ON_box1="box1@198.51.100.7"    # "TARGET [nyctl option...]"; any other host name is an ssh alias
```

The file service's key is `~/.config/sts2/dist-key` (mode 600, made by `dist-admin.py setup`); the hub keeps only its
SHA-256. Each machine reaches the hub's store with its own key `~/.ssh/sts2_hub` (`hub.sh key HOST`), which the hub
lets do nothing but rsync inside the store.

## The usual job

```bash
pool/box.py up box1 16                                  # once per machine: its worker, at nice 19, with 16 threads
pool/box.py up box2 '19:00-06:00 12' 4                  # threads by time windows in UTC+8, then the rest of the day
pool/box.py threads box1 8                              # the only knob, read within 10 s
U=@$(pool/box.py until '+72h')                          # one deadline for every job of a step
DEADLINE=gen1 pool/box.py run silent/sl/selfplay/gen1/a REV $U 'purpose' 1000000:6000 -- RUN-OPTION...
DEADLINE=gate1 pool/box.py run silent/sl/eval/NAME REV none 'purpose' all -- --seeds eval/seeds/X.txt ...
pool/box.py status                                      # from the hub: runs, wins, floors, hosts, free space
pool/box.py fetch gen1                                  # returns once every chunk is on the hub
```

REV must be a commit on `origin/main`: the job's build is made from it, and its seeds and champion files (`eval/seeds`,
`eval/champions`) come from git at REV. The net files it reads (its `--value`, `--combat-net` and
`--f-net`, or its champion file's) come from `weights/all` in this checkout, where `weights/fetch.sh` brings them from the repository's release;
a net's name is used once, so every machine plays the same thing.

## A dataset

A self-play dataset is one file, `eval/datasets/NAME.toml` (`eval/datasets/example.toml` shows every key): the block of
seeds, the characters, the champion file it plays, the exploration tiers (each a uniform draw's chance ε and a softmax
temperature τ on log V, and it may tilt a run's cards: `favor` with `favor_by`, `refuse`, `keep` and `upgrade_first` name
card families of the file's `[families]`; l0 weighs each afterstate with a bonus of `favor_by` in log V for each favored
card it adds, never lets a refused one in nor a kept one out, and an upgrade's prompt takes a card to upgrade first
first; run's `--favor`, `--favor-by`, `--refuse`, `--keep` and `--upgrade-first`), the games (which acts win their plain monster fights at their start, a fight's effort by
its room: monster, elite, boss, and the game mode: `mode = "dread"` for the Dread mode, played at ascension 10 only, or
none for the standard game; `env = "nosl"` for a NoSL game, played by the file's `nosl_champion`, with no effort), and
rows of runs, each a game, ascensions and counts, for every character or for the ones it names. SL's jobs go to `CHAR/sl/selfplay/NAME/...`, NoSL's to `CHAR/nosl/selfplay/NAME/...`, one step.

```bash
pool/box.py plan eval/datasets/NAME.toml                # the jobs, their options and seeds, the shares; nothing sent
pool/box.py launch eval/datasets/NAME.toml REV --smoke  # one run per character, row and ascension, 1 hour
pool/box.py launch eval/datasets/NAME.toml REV          # the step NAME
```

`launch` reads the file at REV and turns each of its jobs into what `box.py run` opens, so the hub deals exactly what
the file says. Character k of SILENT, IRONCLAD, DEFECT, REGENT, NECROBINDER, FOOL plays seeds from `seeds + 100000 k`, a
ledger block of 100,000 each (so a file takes six blocks), its rows
back to back in the file's order, each row's ascensions in order, each ascension's runs split evenly over the tiers; the
last 1,000 of a character's block are the smoke's, so a character plays at most 99,000 runs. A job is `CHAR/sl/selfplay/NAME/aNN-GAME-eEEE[-tTTT][-fA.B-bBBB][-rC][-kD][-uE]` (ε and τ in
hundredths, then the families favored with their bonus in hundredths, refused, kept and upgraded first). The smoke plays one more run for each
other card lock a row's tiers have. The file then lies beside the data, its first line `rev = "REV"`, as `CHAR/sl/selfplay/NAME/dataset.toml`
in each character's folder, here and on the hub.

## Words

- **Job**: one folder `data/JOB` (here: `manifest.json` and the small files; on the hub: everything), one queue of
  chunks on the hub, one REV, one set of options and inputs. Launched once.
- **Step**: the jobs launched with one `DEADLINE=NAME`. A step has one deadline and one priority. Its file is
  `data/pool/steps/NAME.json` here (the truth) and `pool/steps/NAME.json` on the hub (what the dealer reads).
- **Worker**: one process per machine (`~/sts2/pool/worker.py`), running N threads. A thread plays one `run` at a time.
  Its shard is `HOST-i`.
- **Chunk**: CHUNK runs (default 1) of a job's range, the unit a thread is dealt.

## The order

On each request the dealer walks the open steps (not stopped; deadline not passed, or none):

1. **Between steps**: higher priority first, then the step launched first. Default: `selfplay` 0, `eval` and `forks` 1
   (`PRIORITY=N` at launch, `box.py priority NAME N` live). A gate launched during a self-play step takes every thread
   as its runs end; nothing is killed.
2. **Inside a step**: the job with the lowest share of its queue dealt (dealt / queue length). Equal jobs move through
   their seed file together, so a gate's pairs stay on the same seeds, and a step cut by its deadline keeps its planned
   mix.
3. **A job that keeps failing**: when a host's chunks of a job end with fewer runs than dealt twice in a row, that host
   gets no more of it (`JOB/deal/refused/HOST`; `status` shows it).

## Deadlines and stops

- **Self-play and forks** have a deadline (`UNTIL`: `+72h`, `@EPOCH`, or anything `date -d` reads). A run in play then
  is killed and its chunk sent with the runs that finished; its seeds are not dealt again. `box.py deadline NAME UNTIL`
  moves it either way. Give a data step far more seeds than fit.
- **Eval** has none (`UNTIL` is `none`): it ends when its queue is used up and every chunk it dealt is back, so a gate
  is always whole and paired.
- `box.py stop JOB|NAME REASON`: nothing more is dealt, and runs in play are killed within about a minute; finished
  runs stay.

## Seeds go back to the hub

A chunk dealt but not finished goes back to the front of its job's queue, and the next free thread anywhere takes it,
when its thread is cut (`box.py down HOST`, fewer threads, the end of a threads window), when its host's worker starts
again, or when its host has not been heard from for 15 min. A chunk taken back is refused from its old holder later
(`dist.py` answers 409), so no seed is counted twice. `JOB/deal/claims.log` has a line per chunk dealt and taken back.

## Workers

- `box.py up HOST THREADS...` ships `worker.py` and `config.json` (the file service's URL, its key, the hub's
  certificate; mode 600 in `~/sts2/pool`) and starts the worker at nice 19. A worker already running there is cut first,
  so `up` is also how a new `worker.py` goes out. `box.py down HOST` ends it: its runs cut, their chunks back, its
  outbox sent. `up HOST THREADS --only KIND` starts a worker that takes only that kind of job (selfplay, eval, forks):
  for a machine too small for some steps' runs.
- A thread asks the dealer (`/next`), plays the chunk with `sim-REV.sh run OPTIONS --start INDEX --n N --log --records`
  in the job's inputs, and leaves it in `~/sts2/pool/outbox/`; one sender thread PUTs the outbox to `dist.py` in order
  and tries again until the hub takes it.
- **Played again on failure** (`~/sts2/pool/retry`, a number, none by default): for a machine whose memory flips bits.
  A chunk that fails there (a run exit with fewer runs than dealt, or a record whose outcome is failed) is played
  again up to that many times before it goes up; the same seed on the same machine gives the run the flip spoiled, and
  a failure that comes back every time goes up as before. Each attempt played again leaves a line in the chunk's
  `out-` and a `sum played again: N` line in its `tally-`.
- **Builds and inputs on first need.** The first thread that needs a job's REV downloads `dist/sts2-REV.tar.zst`
  (`dist-admin.py bundle`: the build, and .NET and the game files a new machine lacks) and `dist/inputs-KEY.tar.zst`;
  the machine's other threads wait. `box.py run` puts both on the hub first.
- **Each chunk sends** `rec-SHARD-INDEX.jsonl.zst` (run records), `log-` (decision logs), `out-` (output), `counts-`,
  `tally-` (a line per run, what `status` and `fetch` read) and `done-` last (the finished indices).

A machine's layout:

```
~/sts2/                       mode 700
├─ dotnet/                    the .NET runtime only
├─ gamebin/v0.113.1/          the game's assemblies the simulator loads
├─ builds/REV/release/        a build
├─ sim-REV.sh                 its launcher
├─ home/                      the redirected HOME
├─ inputs/KEY/                a job's inputs
└─ pool/                      worker.py, config.json, threads, retry, worker.pid, worker.log, outbox/, work/SHARD/
```

## Looking and collecting

- `box.py status [NAME|JOB...]` reads the hub in one call: per job runs, wins, floor, chunks dealt and in play, game
  error counts; per host threads, playing, busy CPUs and its last look; the hub's free space.
- `box.py fetch NAME|JOB` waits until each job is over and every chunk dealt has its `done-` on the hub, copies `done-`,
  `tally-` and `deal/` here, closes each manifest with the pool's numbers (runs, wins, floor, chunks per host, ends),
  and once the whole step is fetched takes it off the hub.

## Tests

```bash
python3 -m unittest discover -s pool/tests -v
```

`pool/tests/test_pool.py` plays the pool end to end on one machine: the real `deal.py`, `dist.py` (over TLS, with a
certificate made for the test) and `worker.py` processes on a throwaway store, with `fakesim.py` for the simulator.
It needs `python3`, `openssl`, `zstd` and `tar`, no game and no other machine, and takes about 20 s. It covers the
order chunks are dealt in, chunks going back (fewer threads, a worker ended, killed or silent), stops and deadlines, a
job launched again under its name, and a host whose chunks fail. `WORKER_PACE` (below 1) shortens a worker's waits
and `DEAL_SILENCE` the dealer's patience: the tests set both, nothing else does.
