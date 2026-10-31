// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import svelte from '@astrojs/svelte';
import mermaid from 'astro-mermaid';
import katex from 'katex';

// The sidebar is the book's table of contents, as the overview's doc map lists it: an overview, four parts and the
// appendices. Labels and status badges come from each page's own frontmatter, so every locale shows its own text.
const environments = ['arch/environments', 'arch/environments/information', 'arch/environments/worlds'];
const simulator = [
  'arch/simulator',
  'arch/simulator/headless',
  'arch/simulator/combat',
  'arch/simulator/runs',
  'arch/simulator/worlds',
  'arch/simulator/pool',
  'arch/simulator/reference',
];
const protocol = ['arch/protocol', 'arch/protocol/wire', 'arch/protocol/observation', 'arch/protocol/worlds'];
const gamebridge = ['arch/gamebridge', 'arch/gamebridge/observe', 'arch/gamebridge/actions'];
const harness = ['arch/harness', 'arch/harness/routing', 'arch/harness/decision-log'];
const mod = ['arch/mod', 'arch/mod/loop', 'arch/mod/sl-record', 'arch/mod/auto'];
const learning = ['arch/learning', 'arch/learning/combat-net', 'arch/learning/loop', 'arch/learning/reference'];
const evaluation = ['arch/evaluation', 'arch/evaluation/metrics', 'arch/evaluation/gates', 'arch/evaluation/champions'];
const macroBrain = [
  'arch/macro-brain',
  'arch/macro-brain/afterstates',
  'arch/macro-brain/search',
  'arch/macro-brain/network',
  'arch/macro-brain/value',
  'arch/macro-brain/selfplay',
  'arch/macro-brain/next',
];
const combatBrain = ['arch/combat-brain', 'arch/combat-brain/leaf', 'arch/combat-brain/network', 'arch/combat-brain/walkthrough', 'arch/combat-brain/shared'];
const slPlanner = ['arch/sl-planner', 'arch/sl-planner/turn-search', 'arch/sl-planner/beam', 'arch/sl-planner/budget'];
const pimc = ['arch/pimc', 'arch/pimc/search', 'arch/pimc/budget'];
const book = [
  { label: '前言', translations: { 'zh-TW': '前言', en: 'Introduction' }, items: ['index'] },
  { label: '第一部　问题', translations: { 'zh-TW': '第一部　問題', en: 'Part I: The problem' }, items: ['arch/game', { label: '环境', translations: { 'zh-TW': '環境', en: 'Environments' }, items: environments }] },
  {
    label: '第二部　决策',
    translations: { 'zh-TW': '第二部　決策', en: 'Part II: Deciding' },
    items: [
      { label: '战斗大脑', translations: { 'zh-TW': '戰鬥大腦', en: 'Combat brain' }, items: combatBrain },
      { label: 'SL 规划器', translations: { 'zh-TW': 'SL 規劃器', en: 'SL planner' }, items: slPlanner },
      { label: 'NoSL PIMC', translations: { 'zh-TW': 'NoSL PIMC', en: 'NoSL PIMC' }, items: pimc },
      { label: '宏观大脑', translations: { 'zh-TW': '宏觀大腦', en: 'Macro brain' }, items: macroBrain },
    ],
  },
  {
    label: '第三部　学习与评估',
    translations: { 'zh-TW': '第三部　學習與評估', en: 'Part III: Learning and evaluation' },
    items: [{ label: '学习', translations: { 'zh-TW': '學習', en: 'Learning' }, items: learning }, { label: '评估', translations: { 'zh-TW': '評估', en: 'Evaluation' }, items: evaluation }],
  },
  {
    label: '第四部　机器',
    translations: { 'zh-TW': '第四部　機器', en: 'Part IV: The machinery' },
    items: [{ label: '模拟器', translations: { 'zh-TW': '模擬器', en: 'Simulator' }, items: simulator }, { label: 'Harness', translations: { 'zh-TW': 'Harness', en: 'Harness' }, items: harness }, { label: '协议', translations: { 'zh-TW': '協定', en: 'Protocol' }, items: protocol }, { label: 'GameBridge', translations: { 'zh-TW': 'GameBridge', en: 'GameBridge' }, items: gamebridge }, { label: 'Mod', translations: { 'zh-TW': 'Mod', en: 'Mod' }, items: mod }, 'arch/output'],
  },
  { label: '附录', translations: { 'zh-TW': '附錄', en: 'Appendices' }, items: ['status', 'glossary'] },
];

// Numbered headings ("## 5." / "### 5.1") get the id "sec-5" / "sec-5.1" in every
// locale, so "Simulator §5.1" links survive translation and heading edits.
// Runs before the processor's own heading-id step, which keeps an existing id.
const sectionIds = {
  name: 'section-ids',
  hooks: {
    'astro:config:setup': ({ config }) => {
      config.markdown.processor.options.hastPlugins.push({
        name: 'section-ids',
        element: {
          filter: ['h2', 'h3', 'h4'],
          visit(node, ctx) {
            const number = /^(\d+(?:\.\d+)*)[.\s]/.exec(ctx.textContent(node));
            if (number) ctx.setProperty(node, 'id', `sec-${number[1]}`);
          },
        },
      });
    },
  },
};

// $…$ and $$…$$ render with KaTeX at build time. The processor parses them
// into code.language-math (inside a pre for display math); this swaps each
// for KaTeX's HTML before Expressive Code can take the pre as a code block.
const isMath = (node) => String(node.properties?.className ?? '').includes('language-math');
const mathKatex = {
  name: 'math-katex',
  hooks: {
    'astro:config:setup': ({ config }) => {
      const options = config.markdown.processor.options;
      options.features = { ...options.features, math: true };
      options.hastPlugins.push({
        name: 'math-katex',
        element: {
          filter: ['pre', 'code'],
          visit(node, ctx) {
            if (node.tagName === 'pre') {
              const code = node.children?.find((child) => child.tagName === 'code');
              if (!code || !isMath(code)) return;
              const html = katex.renderToString(ctx.textContent(code), { displayMode: true, throwOnError: true });
              ctx.replaceNode(node, { type: 'raw', value: html });
            } else if (isMath(node) && ctx.parent(node)?.tagName !== 'pre') {
              const html = katex.renderToString(ctx.textContent(node), { throwOnError: true });
              ctx.replaceNode(node, { type: 'raw', value: html });
            }
          },
        },
      });
    },
  },
};

export default defineConfig({
  // Where the site is served (docs/wrangler.jsonc); Starlight uses it for canonical links and the sitemap.
  site: 'https://sts2-docs.shinnkunikaidou.workers.dev',
  integrations: [
    sectionIds,
    mathKatex,
    // Must come before Starlight so it sees mermaid code blocks first.
    mermaid({ autoTheme: true, enableLog: false }),
    // The macro brain's figures are Svelte islands (src/components/v/).
    svelte(),
    starlight({
      title: { 'zh-CN': '杀戮尖塔 2 AI 架构手册', 'zh-TW': '殺戮尖塔 2 AI 架構手冊', en: 'STS2 AI Architecture' },
      customCss: ['katex/dist/katex.min.css', './src/styles/viz.css'],
      defaultLocale: 'root',
      locales: {
        root: { label: '简体中文', lang: 'zh-CN' },
        'zh-tw': { label: '繁體中文', lang: 'zh-TW' },
        en: { label: 'English', lang: 'en' },
      },
      sidebar: book,
    }),
  ],
});
