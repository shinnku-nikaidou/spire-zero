// Checks the built site in dist/ (run after `astro build`):
// - every internal link in page content reaches an existing page and anchor,
// - links stay in the locale of the page they are on,
// - every page exists in all three locales (Simplified Chinese at the root,
//   Traditional Chinese under /zh-tw/, English under /en/),
// - no Markdown bold was left unrendered (in Chinese, `**入口：**到达` does not render;
//   write `**入口**：到达`),
// - the book describes the system as it is now: no commit hash, date, generation name, `.claude/` or `tmp/` path in
//   what a reader sees or in the site's sources and data (README: "What the book may not say"),
// - every `docs: PAGE §N` that the repository's code cites reaches an existing page and section.
import { execFileSync } from 'node:child_process';
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const prefixes = ['/en', '/zh-tw', ''];

function* pageFiles(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) yield* pageFiles(path);
    else if (entry.name === 'index.html') yield path;
  }
}

const decode = (s) => decodeURIComponent(s.replaceAll('&amp;', '&'));
const localeOf = (route) => prefixes.find((p) => route.startsWith(p + '/'));
const stripLocale = (route) => route.slice(localeOf(route).length);

const pages = new Map();
for (const file of pageFiles(dist)) {
  const route = '/' + relative(dist, file).replace(/index\.html$/, '');
  const html = readFileSync(file, 'utf8');
  const main = html.slice(html.indexOf('<main'), html.indexOf('</main>'));
  pages.set(route, {
    ids: new Set([...html.matchAll(/\sid="([^"]+)"/g)].map((m) => decode(m[1]))),
    links: [...main.matchAll(/\shref="([/#][^"]*)"/g)].map((m) => m[1]),
    text: main.replace(/<pre[\s\S]*?<\/pre>|<code[\s\S]*?<\/code>/g, ''),
    shown: main.replace(/<script[\s\S]*?<\/script>|<style[\s\S]*?<\/style>/g, '').replace(/<[^>]+>/g, ' '),
  });
}

// What the book may not say: it describes the system as it is now. A commit hash (7-12 or 40 hex digits with a letter
// and a digit; run ids have 16 and pass; `abc1234` is the tests' placeholder commit), a date, a generation's name
// (`vNgM`), the local `tmp/` folder (except `tmp/gamebin/`, the default place of the game's assemblies) or `.claude/`.
const forbidden = [
  ['a commit hash', /(?<![#0-9A-Za-z_./-])(?=[0-9a-f]*[a-f])(?=[0-9a-f]*[0-9])(?!abc1234\b)(?:[0-9a-f]{7,12}|[0-9a-f]{40})(?![0-9A-Za-z_])/g],
  ['a date', /(?<![0-9])20\d\d-[01]\d-[0-3]\d(?![0-9])/g],
  ['a generation name', /(?<![0-9A-Za-z_])v\d+g\d+/g],
  ['a tmp/ path', /(?<![0-9A-Za-z_.-])tmp\/(?!gamebin\/)/g],
  ['a .claude/ path', /\.claude\//g],
];
const lint = (where, text) => {
  for (const [what, pattern] of forbidden)
    for (const m of text.matchAll(pattern)) errors.push(`${where}: ${what} "${m[0]}"`);
};

// The site's own sources and data: page sources, figures and the data they read.
function* sourceFiles(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) yield* sourceFiles(path);
    else if (/\.(mdx|md|svelte|ts|json)$/.test(entry.name) && !path.includes('/data/names/')) yield path;
  }
}

const errors = [];
let checked = 0;
for (const [route, page] of pages) {
  for (const prefix of prefixes) {
    const twin = prefix + stripLocale(route);
    if (!pages.has(twin)) errors.push(`${route}: no page in locale "${prefix || '/'}" (${twin})`);
  }

  for (const m of page.text.matchAll(/[^<>]{0,20}\*\*[^<>]{0,20}/g))
    errors.push(`${route}: unrendered bold near "${m[0]}"`);
  lint(route, page.shown);

  for (const href of page.links) {
    if (href.startsWith('/_astro/') || href.startsWith('/pagefind/')) continue;
    checked++;
    const [path, anchor] = href.split('#');
    const target = path === '' ? route : path.endsWith('/') ? path : path + '/';
    const targetPage = pages.get(target);
    if (!targetPage) {
      errors.push(`${route}: link to missing page ${href}`);
      continue;
    }
    if (anchor && anchor !== '_top' && !targetPage.ids.has(decode(anchor)))
      errors.push(`${route}: link to missing anchor ${href}`);
    if (localeOf(route) !== localeOf(target))
      errors.push(`${route}: link leaves the page's locale ${href}`);
  }
}

for (const file of sourceFiles(fileURLToPath(new URL('../src/', import.meta.url))))
  lint(relative(root, file), readFileSync(file, 'utf8'));

// Code comments cite the book as `docs: PAGE §N` (`docs: simulator/combat §5`, `docs: mod`): each must reach a page
// and, with §N, its numbered section, so a section is never renumbered or removed while the code cites it.
const tracked = execFileSync('git', ['ls-files', '-z', '--', '.', ':!docs'], { cwd: root, encoding: 'utf8' }).split('\0');
for (const file of tracked.filter(Boolean)) {
  let text;
  try {
    text = readFileSync(join(root, file), 'utf8');
  } catch {
    continue;
  }
  if (text.includes('\0')) continue;
  for (const m of text.matchAll(/docs: ([a-z][a-z0-9-]*(?:\/[a-z0-9-]+)*)(?: §(\d+(?:\.\d+)*))?/g)) {
    checked++;
    const page = pages.get(`/en/arch/${m[1]}/`) ?? pages.get(`/en/${m[1]}/`);
    if (!page) errors.push(`${file}: cites a missing page "${m[0]}"`);
    else if (m[2] && !page.ids.has(`sec-${m[2]}`)) errors.push(`${file}: cites a missing section "${m[0]}"`);
  }
}

// The combat net's walkthrough must hold what the champion's combat net file's test states hold
// (docs: combat-brain/walkthrough).
const champion = JSON.parse(readFileSync(new URL('../../eval/champions/sl.json', import.meta.url), 'utf8'));
const combat = JSON.parse(readFileSync(join(root, champion.options['combat-net']), 'utf8'));
const walk = JSON.parse(readFileSync(new URL('../src/data/combat-walk.json', import.meta.url), 'utf8'));
combat.tests.forEach((t, i) => {
  const off = Math.max(...t.outcome.map((p, k) => Math.abs(p - walk.tests[i].outcome[k])));
  if (off > 1e-3) errors.push(`combat-walk.json: test state ${i} is ${off} off the net file's`);
});

for (const error of errors) console.error(error);
console.log(`${pages.size} pages, ${checked} internal links, ${errors.length} problems`);
process.exit(errors.length ? 1 : 0);
