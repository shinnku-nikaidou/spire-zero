# The book, rewritten for a reader who wants to understand the design

Drafted 2026-11-11 14:25 Taiwan time. **Approved by the user 2026-11-11 ~14:35** ("批准，開始").

**State (2026-11-11 ~15:35 Taiwan): steps 1 to 5 done and pushed; step 6 (deploy) waits for the user's word; step 7
(memory) done.** The user read the whole zh-TW draft and said "可以，開始翻".

- Step 3: Simplified Chinese and English written by two agents from the zh-TW text, reviewed here (English read in
  full, Simplified spot-checked); the three locales have the same blocks, figures and numbers. English says "mod" in
  lower case, "position" (not "situation") in the macro chapter too, "copy" for 複本; Simplified says 克隆, 重放, 菜谱.
  The rest site now reads "30% of max HP" in all three (the game's code).
- Step 4: the switch ran on the real tree with the script kept in this session's scratchpad (old `arch/`, old
  `index.mdx`, `glossary.mdx` deleted per locale; `preface.mdx` became `index.mdx`; flat sidebar; `sectionIds` removed;
  new `check.mjs` and `README.md`; 15 book figures, `Widening`, `td.ts`, `v/theme.ts` and five data files removed;
  `book-data.py` trimmed; `evalStats.ts` cut to the two intervals). `pnpm check`: 39 pages, 285 links, 17,999
  characters in Traditional Chinese, 0 problems.
- Step 5: no tracked file outside `docs/` cites the book (checked by `pnpm check`), and the figures' own comments cite
  no old page either.
- Open for the user: deploy (the look and the new book go out together); the site's title still says "architecture
  handbook"; the title screen's five win cards repeat the preface's paragraph on how well it plays; KaTeX is loaded
  but no chapter uses math.

Earlier notes, kept as written:

**Step 1 written, waiting for the user's read** (2026-11-11 ~15:05): `docs/src/content/docs/zh-tw/combat.mdx`, a draft
page, read at `/zh-tw/combat/` on a dev server. Measured: 2,095 characters (cap 6,000), 25 paragraphs, longest 139,
mean sentence 28 characters, no `§`, code span or link. Beyond "only adds draft files": the zh-TW labels of TurnTree,
BeamStepper and `combat/words.ts` were changed to the chapter's words (局面, 步, 每回合留幾個). Left out because the
reports do not record it: the old page's "0 replans in the champion's reports". Step 0 is still open.

**Step 1 accepted by the user** (2026-11-11, "寫的好啊，可以繼續了"). **Step 2 written, waiting for the user's read
of the whole book** (2026-11-11 evening): thirteen drafts under `docs/src/content/docs/zh-tw/`, 16,931 characters in
all (cap 55,000), no paragraph over 151, no `§`, code span or file name; three links, all in the preface.

- The preface is `preface.mdx` until the switch: `index.mdx` is still the old page.
- Facts came from eight read-only research agents (old English pages checked against the code) and were written up
  here. Where the old pages were stale the chapters follow the code: a card prompt is a state of the search; a
  promotion is one command that judges all characters' pairs together; the champion's NoSL file names no report.
- Figures used: 17. Not used after all: PairedScatter (old nets, a regime of rare wins), SprtReplay (the sequential
  gate decides no promotion), Determinize (WorldSampler shows the same better), ObservationExplorer (its labels are
  protocol field names).
- zh-TW labels and captions of the used figures were rewritten to the chapters' words. Left as they are: the two net
  diagrams' hover texts keep standard ML terms (嵌入, logit, softmax); PanelMock keeps the real panel's own lines.
- Numbers that belong to the champion's nets stand in the prose of the preface, environments, combat-net, value-net,
  evaluation and limits: after a promotion those sentences change with the figures' data.
- **The look is another session's** (the user's decision, 2026-11-11): `docs/src/styles/spire.css`,
  `docs/src/components/spire/PageTitle.astro`, and three keys in `docs/astro.config.mjs` (`customCss`, `components`,
  `head`). This plan does not touch them; at the switch it changes only `book` (the sidebar) and removes the
  `sectionIds` integration in that file. The design session was told the thirteen titles, slugs and their order.
  D11 ("the only writer of `docs/`") therefore holds for the text, figures, check and README, not for the look.
- Hydration of the interactive figures was seen only on `combat` and `game` (visible pane); the others were checked
  by a server render without error and by loading each component's module.

The user's rules (2026-11-11), which this plan carries out:

1. **The reader** is someone who wants to understand the design. Not someone changing the code, not a player
   setting up the mod.
2. **No maintainers' reference, anywhere.** The code is its own documentation. Protocol fields, command-line
   options, file and class names, message formats are not moved to an appendix or a README: they go.
3. **The model for the writing** is the macro-brain overview's "The method in one picture": the method in one
   sentence, one figure with the same boxes as the sentence, four short steps. It is the one passage the user shaped.

## 0. What was measured

Read in full (zh-TW): the introduction, the game chapter, the combat-brain and macro-brain overviews, macro-brain
search. Read in part: gates, mod, the learning loop, the glossary. The rest by counts and section headings only.

- **Size**: 55 pages per locale, 454,432 characters in zh-TW (about a 300-page book).
- **Per locale**: 1,365 `§` references; 1,325 internal links (24 a page); 4,929 inline code spans; 717 source file
  references (92 in the game chapter, to decompiled code the reader does not have); 499 command-line options
  (58 distinct); 178 "planned"; 12 status boxes at the top of overview pages; 1,639 table rows; 361 of 2,330 prose
  paragraphs longer than 220 raw characters.
- **How it got this way**: every docs update adds facts and none removes any. The paragraph right after the model
  passage grew from 251 to 306 characters over five updates; the four steps themselves were edited four times.
- **Code cites the book** in 203 lines (`docs: PAGE §N`), in `src/`, `learn/` and in files under `eval/`
  (leaderboards, the ledger, seed lists, reports). Section numbers are frozen because of them.
- **Nothing outside `docs/` links to a page URL**, so URLs are free to change.
- **Figures**: 33 components (`book/` 23, `combat/` 7, `v/` 3), used 36 times.
- **Another session is editing the book right now**: 38 English pages have uncommitted changes (seen 14:21).

## 1. Decisions

The user's: the three rules above.

Mine, each open to being overruled:

- **D1. Traditional Chinese is written first and is the reference.** The translated feel of the Chinese comes from
  mirroring English sentence by sentence; the user reads zh-TW and judges it. zh-CN and English are made from the
  zh-TW text only after the user has read it.
- **D2. Three locales stay.** No one asked to drop one.
- **D3. Thirteen short pages, one flat list** in the sidebar: no parts, no chapter folders, no overview pages, no
  badges. New slugs at the site's root (`/zh-tw/combat/`), the `arch/` prefix goes.
- **D4. No glossary.** The book uses a fixed vocabulary of about sixteen terms (§4); a chapter that needs a term
  says what it means in one sentence, again if need be.
- **D5. No section numbers.** Headings are plain; the `sec-N` anchor integration is removed. Nothing cites
  sections any more.
- **D6. What is not built gets one closing page**, "Weaknesses and what is not built". Everywhere else the book
  describes only what runs.
- **D7. How a player installs and uses the mod is not in the book.** It belongs on the Workshop page. Writing that
  page is not part of this plan.
- **D8. The game chapter keeps only what later chapters use.** One sentence says the facts were read from the
  game's own code at v0.111.0; no file and line citations.
- **D9. Drafts are real pages with `draft: true`** (Starlight 0.42.7 has it): shown by `pnpm dev`, left out of
  `pnpm build`, the check and a deploy. The old book stays whole and live until the switch (step 4).
- **D10. The rules are enforced by `pnpm check`** (§3, "checked"), so the book cannot grow back.
- **D11. This session writes it**, and from approval on it is the only writer of `docs/`.

## 2. The chapters

Targets are zh-TW characters; the whole book is at most 55,000 (now 454,432).

| Slug | Title (working) | The question it answers | Made from | Figures | Target |
|---|---|---|---|---|---|
| `index` | 前言 | What is this, how well does it play, how to read the book | index | none | 1,200 |
| `game` | 遊戲 | What a run is, what the player decides, what is hidden, why it is hard | game | RunTimeline, SurfaceCounts | 3,500 |
| `idea` | 想法 | How the AI makes any decision: search plus a learned value, on one scale | index §1, combat-brain §2, macro-brain | one small diagram | 3,000 |
| `environments` | 兩種環境 | How much the AI may know: with and without save and load | environments (3 pages) | NodeTimeline | 4,000 |
| `combat` | 戰鬥 | How a fight is played when everything in the room is known | combat-brain overview, sl-planner (4 pages) | TurnTree, BeamStepper, PlanTimeline | 6,000 |
| `combat-hidden` | 看不到牌序的戰鬥 | How a fight is played when the draw order is hidden | pimc (3 pages), environments/worlds | WorldSampler or Determinize, WorldsTable, StrategyFusion | 3,500 |
| `combat-net` | 戰鬥網路 | What a position is worth where the search stops | combat-brain leaf, network, walkthrough; learning/combat-net | ObservationExplorer, ArchDiagram, OutcomeHead | 5,000 |
| `macro` | 戰鬥之外 | Which card to take, which way to go | macro-brain overview, afterstates, search | the existing diagram, MapRoute | 4,500 |
| `value-net` | 價值網路 | What V reads and what it computes | macro-brain network, value | ArchDiagram, EnteringFork, NetWalk | 5,000 |
| `learning` | 學習 | How the nets learn from nothing | macro-brain selfplay, learning loop (built parts only) | one loop diagram | 4,500 |
| `evaluation` | 評估 | How we know a new net is better, and how good the champion is | evaluation gates, metrics, champions | PairedScatter, WilsonInterval, DeathsByFloor | 4,500 |
| `real-game` | 真遊戲 | How one brain plays the simulator and the real game | simulator, protocol, harness, gamebridge, mod, output (23 pages) | PanelMock | 5,000 |
| `limits` | 弱點與還沒做的 | Where it is weak, and what is not built | macro-brain next, the "planned" sections | none | 2,000 |

The `macro` chapter is built around the user's passage, kept word for word where it is still true.

Every chapter is checked against the code at HEAD, not against the old pages: the old pages supply explanations,
the code supplies the facts.

## 3. Writing rules

Checked by `pnpm check` on the built pages (thresholds are first guesses, fixed after the sample chapter):

- no `§`;
- no source file name, no command-line option, no `Type.Member`; at most 5 inline code spans a page;
- at most 8 links to other chapters a page;
- a paragraph is at most 200 characters (zh), a page at most 6,000, the book at most 55,000 (lengths on zh-TW);
- no status badge; "planned" and its Chinese forms only on `limits`;
- no `docs: ...` citation in any tracked file outside `docs/`;
- the existing checks stay: links and anchors, three locales, unrendered bold, no hash, date, generation name,
  `tmp/` or `.claude/` path.

Not checkable, held by the writer:

- **One chapter, one question.** It opens with the answer in at most three sentences, then one picture, then the
  mechanism in steps. It ends when the question is answered: no summary, no "this chapter" table.
- **A reason at each step**, not where the code is. "So a card, a relic and a heal are compared on one scale."
- **A chapter stands alone.** An idea from an earlier chapter is restated in one sentence, not pointed at.
- **Examples come from the game** and from the book's one example run.
- **Only numbers that make a point**: the win rates, 48 floors, about 830 decisions a run, 8 sampled worlds. No
  timings in milliseconds, no line counts, no test names.
- **A figure says what the text next to it says.** A figure the chapter does not need goes.
- **Chinese written as Chinese**: the subject is named, no chains of 它, a parenthesis holds a gloss and never a
  second sentence, verbs a player would use (打一遍、看、問、選).
- **Still holds**: the system as it is now, no history.

## 4. Vocabulary

About sixteen terms, each explained in plain words where a chapter first uses it:

一局、層、房間、種子、SL / NoSL、複本、世界、搜尋、後狀態、價值 V、戰鬥網路、價值網路、自我對弈、探索、冠軍與挑戰者、
門檻。

Renamed or dropped from prose:

| Now | Becomes |
|---|---|
| 節點 (node) | 房間, or 當前房間 |
| 節點複本 | 複本 |
| 決策介面 (surface) | 畫面 |
| 葉節點 (leaf) | 搜尋停下的局面 |
| L0 | 宏觀大腦; L1 is planned and goes |
| PIMC, beam | named once as the method's name; in prose 取樣再平均, 每回合只留最好的幾條 |
| 宿主, Harness, dispatcher, worker, GameBridge, Protocol | not used; `real-game` says what each side does in plain words |

The wording is settled on the sample chapter.

## 5. Figures

Kept (19): RunTimeline, SurfaceCounts, NodeTimeline, TurnTree, BeamStepper, PlanTimeline, WorldSampler or
Determinize (one of the two), WorldsTable, StrategyFusion, ObservationExplorer, ArchDiagram, OutcomeHead, MapRoute,
EnteringFork, NetWalk, PairedScatter, WilsonInterval, DeathsByFloor, PanelMock.

Dropped (13, plus whichever of WorldSampler and Determinize is not kept), with their data and the parts of `book-data.py` that only feed them: DecisionLog, NetFileAnatomy,
PoolTimeline, PumpDemo, RestoreCost, Scaling, TickDemo, WireExchange, WorldTurns (reference material); TdTargets,
ShapingToy, Widening (planned methods); SprtReplay, unless the gate in use is the sequential one (checked when
`evaluation` is written).

Labels inside kept figures follow §3 and §4 too.

## 6. Steps

0. **The other session's docs update.** Its 38 uncommitted English pages update pages this plan deletes. The user
   tells that session to stop its docs part; its facts reach the new book through the code. Until that is settled
   this plan only adds draft files, which collide with nothing.
1. **Sample chapter: `combat`, in zh-TW.** Chosen because it is the densest today (five pages, about 33,000
   characters, into 6,000). Written as a draft page, measured against §3, opened in the local preview.
   **Stop: the user reads it.** Their remarks correct §3, §4 and the thresholds in this file.
2. **The other twelve chapters in zh-TW**, in reading order, `index` last, as drafts. **Stop: the user reads the
   book.** Translation does not start before the user says the text is right.
3. **zh-CN and English from the zh-TW text.** zh-CN with mainland wording and the game's Simplified names; English
   written as English with the same paragraphs, figures and numbers.
4. **The switch, one commit.** Draft flags off; the 165 old pages deleted; the sidebar a flat list; section
   numbering and its integration removed; dropped figures, data and script parts deleted; `check.mjs` gets the
   checks of §3 and loses the citation check; `docs/README.md` rewritten: who the book is for, the rules, zh-TW as
   the reference, and how to maintain it: **a change to the system changes the book only where a sentence became
   false; facts about the code are not added.** `pnpm check`: 0 problems.
5. **The 203 citations in code.** Each `docs: ...` is removed. Where it was the comment's only reason, the reason
   is written there in one line. Generated text is fixed in its generator (leaderboard, reports) and the existing
   files' header lines are edited. Files carrying another session's uncommitted hunks wait until those land; each
   commit holds only this plan's hunks. Can run any time after approval.
6. **Deploy**, on the user's word, after the host-name scan.
7. **Memory**: the docs memory is rewritten to these rules, so later sessions keep the book as it is.

## 7. Done means

- 13 pages in each of three locales; at most 55,000 characters in zh-TW;
- `pnpm check` reports 0 problems with the new checks;
- no `docs: ...` citation in any tracked file outside `docs/`;
- the user has read the zh-TW book and said it is right.

## 8. Not in this plan

- The Workshop page for players (D7).
- Any change to code other than comments.
- New figures. If a chapter cannot be understood without one, that is raised when the chapter is written.
