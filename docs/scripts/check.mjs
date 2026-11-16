// Checks the built site in dist/ (run after `astro build`):
// - every internal link in page content reaches an existing page and anchor,
// - links stay in the locale of the page they are on,
// - every page exists in all three locales (English at the root, Simplified Chinese under /zh-cn/, Traditional
//   Chinese under /zh-tw/),
// - no Markdown bold was left unrendered (in Chinese, `**入口：**到达` does not render;
//   write `**入口**：到达`),
// - the book describes the system as it is now: no commit hash, date, generation name, `.claude/` or `tmp/` path in
//   what a reader sees or in the site's sources and data (README: "What the book may not say"),
// - the book is written for a reader who wants to understand the design (README: "How the book is written"): a
//   chapter's own text holds no section sign, source file name, command-line option or `Type.Member`, few code spans
//   and links, short paragraphs, no status badge; a chapter and the whole book stay short,
// - no code cites the book: the code is its own documentation.
import { execFileSync } from 'node:child_process';
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const dist = fileURLToPath(new URL('../dist/', import.meta.url));
// The root's prefix comes last: every route starts with it.
const prefixes = ['/zh-cn', '/zh-tw', ''];

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
const textOf = (html) => html.replace(/<\/(?:p|li|h\d|div|td|th|tr|ul|ol|table|blockquote|section)>|<br\s*\/?>/g, ' ').replace(/<[^>]+>/g, '').replaceAll('&amp;', '&').replaceAll('&lt;', '<').replaceAll('&gt;', '>').replaceAll('&quot;', '"').replaceAll('&#39;', "'").replace(/\s+/g, ' ').trim();

const pages = new Map();
for (const file of pageFiles(dist)) {
  const route = '/' + relative(dist, file).replace(/index\.html$/, '');
  const html = readFileSync(file, 'utf8');
  const main = html.slice(html.indexOf('<main'), html.indexOf('</main>'));
  // A chapter's own text: the page without its figures (their labels are the figures'), diagrams, code blocks and
  // the footer under it (the links to the chapters before and after).
  const body = main.includes('sl-markdown-content') ? main.slice(main.indexOf('sl-markdown-content')) : main;
  const content = body.includes('<footer') ? body.slice(0, body.indexOf('<footer')) : body;
  const prose = content.replace(/<astro-island[\s\S]*?<\/astro-island>|<pre[\s\S]*?<\/pre>|<script[\s\S]*?<\/script>|<style[\s\S]*?<\/style>/g, '');
  pages.set(route, {
    ids: new Set([...html.matchAll(/\sid="([^"]+)"/g)].map((m) => decode(m[1]))),
    links: [...main.matchAll(/\shref="([/#][^"]*)"/g)].map((m) => m[1]),
    text: main.replace(/<pre[\s\S]*?<\/pre>|<code[\s\S]*?<\/code>/g, ''),
    shown: main.replace(/<script[\s\S]*?<\/script>|<style[\s\S]*?<\/style>/g, '').replace(/<[^>]+>/g, ' '),
    prose,
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

// How the book is written: what a chapter's own text may not hold, and how long it may be. Lengths are counted in
// characters on the Chinese pages and in words on the English ones.
const unwritten = [
  ['a section sign', /§/g],
  ['a source file name', /(?<![\w/.-])[\w./-]+\.(?:cs|py|mjs|ts|toml|json|sh)(?![\w-])/g],
  ['a command-line option', /(?<![\w-])--[a-z][a-z-]+/g],
  ['a Type.Member name', /(?<![\w.])[A-Z][a-z]+[A-Za-z0-9]*\.[A-Z][A-Za-z0-9]+/g],
  ['a status word', /規劃中|规划中|已實作|已实现|已核實|已核实/g],
];
const limits = { codeSpans: 5, links: 8, paragraph: { zh: 200, en: 120 }, page: 6000, book: 55000 };
const sizeOf = (route, text) => (localeOf(route) === '' ? text.split(' ').filter(Boolean).length : text.replace(/\s/g, '').length);

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
let book = 0;
for (const [route, page] of pages) {
  for (const prefix of prefixes) {
    const twin = prefix + stripLocale(route);
    if (!pages.has(twin)) errors.push(`${route}: no page in locale "${prefix || '/'}" (${twin})`);
  }

  for (const m of page.text.matchAll(/[^<>]{0,20}\*\*[^<>]{0,20}/g))
    errors.push(`${route}: unrendered bold near "${m[0]}"`);
  lint(route, page.shown);

  const prose = textOf(page.prose);
  for (const [what, pattern] of unwritten) {
    if (what === 'a status word' && stripLocale(route) === '/limits/') continue;
    for (const m of prose.matchAll(pattern)) errors.push(`${route}: ${what} "${m[0]}"`);
  }
  if (/class="[^"]*sl-badge/.test(page.prose)) errors.push(`${route}: a status badge`);
  const spans = [...page.prose.matchAll(/<code[\s>]/g)].length;
  if (spans > limits.codeSpans) errors.push(`${route}: ${spans} code spans (at most ${limits.codeSpans})`);
  const cap = limits.paragraph[localeOf(route) === '' ? 'en' : 'zh'];
  for (const m of page.prose.matchAll(/<(p|li)[\s>][\s\S]*?<\/\1>/g)) {
    const text = textOf(m[0].replace(/<(ul|ol)[\s>][\s\S]*<\/\1>/g, ''));
    if (sizeOf(route, text) > cap) errors.push(`${route}: a paragraph of ${sizeOf(route, text)} (at most ${cap}): "${text.slice(0, 30)}…"`);
  }
  if (localeOf(route) === '/zh-tw') {
    const size = sizeOf(route, prose);
    book += size;
    if (size > limits.page) errors.push(`${route}: ${size} characters (at most ${limits.page})`);
  }

  let others = 0;
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
    if (target !== route && page.prose.includes(`href="${href}"`)) others++;
    if (anchor && anchor !== '_top' && !targetPage.ids.has(decode(anchor)))
      errors.push(`${route}: link to missing anchor ${href}`);
    if (localeOf(route) !== localeOf(target))
      errors.push(`${route}: link leaves the page's locale ${href}`);
  }
  if (others > limits.links) errors.push(`${route}: ${others} links to other chapters (at most ${limits.links})`);
}
if (book > limits.book) errors.push(`the book: ${book} characters in Traditional Chinese (at most ${limits.book})`);

for (const file of sourceFiles(fileURLToPath(new URL('../src/', import.meta.url))))
  lint(relative(root, file), readFileSync(file, 'utf8'));

// No code cites the book (`docs: PAGE §N`): the book holds the design, the code documents itself.
const tracked = execFileSync('git', ['ls-files', '-z', '--', '.', ':!docs'], { cwd: root, encoding: 'utf8' }).split('\0');
for (const file of tracked.filter(Boolean)) {
  let text;
  try {
    text = readFileSync(join(root, file), 'utf8');
  } catch {
    continue;
  }
  if (text.includes('\0')) continue;
  for (const m of text.matchAll(/docs: [a-z][a-z0-9-]*(?:\/[a-z0-9-]+)*(?: §\d+(?:\.\d+)*)?/g)) errors.push(`${file}: cites the book "${m[0]}"`);
}

// The combat net's figure must hold what the champion's combat net file's test states hold.
const champion = JSON.parse(readFileSync(new URL('../../eval/champions/sl.json', import.meta.url), 'utf8'));
const combat = JSON.parse(readFileSync(join(root, champion.options['combat-net']), 'utf8'));
const walk = JSON.parse(readFileSync(new URL('../src/data/combat-walk.json', import.meta.url), 'utf8'));
combat.tests.forEach((t, i) => {
  const off = Math.max(...t.outcome.map((p, k) => Math.abs(p - walk.tests[i].outcome[k])));
  if (off > 1e-3) errors.push(`combat-walk.json: test state ${i} is ${off} off the net file's`);
});

// V's figure must end in the value the champion's V file holds for its first test run.
const value = JSON.parse(readFileSync(join(root, champion.options.value), 'utf8'));
const vWalk = JSON.parse(readFileSync(new URL('../src/data/v-walk.json', import.meta.url), 'utf8'));
if (Math.abs(vWalk.test_value - value.tests[0].value) > 1e-9) errors.push(`v-walk.json: made from another V than the champion's (scripts/v-walk.py)`);

for (const error of errors) console.error(error);
console.log(`${pages.size} pages, ${checked} internal links, ${book} characters in Traditional Chinese, ${errors.length} problems`);
process.exit(errors.length ? 1 : 0);
