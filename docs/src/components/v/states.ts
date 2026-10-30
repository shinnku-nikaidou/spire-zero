// The run states a reader can pick in the figures: the four real decisions of a run v6-3-2 won
// (src/data/macro-examples.json) and the net file's eight held-out test states.
import examples from '../../data/macro-examples.json';
import type { RunView } from '../../lib/v/encode.ts';
import type { Meta } from '../../lib/v/net.ts';
import { surface, words } from './i18n.ts';
import type { Lang } from './names.ts';

export interface Picked {
  id: string;
  label: string;
  view: RunView;
}

export interface Option {
  action: string;
  value: number; // the value `sim afterstates` logged
  after: Record<string, unknown>;
}

export interface Decision {
  decision_id: number;
  surface: string;
  chosen: string;
  run_view: RunView;
  options: Option[];
}

export const EXAMPLES = examples as unknown as {
  source: { job: string; run: string; seed: string; outcome: string; floor: number; rev: string; champion: string; values: string };
  decisions: Record<'card_reward' | 'rest' | 'map' | 'shop', Decision>;
  series: { d: number; s: string; floor: number; hp: number; max_hp: number; chosen: string; v: number; best: number; n: number }[];
};

export const DECISIONS = ['card_reward', 'rest', 'map', 'shop'] as const;

export function states(lang: Lang, meta: Meta): Picked[] {
  const t = words(lang);
  const floor = t('floor');
  const real = DECISIONS.map((k) => {
    const d = EXAMPLES.decisions[k];
    const v = d.run_view;
    return { id: k, label: `${t('wonRun')} · ${floor} ${v.floor} · ${surface(lang, d.surface)}`, view: v };
  });
  const held = meta.tests.map((x, i) => {
    const v = x.run;
    return { id: `test-${i}`, label: `${t('heldOut')} ${i + 1} · ${floor} ${v.floor} · HP ${v.hp}/${v.max_hp}`, view: v };
  });
  return [...real, ...held];
}
