import { createHash } from 'node:crypto';
import react from '@vitejs/plugin-react';
import { defineConfig, type Plugin } from 'vitest/config';
import {
  buildServiceWorker,
  isCacheableAsset,
} from './src/build/serviceWorker';
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

/**
 * Emits a service worker that precaches only the versioned shell and brand
 * assets of this build. Nothing from the API, media, delivery, or share
 * surfaces is ever cached.
 */
function serviceWorker(base: string): Plugin {
  const brandAssets = [
    'favicon.svg',
    'favicon.ico',
    'apple-touch-icon.png',
    'icon-192.png',
    'icon-512.png',
    'icon-maskable-512.png',
    'manifest.webmanifest',
  ];

  return {
    name: 'vistara-service-worker',
    apply: 'build',
    generateBundle(_options, bundle) {
      const emitted = Object.keys(bundle)
        .map((name) => `${base}${name}`)
        .filter(isCacheableAsset);
      const precache = [
        `${base}index.html`,
        ...emitted,
        ...brandAssets.map((asset) => `${base}${asset}`),
      ];
      const cacheName = `vistara-shell-${createHash('sha256')
        .update(precache.join('\n'))
        .digest('hex')
        .slice(0, 12)}`;

      this.emitFile({
        type: 'asset',
        fileName: 'sw.js',
        source: buildServiceWorker({
          cacheName,
          shell: `${base}index.html`,
          precache,
        }),
      });
    },
  };
}

export default defineConfig(({ mode }) => {
  const isPagesBuild = mode === 'pages';
  const base = isPagesBuild ? '/Vistara/' : '/';

  return {
    base,
    plugins: [react(), socialPreview(base), serviceWorker(base)],
    build: {
      outDir: isPagesBuild ? 'dist-pages' : 'dist',
      sourcemap: !isPagesBuild,
    },
    test: {
      environment: 'jsdom',
      setupFiles: './src/test/setup.ts',
      restoreMocks: true,
      // Whole-application renders are slow on a loaded machine; the defaults
      // fail these suites for scheduling rather than for behaviour.
      testTimeout: 30_000,
      hookTimeout: 30_000,
    },
  };
});
