// The net the figures run: its net file (in the repository's weights/) and where the site serves it.
export const NET = 'v8-1';
export const netFile = (root: string) => `${root}/weights/silent/${NET}.json`;
export const metaUrl = (base: string) => `${base.replace(/\/$/, '')}/data/${NET}.json`;
export const floatsUrl = (base: string) => `${base.replace(/\/$/, '')}/data/${NET}.bin`;
