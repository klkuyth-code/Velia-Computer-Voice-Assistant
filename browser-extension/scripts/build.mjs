import { cp, mkdir, rm } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = resolve(root, 'dist');
await rm(output, { recursive: true, force: true });
await mkdir(output, { recursive: true });
await cp(resolve(root, 'public'), output, { recursive: true });
await build({
  entryPoints: {
    'service-worker': resolve(root, 'src/service-worker.ts'),
    content: resolve(root, 'src/content.ts'),
    popup: resolve(root, 'src/popup.ts'),
  },
  outdir: output,
  bundle: true,
  format: 'esm',
  platform: 'browser',
  target: 'chrome116',
  sourcemap: false,
  minify: false,
  legalComments: 'none',
});
