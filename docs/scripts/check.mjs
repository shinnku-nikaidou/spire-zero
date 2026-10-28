// Checks the built site in dist/ (run after `astro build`):
// - every internal link in page content reaches an existing page and anchor,
// - links stay in the locale of the page they are on,
// - every page exists in all three locales (Simplified Chinese at the root,
//   Traditional Chinese under /zh-tw/, English under /en/),
// - no Markdown bold was left unrendered (in Chinese, `**入口：**到达` does not render;
//   write `**入口**：到达`).
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

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
  });
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

for (const error of errors) console.error(error);
console.log(`${pages.size} pages, ${checked} internal links, ${errors.length} problems`);
process.exit(errors.length ? 1 : 0);
