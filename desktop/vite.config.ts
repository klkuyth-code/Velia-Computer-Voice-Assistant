import { defineConfig } from 'vite';
import { configDefaults } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  base: './',
  plugins: [react()],
  build: {
    outDir: 'dist-renderer',
    emptyOutDir: true,
    sourcemap: false,
  },
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
  },
  test: {
    environment: 'jsdom',
    globals: true,
    // Windows occasionally fails to page dozens of jsdom fork workers at
    // once with an UNKNOWN read error. Four workers keep the suite fast while
    // making the release gate deterministic on ordinary customer hardware.
    maxWorkers: 4,
    minWorkers: 1,
    exclude: [...configDefaults.exclude, 'dist-electron/**', 'dist-renderer/**', 'release/**'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'json', 'html'],
    },
  },
});
