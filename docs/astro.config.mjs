// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import mermaid from 'astro-mermaid';

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

export default defineConfig({
  integrations: [
    sectionIds,
    // Must come before Starlight so it sees mermaid code blocks first.
    mermaid({ autoTheme: true, enableLog: false }),
    starlight({
      title: { 'zh-CN': '杀戮尖塔 2 AI 架构手册', en: 'STS2 AI Architecture' },
      defaultLocale: 'root',
      locales: {
        root: { label: '简体中文', lang: 'zh-CN' },
        en: { label: 'English', lang: 'en' },
      },
      sidebar: [
        {
          label: '总览',
          translations: { en: 'Overview' },
          items: ['index', 'status', 'glossary'],
        },
        {
          label: '子系统',
          translations: { en: 'Subsystems' },
          items: architecture,
        },
      ],
    }),
  ],
});
