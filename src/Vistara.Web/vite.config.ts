import react from '@vitejs/plugin-react';
import { defineConfig, type Plugin } from 'vitest/config';
import { socialPreviewTags } from './src/build/socialPreview';

/**
 * Adds the Open Graph preview only when the deployment origin is known, so a
 * build never advertises an unresolvable image URL.
 */
function socialPreview(base: string): Plugin {
  return {
    name: 'vistara-social-preview',
    transformIndexHtml: {
      order: 'pre',
      handler: () => ({
        html: '',
        tags: [...socialPreviewTags(process.env.VITE_SITE_ORIGIN, base)],
      }),
    },
  };
}

export default defineConfig(({ mode }) => {
  const isPagesBuild = mode === 'pages';
  const base = isPagesBuild ? '/Vistara/' : '/';

  return {
    base,
    plugins: [react(), socialPreview(base)],
    build: {
      outDir: isPagesBuild ? 'dist-pages' : 'dist',
      sourcemap: !isPagesBuild,
    },
    test: {
      environment: 'jsdom',
      setupFiles: './src/test/setup.ts',
      restoreMocks: true,
    },
  };
});
