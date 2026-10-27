# Docs site

The project's design manual, in Chinese (site root) and English (`/en/`), built with
[Astro](https://astro.build) + [Starlight](https://starlight.astro.build).
Pages live in `src/content/docs/` (Chinese) and `src/content/docs/en/` (English).

```bash
pnpm install        # once
pnpm dev            # http://localhost:4321 (runs in the background)
pnpm astro dev stop # stop it
pnpm check          # build, then check links, anchors, locale twins, unrendered bold
```

Editing rules: the English page is the reference version; change both languages in the
same commit; keep section numbers stable (numbered headings get the anchor `#sec-5.1`,
and code comments cite them as `docs: simulator §5.1`).
