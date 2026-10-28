# Docs site

The project's design manual, in Simplified Chinese (site root), Traditional Chinese (`/zh-tw/`)
and English (`/en/`), built with [Astro](https://astro.build) + [Starlight](https://starlight.astro.build).
Pages live in `src/content/docs/` (Simplified Chinese), `src/content/docs/zh-tw/` (Traditional
Chinese) and `src/content/docs/en/` (English).

```bash
pnpm install        # once
pnpm dev            # http://localhost:4321 (runs in the background)
pnpm astro dev stop # stop it
pnpm check          # build, then check links, anchors, locale triplets, unrendered bold
```

Editing rules: the English page is the reference version; change all three languages in the
same commit; keep section numbers stable (numbered headings get the anchor `#sec-5.1`,
and code comments cite them as `docs: simulator §5.1`). The Traditional Chinese pages use
Taiwan wording and the game's own Traditional Chinese names (Boss = 頭目, Elite = 菁英,
Ascension = 晉階).
