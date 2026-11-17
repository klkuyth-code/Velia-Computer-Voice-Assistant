import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const electronDirectory = resolve('node_modules', 'electron');
const manifestPath = join(electronDirectory, 'package.json');
const installerPath = join(electronDirectory, 'install.js');
const executablePath = join(electronDirectory, 'dist', 'electron.exe');
const versionPath = join(electronDirectory, 'dist', 'version');

if (!existsSync(manifestPath) || !existsSync(installerPath)) {
  throw new Error('Electron dependency is missing. Run pnpm install before packaging.');
}

const expectedVersion = JSON.parse(readFileSync(manifestPath, 'utf8')).version;
const installedVersion = existsSync(versionPath)
  ? readFileSync(versionPath, 'utf8').trim().replace(/^v/, '')
  : '';

if (!existsSync(executablePath) || installedVersion !== expectedVersion) {
  const result = spawnSync(process.execPath, [installerPath], { stdio: 'inherit' });
  if (result.status !== 0) {
    throw new Error(`Electron ${expectedVersion} binary installation failed.`);
  }
}

if (!existsSync(executablePath)) {
  throw new Error(`Electron ${expectedVersion} executable is still missing after installation.`);
}

process.stdout.write(`VELIA_ELECTRON_DIST_OK ${expectedVersion}\n`);
