import { rmSync } from 'node:fs';

for (const directory of ['../dist-electron', '../dist-renderer']) {
  rmSync(new URL(directory, import.meta.url), { recursive: true, force: true });
}
