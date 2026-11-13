# DONE: group A (introduction, Part I, appendix)

All changes in English first, then mirrored in zh-CN (root) and zh-TW, same structure. Twins checked by script: the
same numbered headings, the same table row counts and the same code spans in all three locales of every page. No heading
renumbered or retitled. Not built, not committed. Code read at HEAD 70e9a25 / a0071e4 (after the coordinator's note).

## Pages

**index.mdx**
- description: "how well it plays today" -> "how well it plays".
- intro: "The first target is the Silent ... A0, then A10" -> single-player only, one set of nets for all five characters
  at every Ascension level. Unattended whole runs stay as planned (D1).
- "How well it plays": FACTS' report numbers (Silent 92 of 100, 85.0-95.9 %; 82 / 71 / 68 / 47 % for Ironclad, Regent,
  Defect, Necrobinder), linked to evaluation/champions §2; the A10 and old-V sentences dropped; NoSL: plays the same two
  nets, learned from SL's games, not trained or gated in NoSL; the NoSL champion has no report.
- §1 idea 5: "A random decision voids" -> "A decision by a random placeholder voids" (`Harness/Reports.cs:39-41`).
- §3 mermaid edge "Unix socket" -> "stdin / stdout"; the brain-process bullet says the mod starts `brain` as a child
  process and talks on its stdin/stdout.
- §4: `eval/` without the ledger; `distro/` added (build.sh builds the Workshop item into `out/Sts2Ai/`: the mod, a
  self-contained brain per platform, and the NoSL champion's file with its nets, which serves both environments; link
  `/en/arch/mod/#sec-3`); `data/` "where self-play and evaluation write their logs and records; not in git"; no `pool/`.
- §5: "four parts and an appendix"; Environments badge decided -> built (已实现 / 已實作); appendix line is the glossary
  only; "how strong" route -> Evaluation + Evaluation: champions and baselines.
- §6: `tmp/decompiled` and `tmp/notes` pointers dropped ("The decompiled game is not in the repository"); "Maintaining
  it" removed (now in docs/README.md, no Claude sentence). Kept two reader-facing lines: section numbers stay because
  code comments cite them (folded into the `#sec-N` bullet) and "the English pages are the reference version".

**arch/game.mdx**
- intro: `tmp/decompiled/sts2` -> "paths in the decompiled game, which is not in the repository".
- §1: "won on floor 48 by the SL champion" -> "an SL run of the Silent on seed `0SSL7NS01MU6`, won on floor 48".
- §5: "trains first" -> "the character of the book's example run".
- §6: "plays at level 0 so far" -> the champion's reports are at level 0; self-play covers 0-10 (link
  macro-brain/selfplay); `run --asc N` plays any level.
- §8: "300 SL runs of the champion" -> "300 SL runs of the Silent"; "the SL champion's report" -> "those 300 SL runs".

**arch/environments/index.mdx**
- badge decided -> built; Status aside: "Decided ... that exists" -> "Built, and in force in every part of the code";
  "today" dropped (planned counterfactual tests / reload count stay).
- §3: "a stepping stone" -> "the training environment"; "today's ... yet" -> present; the whole-run paragraph rewritten
  as rationale ("There is no whole-run environment: ...").
- §5: mod row: `settings.json` in `$XDG_STATE_HOME/sts2ai` (default `~/.local/state/sts2ai`); `brain` row: NoSL hosts
  get L0 on the screen's afterstates, SL hosts L0 on the node's clone (`BrainCommand.Champions`, after 70e9a25);
  citations `RunCommand.cs:66` -> `:61`, `Dispatcher.cs:174` -> `:182`, `BrainServer.cs:112-123` -> `:61`, `:71-81`.

**arch/environments/information.mdx**
- §2: the "Rejected" Aside deleted (overview §3 holds the argument); `Aside` import dropped.
- §3 "No search does this today" -> present; §4 "was chosen anyway" -> "The node is the boundary anyway";
  §5 "Today NoSL's worlds" -> present (the full posterior stays planned).
- §6: the `tmp/notes/fair-play-spec.md` pointer -> "Rulings ... which the observation whitelist implements";
  citation `Observe.cs:259-261` -> `:261-263` (the `Powers` lines at HEAD).
- §7: relic story -> rule (the save would drop relic fields; the checkpoint carries their plain fields; `run --check`
  replays every node from its checkpoint and fails when a clone differs); `Assist.cs:275` -> `:277` (moved in 70e9a25).
- §8: intro "has, or will have ... today" -> "has a guard ..., built or planned"; guards 4, 5 "today" dropped;
  guard 10: `brain --wire FILE` logs node messages, which the mod passes (with `--log`) when settings' `Record` is on;
  `SelfTest.Run.cs:134-174` -> `:134-166`. Planned guards stay (D1).
- §9 kept (D1): "(today)" -> "(in force)", "Once full runs are common" dropped.

**arch/environments/worlds.mdx**
- §1 "Today every world" -> present (planned NoSL run worlds stay); §2 "Not modelled yet" -> "Not modelled";
  Determinize prose: "training data of an earlier combat net" -> "a real start of a round from NoSL self-play".
- §3 "turns out to matter" -> "matters"; §4 "today" dropped.
- §5: the `brain` sentence adds L0 on the screen's afterstates for NoSL hosts, on the clone for SL hosts.
- §6: c9-1 -> "the champion's combat net", "has never been trained" -> "is not trained or gated there".
- §7: false claim fixed: "Both files name the same V and the same combat net. The NoSL file names no report, so a NoSL
  gate refuses every character" (link evaluation/champions §1; the refusal is `EvalCommands.cs:92`). Explanations
  bullet stays as planned.

**glossary.mdx**
- Silent: "one of the five characters; the book's example run is hers"; `brain`: the mod starts it as a child process,
  stdin/stdout (no `run --brain`); whitelist: JSON round trips check every observation (`run --check`, `selftest`), no
  "each field has a test"; placeholder: "a decision by a placeholder voids an evaluation"; leaderboard
  `eval/CHARACTER/leaderboard.md`.
- §7: v9-1 / c9-1 -> v9-2 / c9-2 ("the champion's V / combat net"); `weights/all/NAME.json`: the champions play
  `v9-2.json` and `c9-2.json`, a name is never reused; champion files: "the reports behind it (the NoSL file names
  none)"; ledger row and `tmp/build.sh` row deleted; `data/CHARACTER/{sl,nosl}/…` "not in git".
- TurnEnds, iterative widening, SPSA, ExIt kept (D1).

**status.mdx** deleted in all three locales. Its only inbound links were index §5 (x2 per locale), retargeted.

**docs/astro.config.mjs**: `book`'s last group is `附录` / `附錄` / `Appendix` with `['glossary']`; the comment says "the
appendix". Nothing else touched.

**docs/README.md** rewritten: what the site is, commands (incl. `pnpm run deploy`), the sidebar, figures (Svelte islands
in `src/components/{book,combat,v}/`, data in `src/data/`, `combat-walk.py`, `game-names.mjs`), what `pnpm check`
checks, "Maintaining it" (English reference, three locales in one commit, stable `#sec-N` cited by code, zh-TW
wording, English mermaid/code), and "What the book may not say". No `weights/silent/v8-3.json`, no `src/lib/v/`, no
Claude sentence.

**docs/wrangler.jsonc**: `account_id` and the account-naming comment removed; the comment says the account comes from
`CLOUDFLARE_ACCOUNT_ID` in the environment.

## Left on purpose

- Planned content (D1): unattended whole runs (index intro), planned guards 4, 5, 6, 9, 10, information §9, NoSL run
  worlds, ISMCTS, explanation facts, glossary's planned terms.
- environments/index §5: the bullet "SL and NoSL play the same combat net file" stays under "What refuses the wrong
  environment" (the audit suggested moving it to §4; the plan row did not ask).
- environments/index §3: spire-mentor's 22.8-25.5 pp stays without a source (audit item 12; prior art, not history).
- environments/index §4: "a check that is planned for the mod's logs" (NumReloads) stays as planned.

## For the figures step (sentences waiting for regenerated data)

- game §1: "an SL run of the Silent on seed `0SSL7NS01MU6`, won on floor 48" and the line after it (28 combats ...
  848 decisions, 631 in combat).
- game §2: "The example run's maps had 62, 60 and 58 points ...".
- game §8: "about 555 decisions on average (166,527 decisions in 300 SL runs of the Silent), the example run 848",
  "Where the decisions are, in those 300 SL runs:", "Combat is 71% of all decisions" (the champion's Silent report gives
  73 %, 830 a run; rewrite these when `surface-counts.json` comes from the champion's reports).
- worlds §2: the Determinize caption in the component still says "an earlier combat net's file"; the prose now says
  "a real start of a round from NoSL self-play" (matches `round-start.json`'s own `what`).
- information §3: NodeTimeline prose ("the won SL run ...") is clean; its data `source` still names old nets.
- The ObservationExplorer and SurfaceCounts captions (game §4, §8) are components: not touched.

## Cross-page links

- Changed: index §5 status links -> `/LOCALE/arch/evaluation/champions/`; index intro links champions `#sec-2` (needs
  that section, retitled "Reports" by group D); worlds §7 now links `/LOCALE/arch/evaluation/champions/#sec-1`
  (was evaluation `#sec-3`); game §6 links `/LOCALE/arch/macro-brain/selfplay/`; index §4 links `/LOCALE/arch/mod/#sec-3`.
- No anchors of my pages changed; information §9 kept, so links from macro-brain/afterstates and sl-planner/budget hold.

## Where the code disagreed with FACTS.md or the audit

- Audit: `Dispatcher.cs:174 -> 181`; HEAD has the node-action refusal at `:182`.
- Audit: `BrainServer.cs` refusal path L39-80; the refusal is kept at `:80` (in `:71-81`) and thrown at `:61`.
- Not in the audit: `Observe.cs:259-261` drifted to `:261-263`; `SelfTest.Run.cs:134-174` overshoots (method ends
  `:166`); `Assist.cs:275` became `:277` with 70e9a25.
- After 70e9a25 the Workshop item ships `eval/champions/nosl.json` as `brain/champion.json` (not `sl.json`); index §4
  says so.
- README describes `docs/scripts/check.mjs` as it is in the working tree now (uncommitted edits by another writer: the
  public lint and the `docs: PAGE §N` citation check, the walk read from `eval/champions/sl.json`); that file's comment
  points at README's "What the book may not say", which now exists. If those edits do not land, README's check list
  must lose its lint, citation and champion-walk lines.
- `.env` has no `CLOUDFLARE_ACCOUNT_ID` yet; a deploy needs it set (or a wrangler login with one account).
- `.gitignore` still cites `docs: learning §6` (plan §9; D9's citation check will flag it).
