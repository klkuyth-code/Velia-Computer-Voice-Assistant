import { cp, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const electronBuilderPackage = require.resolve('electron-builder/package.json');
const requireFromElectronBuilder = createRequire(electronBuilderPackage);
const { build, Platform } = requireFromElectronBuilder('electron-builder');
const nsisUtil = requireFromElectronBuilder('app-builder-lib/out/targets/nsis/nsisUtil.js');

const originalTemplatesDir = nsisUtil.nsisTemplatesDir;
const temporaryRoot = await mkdtemp(join(tmpdir(), 'velia-nsis-'));
const temporaryTemplatesDir = join(temporaryRoot, 'nsis');

try {
  await cp(originalTemplatesDir, temporaryTemplatesDir, { recursive: true });
  nsisUtil.nsisTemplatesDir = temporaryTemplatesDir;

  await build({
    targets: Platform.WINDOWS.createTarget(['nsis']),
  });
} finally {
  nsisUtil.nsisTemplatesDir = originalTemplatesDir;
  await rm(temporaryRoot, { recursive: true, force: true });
}
