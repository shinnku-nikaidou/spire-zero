// Build time only: the figures' net file, read from the repository and packed for the browser.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { NET, netFile } from './files.ts';
import { pack, type NetFile } from './net.ts';
import { strip } from './strip.ts';

let cache: ReturnType<typeof pack> | null = null;

/** docs/ is the build's working directory; the weights are in the repository's root. */
export function packed() {
  if (!cache) {
    const file: NetFile = JSON.parse(readFileSync(netFile(resolve(process.cwd(), '..')), 'utf8'));
    if (file.name !== NET) throw new Error(`${netFile('..')} is net ${file.name}, not ${NET}`);
    cache = pack(file, strip);
  }
  return cache;
}
