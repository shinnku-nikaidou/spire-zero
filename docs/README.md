# Docs site

The book on the system's design, for a reader who wants to understand it: thirteen short chapters in Traditional
Chinese (`/zh-tw/`), Simplified Chinese (site root) and English (`/en/`), built with [Astro](https://astro.build) +
[Starlight](https://starlight.astro.build). Pages live in `src/content/docs/zh-tw/` (Traditional Chinese),
`src/content/docs/` (Simplified Chinese) and `src/content/docs/en/` (English), one file per chapter, the same file
names in each.

```bash
pnpm install        # once
pnpm dev            # http://localhost:4321 (runs in the background)
pnpm astro dev stop # stop it
pnpm check          # build, then check the built site and the sources (below)
pnpm run deploy     # build, then upload dist/ to Cloudflare Workers with wrangler
```

The sidebar is the book's table of contents, a flat list of the chapters (`astro.config.mjs`). `pnpm run deploy`
takes the Cloudflare account from `CLOUDFLARE_ACCOUNT_ID` in the environment (`wrangler.jsonc` names none). The
book's look is `src/styles/spire.css` and `src/components/spire/`.

## Who the book is for

Someone who wants to understand the design: the problem, the ideas, why they work, how the parts fit, how well it
plays. Not someone changing the code, and not a player setting up the mod. The book holds no reference material:
fields, commands, files and classes are the code's to document, and the code does not cite the book.

## How the book is written

- **One chapter, one question.** It opens with the answer in at most three sentences, then one picture, then the
  mechanism in steps. It ends when the question is answered: no summary, no list of what the chapter holds.
- **A reason at each step**, not where the code is.
- **A chapter stands alone.** An idea from another chapter is said again in one sentence, not pointed at.
- **Examples come from the game** and from the book's one example run.
- **Only numbers that make a point.** No timings in milliseconds, no counts of lines or tests.
- **A figure says what the text beside it says.** Its labels use the chapter's words.
- **What is not built is in the last chapter only.** Everywhere else the book describes what runs.
- **A fixed, small vocabulary**, each term said in plain words where a chapter first uses it: a run, a floor, a
  room, a seed, SL and NoSL, the copy, a world, search, a position, a line, an afterstate, the value V, the combat
  net, the value net, self-play, exploration, the champion, the gate.
- **Each language is written as that language.** The Traditional Chinese text is the reference: a change is written
  there first, then carried to the other two with the same headings, paragraphs, figures and numbers. Traditional
  Chinese uses Taiwan wording and the game's own names (Boss = 頭目, Elite = 菁英, Ascension = 晉階); Simplified
  Chinese uses Mainland wording and the game's Simplified names. Mermaid labels are in the page's language.

## Maintaining it

**A change to the system changes the book only where a sentence became false.** Facts about the code are not added:
no file, class, option or field names, no status of what is built, no cross-reference by section.

The numbers that belong to the champion (wins of each character, what its nets were trained on, how well they
predict) stand in the preface and in the chapters on the two environments, the two nets, evaluation and
weaknesses. After a promotion those sentences change together with the figures' data.

**Figures.** The interactive figures are Svelte islands in `src/components/` (`book/`, `combat/`, `v/`), drawn with
D3. The data they show is in `src/data/`: the book's example run, the champion's reports and its combat net's test
states. `scripts/book-data.py` makes it from one play of the example seed in each environment and the champion's
reports (its docstring has the commands), and `scripts/combat-walk.py` makes the combat net's states; both run from
the repository's root with `uv run --project learn python docs/scripts/...`. After a promotion, run them again.
Cards, relics and monsters show the game's own names (`src/data/names/`, written by
`node scripts/game-names.mjs PATH/SlayTheSpire2.pck`).

## What `pnpm check` checks

It builds the site, then `scripts/check.mjs` checks:

- every internal link in a page reaches an existing page and anchor, and stays in the page's locale;
- every page exists in all three locales;
- no Markdown bold is left unrendered (in Chinese, `**入口：**到达` does not render; write `**入口**：到达`);
- how the book is written, on each chapter's own text (figures, diagrams and code blocks aside): no section sign,
  no source file name, no command-line option, no `Type.Member` name, no status badge, the Chinese words for
  "planned", "built" and "verified" only in the last chapter; at most 5 code spans and 8 links to other chapters a
  page; a paragraph of at most 200 characters in Chinese or 120 words in English; a chapter of at most 6,000
  characters and the book of at most 55,000, counted on the Traditional Chinese pages;
- what the book may not say (below), in the text of every built page and in the site's sources and data under `src/`
  (`.mdx`, `.md`, `.svelte`, `.ts`, `.json`; not the game's names);
- no tracked file outside `docs/` cites the book (`docs: PAGE §N`);
- the combat net's figure data (`src/data/combat-walk.json`) holds what the test states of the champion's combat net
  file hold (the file `eval/champions/sl.json` names).

It prints the number of pages, internal links and characters, and every problem; it exits 1 on any problem.

## What the book may not say

The book describes the system as it is now. It holds no history: no commit hashes, no dates of events, no names of
generations of self-play data (`vNgM`), no old nets or how one net led to the next, no stories of bugs or of
experiments that decided something, and no "no longer" or "used to". Design rationale stays, told as an argument
("X, because Y"). It points at nothing outside the repository: no `tmp/` path and no `.claude/` path.

`pnpm check` enforces the patterns it can see: a commit hash (7 to 12 or 40 hex digits with a letter and a digit;
`abc1234` is the tests' placeholder commit, and 16-digit run ids pass), a date, a generation name, a `tmp/` path and
a `.claude/` path.
