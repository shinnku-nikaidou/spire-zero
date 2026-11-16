// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import svelte from '@astrojs/svelte';
import mermaid from 'astro-mermaid';
import katex from 'katex';

// The sidebar is the book's table of contents: a flat list of its chapters, each labelled by its page's title.
const book = ['index', 'game', 'idea', 'environments', 'combat', 'combat-hidden', 'combat-net', 'macro', 'value-net', 'learning', 'evaluation', 'real-game', 'limits'];

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
    mathKatex,
    // Must come before Starlight so it sees mermaid code blocks first.
    mermaid({ autoTheme: true, enableLog: false }),
    // The macro brain's figures are Svelte islands (src/components/v/).
    svelte(),
    starlight({
      title: { en: 'STS2 AI Architecture', 'zh-CN': '杀戮尖塔 2 AI 架构手册', 'zh-TW': '殺戮尖塔 2 AI 架構手冊' },
      // The book's look (src/styles/spire.css): a stone wall and parchment, the sidebar as a map scroll.
      customCss: ['katex/dist/katex.min.css', './src/styles/viz.css', './src/styles/spire.css'],
      components: { PageTitle: './src/components/spire/PageTitle.astro' },
      // Latin text and numbers are set in Kreon; the stylesheet loads without holding up the first paint.
      head: [
        { tag: 'link', attrs: { rel: 'preconnect', href: 'https://fonts.gstatic.com', crossorigin: true } },
        {
          tag: 'link',
          attrs: {
            rel: 'stylesheet',
            href: 'https://fonts.googleapis.com/css2?family=Kreon:wght@400;500;700&display=swap',
            media: 'print',
            onload: "this.media='all'",
          },
        },
      ],
      // English is the default, at the site's root; the two Chinese books are under /zh-cn/ and /zh-tw/.
      defaultLocale: 'root',
      locales: {
        root: { label: 'English', lang: 'en' },
        'zh-cn': { label: '简体中文', lang: 'zh-CN' },
        'zh-tw': { label: '繁體中文', lang: 'zh-TW' },
      },
      sidebar: book,
    }),
  ],
});
