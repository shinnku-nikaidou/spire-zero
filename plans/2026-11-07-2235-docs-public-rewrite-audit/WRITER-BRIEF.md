# Writer brief (docs rewrite for the public repository)

You rewrite a set of pages of the trilingual docs site in `/home/shinnku/codes/sts2/docs/` (Astro + Starlight).
Read, in this order:
1. `/home/shinnku/codes/sts2/plans/2026-11-07-2235-docs-public-rewrite-audit/FACTS.md` (rules, shared facts and
   wording: it wins over everything below);
2. the plan `/home/shinnku/codes/sts2/plans/2026-11-07-2235-docs-public-rewrite.md`, sections 1, 2 and your pages'
   rows in section 4;
3. your audit report in the same folder as FACTS.md (the line-level worklist; line numbers are HEAD's).

Then, for each of your pages: read the English page in full, apply every change its plan row and its audit block call
for (and anything else in it that breaks FACTS.md's rules), checking each claim about code against the code at HEAD;
then mirror the same changes in the zh-CN twin and the zh-TW twin (read each in full first).

Rules: edit only your own pages (and the extra files your brief names); never renumber or delete a numbered heading;
do not touch `docs/src/components/**` or `docs/src/data/**`; do not build (`pnpm check`, `astro build`); do not commit,
stash, reset or switch branches (the checkout is shared with other writers and sessions). Keep the house style: plain
present-tense sentences, the pages' existing link style (`/en/arch/...`, `/zh-tw/arch/...`, `/arch/...` for zh-CN).

When done, write `/home/shinnku/codes/sts2/plans/2026-11-07-2235-docs-public-rewrite-audit/DONE-<your group>.md`:
per page, what changed (sections), what you left on purpose and why, anything for the figures step (sentences that
wait for regenerated data), cross-page links you changed or that others must change, and every place where the code
disagreed with FACTS.md or the audit. Reply with a 5-line summary.
