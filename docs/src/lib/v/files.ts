// The net the figures run: its net file (in the repository's weights/) and where the site serves it.
export const NET = 'v9-0';
export const netFile = (root: string) => `${root}/weights/all/${NET}.json`;
export const metaUrl = (base: string) => `${base.replace(/\/$/, '')}/data/${NET}.json`;
export const floatsUrl = (base: string) => `${base.replace(/\/$/, '')}/data/${NET}.bin`;
