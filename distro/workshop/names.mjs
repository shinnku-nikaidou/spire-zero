// Prints, as JSON, for each language in the game's .pck: the characters' titles and every short string whose key names
// Ascension in the tables named after the pck (make.py reads tmp/workshop/names.json, written by:
//   node distro/workshop/names.mjs PATH/SlayTheSpire2.pck ascension main_menu_ui > tmp/workshop/names.json).
// With "list" instead of tables, it lists the pck's languages and the English tables.
import { openSync, readSync } from 'node:fs';
const fd = openSync(process.argv[2], 'r');
const read = (pos, n) => { const b = Buffer.alloc(n); readSync(fd, b, 0, n, pos); return b; };
const head = read(0, 40);
const base = Number(head.readBigUInt64LE(24));
let at = Number(head.readBigUInt64LE(32));
const count = read(at, 4).readUInt32LE(0); at += 4;
const files = new Map();
for (let i = 0; i < count; i++) {
  const len = read(at, 4).readUInt32LE(0);
  const e = read(at + 4, len + 36);
  files.set(e.toString('utf8', 0, len).replace(/\0+$/, ''), { offset: base + Number(e.readBigUInt64LE(len)), size: Number(e.readBigUInt64LE(len + 8)) });
  at += 4 + len + 36;
}
const json = (lang, name) => { const f = files.get(`localization/${lang}/${name}.json`); return f ? JSON.parse(read(f.offset, f.size).toString('utf8')) : null; };
const langs = [...new Set([...files.keys()].filter(p => p.startsWith('localization/')).map(p => p.split('/')[1]))];
if (process.argv[3] === 'list') { console.log(langs.join(' ')); console.log([...files.keys()].filter(p => p.startsWith('localization/eng/')).join('\n')); process.exit(0); }
const out = {};
for (const lang of langs) {
  const c = json(lang, 'characters') ?? {};
  const row = {};
  for (const id of ['IRONCLAD', 'SILENT', 'DEFECT', 'REGENT', 'NECROBINDER', 'FOOL']) row[id] = c[`${id}.title`];
  for (const name of process.argv.slice(3)) {
    const t = json(lang, name) ?? {};
    for (const [k, v] of Object.entries(t)) if (/ASCENSION/i.test(k) && typeof v === 'string' && v.length < 40) row[`${name}:${k}`] = v;
  }
  out[lang] = row;
}
console.log(JSON.stringify(out, null, 1));
