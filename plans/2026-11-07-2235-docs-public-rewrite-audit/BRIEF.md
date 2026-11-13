# Audit brief: docs pages for the public-repo rewrite (read-only)

Context. `/home/shinnku/codes/sts2/docs/` is an Astro + Starlight site, a book about an AI that plays Slay the Spire 2
(C# simulator/brain in `src/`, Python training in `learn/`, nets in `weights/all/`, eval reports in `eval/`).
Pages: Simplified Chinese at `docs/src/content/docs/`, Traditional Chinese at `.../zh-tw/`, English at `.../en/`.
English is the reference. Numbered headings get anchors `#sec-N` / `#sec-N.M`. The repository is about to go public,
with a rewritten git history, so every commit hash in the docs will be stale.

The user's rule for the rewrite: **the docs describe the system as it is now.** Remove:
1. **hash** - any commit hash (7-40 hex), incl. "(commit `abc1234`)" and hashes inside figure data/components.
2. **history** - narratives of how things went: "no longer", "used to", "was replaced by", "since X", "at first",
   "the old ...", "earlier ...", what was tried and dropped, bugs that were found and fixed, "until generation N".
3. **version** - names of earlier nets/generations/datasets (c7-2, v8-1, v8g1, v9g0, gen 3...). The CURRENT champion
   files and names are facts of the system and may stay (today: `weights/all/c9-2.json`, `v9-2.json`; c9-0..2 and
   v9-0..2 are the files in git). Lineage tables ("v9-1 beat v9-0 by ...") are history.
4. **plan** - plan numbers/names ("plan v7", "plan 2026-11-07-1258-...").
5. **date** - dates of decisions or events (a date as part of a current file format/example is fine).
6. **story** - results told as a story ("the gate passed with +12 wins", "this cost us 3 days"); a measured number of
   the CURRENT system stated as a fact (speed, current champion's win rate in its current report) may stay.
7. **experiment** - earlier experiments and their outcomes (an experiment log is history).
8. **host** - the remote hosts, their names or addresses, the hub / storage hub, the fleet, distributed or remote runs,
   Colab, relays, `box.py` (the job launcher on the hub), deadlines files on hosts. Already a rule; check it holds.
9. **skill** - any pointer into `.claude/` (e.g. `.claude/skills/sl-selfplay/SKILL.md`): it leaves git.
10. **untracked** - pointers to files that are not in git, so a public reader cannot follow them: `tmp/...`
    (e.g. `tmp/build.sh`), `~/sts2/...` on hosts, `data/` folders of jobs are OK only as "where output goes".
11. **user** - quoting or attributing "the user" (the project owner) - not allowed in docs.
12. **future** - plans / "next" / "planned" content: list it separately; the decision whether it stays is made later.

Design rationale may stay when it is an argument about the current design ("X, because Y"), not a story of an
experiment. A whole section that is only history/experiments should go; a section mixing facts and history is
rewritten to the facts.

## What to do
Read every English page of your group IN FULL (not excerpts). For each page also:
- list the Svelte components / data files it imports (`import ... from '../../../components/...'`) and check their
  visible text and data JSON (`docs/src/components/**`, `docs/src/data/**`) for the same categories;
- grep the zh-CN and zh-tw twins for hashes, dates, version names, host words, `.claude`, `tmp/` that are NOT in
  the English page (twins should match; report only differences);
- for every section you propose to delete or renumber, grep all English pages for links to it
  (`grep -rn 'PAGE/#sec-N' docs/src/content/docs/en`) and list the inbound links.

Do NOT edit any file in the repository. Do not commit, do not switch branches. Write your report to
`AUDIT_FILE` (given below) and reply with a 5-line summary only.

## Report format (Markdown), one block per page
```
### en/arch/<path>.mdx  (lines N)  -- FATE: KEEP | EDIT | REWRITE | DELETE | MERGE-INTO <page>
Reason: one line.
Sections: list every heading with its anchor and a per-section fate (keep / edit / rewrite / delete), e.g.
  - §3 `#sec-3` Title -- delete (only the c7-2 -> c8 migration story)
Findings (line, category, short quote <= 15 words, proposed action):
  - L42 history "c8-1 replaced c7-2 with no training" -> drop sentence
  - L88 host "on several remote hosts at once" -> rewrite: "on several processes" / drop
Inbound links to deleted/renumbered sections: ...
Figures/data: component -> issues (or "clean").
Twins: differences from English (or "match").
Future/next content: ... (or none)
Rough size of the rewrite: lines removed / rewritten.
```
End the file with a short list of cross-page issues you noticed (stale facts vs code you happened to see, broken
assumptions, pages that overlap heavily and could merge). Keep findings concrete: line numbers and anchors.
