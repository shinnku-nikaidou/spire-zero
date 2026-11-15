// Writes the game's own names of the ids the figures show (cards, relics, potions, enchantments, afflictions, acts,
// encounters, ancients, characters, map point types, monsters, powers, rest site options), in each of the site's
// languages, from the game's .pck:
//   node scripts/game-names.mjs "PATH/Slay the Spire 2/SlayTheSpire2.pck"
// into src/data/names/{en,zh-CN,zh-TW}.json. Run it again when the game's version changes.
import { openSync, readSync, writeFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const pck = process.argv[2];
if (!pck) throw new Error('usage: node scripts/game-names.mjs PATH/SlayTheSpire2.pck');
const fd = openSync(pck, 'r');
const read = (pos, n) => {
  const buf = Buffer.alloc(n);
  readSync(fd, buf, 0, n, pos);
  return buf;
};

// Godot 4 PCK, format 3: magic, format, version (3), flags, file base, directory offset; then the directory.
const head = read(0, 40);
if (head.toString('latin1', 0, 4) !== 'GDPC' || head.readUInt32LE(4) !== 3) throw new Error(`${pck}: not a Godot 4 pack of format 3`);
const flags = head.readUInt32LE(20);
if (flags & 1) throw new Error(`${pck}: its directory is encrypted`);
const base = Number(head.readBigUInt64LE(24));
let at = Number(head.readBigUInt64LE(32));
const count = read(at, 4).readUInt32LE(0);
at += 4;
const files = new Map();
for (let i = 0; i < count; i++) {
  const len = read(at, 4).readUInt32LE(0);
  const entry = read(at + 4, len + 36);
  const path = entry.toString('utf8', 0, len).replace(/\0+$/, '');
  files.set(path, { offset: base + Number(entry.readBigUInt64LE(len)), size: Number(entry.readBigUInt64LE(len + 8)) });
  at += 4 + len + 36;
}

const json = (lang, name) => {
  const f = files.get(`localization/${lang}/${name}.json`);
  if (!f) throw new Error(`${pck}: no localization/${lang}/${name}.json`);
  return JSON.parse(read(f.offset, f.size).toString('utf8'));
};
// Every "ID.title" of a table ("ID.name" for monsters): the name the game shows for ID.
const titles = (table, key = 'title') =>
  Object.fromEntries(Object.entries(table).filter(([k]) => new RegExp(`^[A-Z0-9_]+\\.${key}$`).test(k)).map(([k, v]) => [k.slice(0, -key.length - 1), v]));

// The map's point types as V's map tokens name them (RunNet.MapTokens), and the legend entry the game names each by.
const legend = { Boss: 'BOSS', Elite: 'ELITE', Monster: 'ENEMY', RestSite: 'REST', Shop: 'MERCHANT', Treasure: 'TREASURE', Unknown: 'UNKNOWN', Ancient: 'ANCIENT' };
const langs = { en: 'eng', 'zh-CN': 'zhs', 'zh-TW': 'zht' };
const out = fileURLToPath(new URL('../src/data/names/', import.meta.url));
mkdirSync(out, { recursive: true });
for (const [site, game] of Object.entries(langs)) {
  const map = json(game, 'map');
  const names = {
    card: titles(json(game, 'cards')),
    relic: titles(json(game, 'relics')),
    potion: titles(json(game, 'potions')),
    enchant: titles(json(game, 'enchantments')),
    affliction: titles(json(game, 'afflictions')),
    act: titles(json(game, 'acts')),
    encounter: titles(json(game, 'encounters')),
    ancient: titles(json(game, 'ancients')),
    character: titles(json(game, 'characters')),
    // A word that holds a variable dropped, as the mod's Describe.Monster does: Test Subject's "#C{Count}".
    monster: Object.fromEntries(Object.entries(titles(json(game, 'monsters'), 'name')).map(([k, v]) => [k, v.replace(/\s*\S*\{[^}]*\}\S*/g, '')])),
    power: titles(json(game, 'powers')),
    map: Object.fromEntries(Object.entries(legend).map(([type, key]) => [type, map[`LEGEND_${key}.title`]])),
    // A rest site's options, by the id of their action (rest:SMITH).
    rest: Object.fromEntries(Object.entries(titles(json(game, 'rest_site_ui'), 'name')).filter(([k]) => k.startsWith('OPTION_')).map(([k, v]) => [k.slice('OPTION_'.length), v])),
  };
  writeFileSync(`${out}${site}.json`, JSON.stringify(names) + '\n');
  console.log(`${site}: ${Object.entries(names).map(([k, v]) => `${Object.keys(v).length} ${k}`).join(', ')}`);
}
