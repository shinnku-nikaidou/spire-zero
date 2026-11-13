# DONE: figures, figure data, and the pages that quote them

Not committed. `pnpm --dir docs check`: 165 pages, 6,536 internal links, 0 problems. `tmp/docs-host-scan.sh docs`:
0 hits. The plays in `tmp/docs-play/` were not rerun (all inputs were there).

## 1. Data, and how it is made

**`docs/scripts/book-data.py`** (new; `uv run --project learn python docs/scripts/book-data.py tmp/docs-play`). Reads
the two plays of the book's example seed (`0SSL7NS01MU6`, the Silent, A0; `run --champion eval/champions/sl.json` and
`eval/champions/nosl.json`, `--start 12 --n 1 --records FILE --log FILE`; the commands are in the script's docstring),
the champion's reports named in `eval/champions/sl.json`, and the champion's two net files. Writes:

- `src/data/book/example-run.json`: the SL run (won on floor 48, 997 decisions, 772 in combat; acts Overgrowth/Neow/
  Vantom, Hive/Pael/The Insatiable, Glory/Tanx/Queen).
- `src/data/book/example-nodes.json`: its first three nodes (floors 1-3).
- `src/data/macro-examples.json`: source, the rest site and map decisions, three real decision-log lines (combat 4,
  card prompt 1, rest site 133), the series of V along the run.
- `src/data/book/learning-td.json`: the champion's V (chance to win) at the run's 174 decisions with a run view,
  outcome G = 1.
- `src/data/book/panel-examples.json`: the four entries (SL combat decision 943, NoSL combat decision 863, map decision
  341 with its options, the map of decision 341 with the logged route). Texts made as `Describe.cs` and `L10n.cs` make
  them (the L10n table parsed for zh-CN/zh-TW).
- `src/data/combat-examples.json`: `sl` from the SL run's fight on floor 17 (Vantom); `nosl` kept, its source reduced
  to seed, floor, outcome, how; `panel` and `sl.stats` deleted.
- `src/data/book/surface-counts.json` (the champion's Silent report; example = the SL play), `eval-reports.json` (the
  champion's Necrobinder report), `learning-netfiles.json` (keyed `v` / `c`, file names from the files).
- `round-start.json`, `eval-pairs.json`: sources stripped only (data unchanged).

Every source keeps only what remakes it: the command with `eval/champions/*.json`, the seed, a report by its role. No
commit, date, job folder, old net name or run id.

How macro option values are made (the decision log does not hold L0's candidates): a map move's value is V of its
entered afterstate, computed exactly in Python (checked against `v9-2.json`'s tests); any other chosen SL option's value
is V of the run at the node's next map decision (verified on old values); the rest site's HEAL is V of the run view
with HP raised by the game's heal rule (30% of max HP); NoSL L0's leave/proceed/rewards_done values are V of the view.

**`docs/scripts/combat-walk.py`**: the combat net now comes from `eval/champions/sl.json`'s `combat-net`
(`weights/all/c9-2.json`). MAIN = `tests[11]` (the only test state holding a potion: the Regent against a Slimed
Berserker, Fairy in a Bottle); OTHERS = `tests[3]` (largest chance of a death: the Silent against the Knowledge Demon)
and `tests[2]` (lowest HP: the Ironclad at 14 of 86 against the Decimillipede). The example card is the first plain
attack with a copy in another pile (MAUL, draw pile). Regenerated `src/data/combat-walk.json`; the script still stops
unless its layers end in the file's outputs.

**`docs/scripts/game-names.mjs`**: adds a `rest` table (rest site option names, `rest_site_ui` `OPTION_*`); the three
names files gained only that key. `src/components/v/names.ts`: `Kind` has `'rest'`.

## 2. Components

No hash, date, old net/generation name, `p0`, "earlier V/combat net" or socket left in visible text, captions or
comments; captions in English, zh-CN and zh-TW.

- NetWalk: every step recomputed for `v9-2.json` `tests[0]` (Defect, floor 24, an unknown room; P(win) 0.172).
- TdTargets: chance-to-win axis (0 to 1), act starts from the data, new caption (no run id).
- DecisionLog: the three real lines (decisions 4, 1, 133), the champion's names, caption.
- WireExchange: `sl.json`'s champion name, `combat prompts first`, recommendations carry `candidates`, caption.
- PanelMock: no hash; the starting and offline messages as `BrainLink.cs` / `Assist.cs` write them; captions from the
  data for the three modes.
- MapRoute: localized labels, room types from the data, generic caption.
- SurfaceCounts: "answered by" with `first` and the searches; no commit.
- DeathsByFloor (Necrobinder report; zh-TW 縛亡者, 頭目), NetFileAnatomy (files `v`/`c`, tabs by file name),
  RestoreCost and Scaling (no dates), ObservationExplorer and Determinize (no "earlier combat net"), WilsonInterval
  ("example counts"), OutcomeHead comment, SprtReplay ("the second net as the challenger"), RunTimeline (act starts
  from data).

## 3. Pages changed (en, zh-CN root, zh-tw; no heading renumbered or retitled)

- **arch/game** §1: 24 combats (18 monsters, 3 elites, 3 bosses), 8 rest sites each an upgrade, 6 `?` points (5 events,
  1 monster), 5 shops, 3 treasure rooms, 997 decisions, 772 in combat. §2: Hive 26 monsters, 10 unknowns, 9 rest sites,
  5 elites, 5 treasures, 3 shops; the route planned on floor 19. §8: about 830 decisions on average (83,026 in the
  champion's 100 SL runs of the Silent), the example run 997; "22 or so fights"; "in those 100 SL runs"; combat 73%.
- **arch/simulator/runs**: 997 decisions; HP 0 of 70; the output line `#12 0SSL7NS01MU6 won act 2 floor 48 room
  THE_ARCHITECT hp 0/70 gold 160 ... decisions 997 403012 ms`.
- **arch/sl-planner/index**: the example fight is the run's floor-17 Vantom fight (39/70 HP, 7.0 s, 34 actions over
  9 rounds, the poison plan, Footwork+ Dexterity 2 to 5, round 7's three Defends against 21, round 9 ends with Vantom
  at 22 HP and 34 poison; plan value 0.8892), link to The game §1.
- **arch/macro-brain/index**: 181 -> 174 macro decisions.
- **arch/macro-brain/network** §9: the champion's file's test runs (`tests[0]`), floor 24.
- **arch/learning/loop** §3-§4: 174 decisions; V is the champion's and values the chance to win; outcome 1; V starts at
  0.27; first 60 decisions 0.36 above V; λ = 0.9 0.07 above; credit in act 1 (card rewards floors 2-6, rest site floor
  8, shop floor 12), V near 1 from act 2.
- **arch/learning/reference** §7: "the champion's two net files, V's and the combat net's".
- **arch/combat-brain/walkthrough**: the state is `weights/all/c9-2.json` `tests[11]`; §1 the Regent in act 3's Slimed
  Berserker fight (stars row added, Thorns 3 from Bronze Scales, one enemy table, 10 Slimed from last round); §2 one
  enemy, its slots, relic counters (three relics show one); §3 shapes (32 cards, one enemy, eight relics, padded
  enemy powers); §4 MAUL in the draw pile, damage 10, sum −0.097 + 0 + 0.158 − 0.049 − 0.082 + 0.083 = 0.013; §5
  Thorns 3; §6 one enemy, zero powers' sum; §7 eight relics; §11 share 0.521, no bucket closed at the bottom, HP-lost
  column computed, 83% of the chance on losses of 0 to 0.25; §12 Fairy in a Bottle gone after about one win in
  eleven (0.092); §13 0.908 kept, 26.7 of 71 HP on average; §14 10 HP less (death 0.31% to 0.85%, a win costs 7.1 HP
  against 10.3) and the potion gone (death to 1.09%); §15 Knowledge Demon (death 48%) and Decimillipede (14 of 86 HP,
  17 buckets open, Burning Blood's 6 HP); §16 label at 30 of 71 HP. Most numbers are expressions over
  `combat-walk.json`.
- **arch/output** §2: "win chance 87.7%", "this fight 100% · 53 HP left", "11 more turns planned".
- **arch/mod/index** §2: the map on floor 19 (act 2, 70 of 70 HP); legal `map:3,2` and `map:5,2`; answer `map:5,2`.
- Checked, no change needed: evaluation/metrics (the Necrobinder report's numbers already match: 47 won, 53 losses:
  43 boss, 8 elite, 2 monster; bosses beaten 87/95, 75/83, 47/74; the secondary table), environments/information,
  harness/decision-log, protocol/wire, combat-brain/leaf §4 and network §8, pimc pages (their `nosl` data is kept).

## 4. Notes for the lead

- `macro-examples.json` is imported by no component; it is the source the DecisionLog and WireExchange comments name.
- The DecisionLog figure still shows the log line's real `run` field (a hash of the seed), as the format it explains.
- `run` ids were removed from every data source and from the TdTargets caption.
