// How the figures draw a map point: its type's colour (the categorical slots of viz.css) and a one-letter glyph.
export const TYPES = ['Monster', 'Elite', 'RestSite', 'Shop', 'Unknown', 'Treasure', 'Boss', 'Ancient'];

export const COLOUR: Record<string, string> = {
  Monster: 'var(--v-s1)', Elite: 'var(--v-s2)', RestSite: 'var(--v-s3)', Shop: 'var(--v-s4)',
  Unknown: 'var(--v-s5)', Treasure: 'var(--v-s6)', Boss: 'var(--v-s7)', Ancient: 'var(--v-muted)',
};

export const GLYPH: Record<string, string> = { Monster: 'M', Elite: 'E', RestSite: 'R', Shop: '$', Unknown: '?', Treasure: 'T', Boss: 'B', Ancient: 'A' };

export const DX = 34;
export const DY = 24;
