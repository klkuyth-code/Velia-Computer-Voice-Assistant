import { existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const desktopRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const workspaceRoot = resolve(desktopRoot, '..');
const dotnetRoot = resolve(workspaceRoot, '.tools', 'dotnet');
const dotnet = resolve(dotnetRoot, process.platform === 'win32' ? 'dotnet.exe' : 'dotnet');
const project = resolve(workspaceRoot, 'core', 'src', 'Velia.Core', 'Velia.Core.csproj');
const output = resolve(workspaceRoot, 'core', 'artifacts', 'win-x64');

if (!existsSync(dotnet)) {
  throw new Error(`Bundled .NET SDK is missing: ${dotnet}`);
}

const result = spawnSync(dotnet, [
  'publish', project,
  '--configuration', 'Release',
  '--runtime', 'win-x64',
  '--self-contained', 'true',
  '--output', output,
  '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:DebugType=None',
  '-p:DebugSymbols=false',
], {
  cwd: workspaceRoot,
  env: {
    ...process.env,
    DOTNET_ROOT: dotnetRoot,
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  },
  stdio: 'inherit',
});

if (result.error) throw result.error;
if (result.status !== 0) {
  throw new Error(`Velia Core publish failed with exit code ${result.status ?? 'unknown'}.`);
}

const executable = resolve(output, 'Velia.Core.exe');
if (!existsSync(executable)) {
  throw new Error(`Velia Core publish did not create ${executable}`);
}

process.stdout.write('VELIA_CORE_PUBLISH_OK\n');
