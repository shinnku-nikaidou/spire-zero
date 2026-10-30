// The browser's V: the figures' net fetched once per page and shared by every figure on it.
import { floatsUrl, metaUrl } from './files.ts';
import { V, type Meta } from './net.ts';

let loading: Promise<{ v: V; meta: Meta }> | null = null;

export function loadV(): Promise<{ v: V; meta: Meta }> {
  loading ??= (async () => {
    const base = import.meta.env.BASE_URL;
    const [meta, buf] = await Promise.all([
      fetch(metaUrl(base)).then((r) => r.json() as Promise<Meta>),
      fetch(floatsUrl(base)).then((r) => r.arrayBuffer()),
    ]);
    return { v: V.fromPacked(meta, new Float32Array(buf)), meta };
  })();
  return loading;
}
