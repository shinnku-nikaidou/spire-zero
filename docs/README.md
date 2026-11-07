# Docs site

The book on the system's design, in Simplified Chinese (site root), Traditional Chinese (`/zh-tw/`)
and English (`/en/`), built with [Astro](https://astro.build) + [Starlight](https://starlight.astro.build).
Pages live in `src/content/docs/` (Simplified Chinese), `src/content/docs/zh-tw/` (Traditional
Chinese) and `src/content/docs/en/` (English).

```bash
pnpm install        # once
pnpm dev            # http://localhost:4321 (runs in the background)
pnpm astro dev stop # stop it
pnpm check          # build, then check the built site and the sources (below)
pnpm run deploy     # build, then upload dist/ to Cloudflare Workers with wrangler
```

The sidebar is the book's table of contents (`astro.config.mjs`): an overview, four parts and the appendix. A
chapter of several pages lives in a folder (`arch/macro-brain/`, its first page `index.mdx` at the chapter's URL).
`pnpm run deploy` takes the Cloudflare account from `CLOUDFLARE_ACCOUNT_ID` in the environment (`wrangler.jsonc`
names none).

**Figures.** The interactive figures are Svelte islands in `src/components/` (`book/`, `combat/`, `v/`), drawn with
D3. The data they show is in `src/data/`: the book's example run, evaluation reports and net files, made by scripts
such as `scripts/combat-walk.py` (`uv run --project learn python docs/scripts/combat-walk.py` from the
repository's root). Cards, relics and potions show the game's own names (`src/data/names/`, written by
`node scripts/game-names.mjs PATH/SlayTheSpire2.pck`), followed by the id in brackets.

**`pnpm check`** builds the site, then `scripts/check.mjs` checks:

- every internal link in a page reaches an existing page and anchor, and stays in the page's locale;
- every page exists in all three locales;
- no Markdown bold is left unrendered (in Chinese, `**入口：**到达` does not render; write `**入口**：到达`);
- what the book may not say (below), in the text of every built page and in the site's sources and data under `src/`
  (`.mdx`, `.md`, `.svelte`, `.ts`, `.json`; not the game's names);
- every `docs: PAGE §N` in the repository's tracked code outside `docs/` reaches an existing page and section;
- the combat net's walkthrough (`src/data/combat-walk.json`) holds what the test states of the champion's combat net
  file hold (the file `eval/champions/sl.json` names).

It prints the number of pages and internal links, and every problem; it exits 1 on any problem.

## Maintaining it

- The English page is the reference version; the Simplified and Traditional Chinese pages say the same thing, with
  the same headings and the same code spans. Change all three in the same commit.
- Keep section numbers stable: a numbered heading gets the anchor `#sec-5` or `#sec-5.1` in every locale, and code
  comments cite it as `docs: simulator §5.1`, or `docs: macro-brain/search §2` for a page of a chapter. A numbered
  section is never renumbered or removed while something cites it.
- The Traditional Chinese pages use Taiwan wording and the game's own Traditional Chinese names (Boss = 頭目,
  Elite = 菁英, Ascension = 晉階).
- Mermaid labels, code blocks and code comments in pages are in English in all three locales.

## What the book may not say

The book describes the system as it is now. It holds no history: no commit hashes, no dates of events, no names of
generations of self-play data (`vNgM`), no old nets or how one net led to the next, no stories of bugs or of
experiments that decided something, and no "no longer" or "used to". Design rationale stays, told as an argument
("X, because Y"), and so do planned parts, under their `planned` badge. It points at nothing outside the repository:
no `tmp/` path (except `tmp/gamebin/`, the default folder of the game's assemblies, which you create) and no
`.claude/` path. The data folder `data/` is named only as where outputs go; it is not in git.

`pnpm check` enforces the patterns it can see: a commit hash (7 to 12 or 40 hex digits with a letter and a digit;
`abc1234` is the tests' placeholder commit, and 16-digit run ids pass), a date, a generation name, a `tmp/` path and
a `.claude/` path.
