// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import mermaid from 'astro-mermaid';
import katex from 'katex';

// Sidebar mirrors the doc map on the overview page. Labels and status badges
// come from each page's own frontmatter, so every locale shows its own text.
const architecture = [
  'arch/environments',
  'arch/protocol',
  'arch/gamebridge',
  'arch/simulator',
  'arch/mod',
  'arch/harness',
  'arch/combat-brain',
  'arch/macro-brain',
  'arch/learning',
  'arch/evaluation',
  'arch/output',
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
  integrations: [
    sectionIds,
    mathKatex,
    // Must come before Starlight so it sees mermaid code blocks first.
    mermaid({ autoTheme: true, enableLog: false }),
    starlight({
      title: { 'zh-CN': '杀戮尖塔 2 AI 架构手册', 'zh-TW': '殺戮尖塔 2 AI 架構手冊', en: 'STS2 AI Architecture' },
      customCss: ['katex/dist/katex.min.css'],
      defaultLocale: 'root',
      locales: {
        root: { label: '简体中文', lang: 'zh-CN' },
        'zh-tw': { label: '繁體中文', lang: 'zh-TW' },
        en: { label: 'English', lang: 'en' },
      },
      sidebar: [
        {
          label: '总览',
          translations: { 'zh-TW': '總覽', en: 'Overview' },
          items: ['index', 'status', 'glossary'],
        },
        {
          label: '子系统',
          translations: { 'zh-TW': '子系統', en: 'Subsystems' },
          items: architecture,
        },
      ],
    }),
  ],
});
