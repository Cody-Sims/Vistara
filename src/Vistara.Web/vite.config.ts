import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig(({ mode }) => {
  const isPagesBuild = mode === 'pages';

  return {
    base: isPagesBuild ? '/Vistara/' : '/',
    plugins: [react()],
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
