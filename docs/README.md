# Docs site

The project's design manual, in Simplified Chinese (site root), Traditional Chinese (`/zh-tw/`)
and English (`/en/`), built with [Astro](https://astro.build) + [Starlight](https://starlight.astro.build).
Pages live in `src/content/docs/` (Simplified Chinese), `src/content/docs/zh-tw/` (Traditional
Chinese) and `src/content/docs/en/` (English).

```bash
pnpm install        # once
pnpm dev            # http://localhost:4321 (runs in the background)
pnpm astro dev stop # stop it
pnpm check          # build, then check links, anchors, locale triplets, unrendered bold, and the figures' V
```

The sidebar is the book's table of contents (`astro.config.mjs`): an overview, four parts and the appendices. A
chapter of several pages lives in a folder (`arch/macro-brain/`, its first page `index.mdx` at the chapter's URL).

**Figures.** The macro brain's interactive figures are Svelte islands in `src/components/v/`, drawn with D3.
`src/lib/v/` holds V's forward pass in TypeScript; `pnpm check` makes it value the test runs of
`weights/silent/v8-3.json` as the trainer did. Cards,
relics and potions show the game's own names (`src/data/names/`, written by
`node scripts/game-names.mjs PATH/SlayTheSpire2.pck`), followed by the id in brackets.

Editing rules: the English page is the reference version; change all three languages in the
same commit; keep section numbers stable (numbered headings get the anchor `#sec-5.1`,
and code comments cite them as `docs: simulator §5.1`, or `docs: macro-brain/search §2` for a page of a chapter). The Traditional Chinese pages use
Taiwan wording and the game's own Traditional Chinese names (Boss = 頭目, Elite = 菁英,
Ascension = 晉階).
