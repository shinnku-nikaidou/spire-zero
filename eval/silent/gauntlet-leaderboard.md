# SILENT elite gauntlet leaderboard

Kept by hand by the training loop (plan v6), since `leaderboard.md` is generated from whole-run reports and the
gauntlet's are not among them. The elite gauntlet: `run --gauntlet` (Monster combats won at their start, Elite and Boss
combats fought by plan c2 at the low budget, HP and potions carried), `--afterstates clone`, the eval seeds
300000–300499 (`--seed 1`), 500 runs each. Progress is the floor reached plus the share of enemy HP taken in the
combat the run died in; `learn/gauntlet.py` reports it and pairs it by seed. A new V is promoted when the 95 %
one-sided lower bound of its paired progress difference against the champion is above 0. Ledger: `ledger.md`.

| V | date | commit | progress | act 1 / 2 / 3 boss beaten (of reached) | won | elites a run | elite deaths | paired | report (under `data/silent/`) |
|---|---|---|---|---|---|---|---|---|---|
| v6-3-2 **(champion)** | 2026-10-30 | `b013f95` | 43.36 ± 0.38 | 480/499, 352/471, 173/341 | 173 | 1.37 | 21 | +2.67 ± 0.49 against v6-2 (lower 95 % +1.87); +0.94 ± 0.49 against v6-3-1 | `sl/eval/2026-10-30-gauntlet-v6-3-2` (+ `-b`) |
| v6-3-1 | 2026-10-30 | `b013f95` | 42.42 ± 0.43 | 469/496, 335/458, 154/324 | 154 | 1.41 | 26 | +1.73 ± 0.54 against v6-2 (lower 95 % +0.85) | `sl/eval/2026-10-30-gauntlet-v6-3-1` |
| v6-2 | 2026-10-30 | `b013f95` | 40.69 ± 0.38 | 483/500, 262/479, 68/244 | 68 | 1.25 | 21 | +4.62 ± 0.46 against v6-1 (lower 95 % +3.86) | `sl/eval/2026-10-30-gauntlet-v6-2` |
| v6-1 | 2026-10-30 | `b013f95` | 36.07 ± 0.34 | 475/499, 122/454, 10/103 | 10 | 1.21 | 39 | +1.60 ± 0.44 against pure-1-g (lower 95 % +0.88) | `sl/eval/2026-10-30-gauntlet-v6-1` |
| pure-1-g | 2026-10-29 | `b013f95` | 34.47 ± 0.33 | 469/497, 97/412, 2/61 | 2 | 2.37 | 95 | – (first champion) | `sl/eval/2026-10-29-gauntlet-ref/pure-1-g` |
| n2-b0.03 | 2026-10-29 | `b013f95` | 33.71 ± 0.35 | 441/499, 81/429, 1/56 | 1 | 0.64 | 27 | −0.76 ± 0.45 against pure-1-g | `sl/eval/2026-10-29-gauntlet-ref/n2-b0.03` |
| pure-1-g, every macro decision drawn evenly (`--explore 1.0`) | 2026-10-29 | `b013f95` | 21.89 ± 0.35 | 178/442, 5/109, 0/2 | 0 | 1.54 | 125 | −12.58 ± 0.46 against pure-1-g | `sl/eval/2026-10-29-gauntlet-ref/random` |
