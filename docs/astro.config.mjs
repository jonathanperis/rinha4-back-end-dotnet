import { defineConfig } from 'astro/config';
import { satteri } from '@astrojs/markdown-satteri';
import sitemap from '@astrojs/sitemap';

const isProd = process.env.NODE_ENV === 'production';

export default defineConfig({
  integrations: [sitemap()],
  output: 'static',
  outDir: 'out',
  site: 'https://jonathanperis.github.io',
  base: isProd ? '/rinha4-back-end-dotnet' : '',
  markdown: {
    processor: satteri({ features: { directive: true } }),
  },
});
