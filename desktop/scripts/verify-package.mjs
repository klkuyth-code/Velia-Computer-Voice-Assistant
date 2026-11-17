import { createRequire } from 'node:module';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

const require = createRequire(import.meta.url);
const asar = require('@electron/asar');
const archivePath = resolve(process.argv[2] ?? 'release/win-unpacked/resources/app.asar');

if (!statSync(archivePath).isFile()) {
  throw new Error(`Packaged archive is missing: ${archivePath}`);
}

const normalizeEntry = (entry) => entry.replaceAll('\\', '/');
const requiredEntries = [
  '/dist-electron/main.js',
  '/dist-renderer/index.html',
  '/dist-electron/core/CoreMessage.js',
  '/node_modules/@velia/protocol/package.json',
  '/node_modules/@velia/protocol/dist/index.js',
  '/node_modules/node-edge-tts/package.json',
  '/node_modules/node-edge-tts/dist/edge-tts.js',
  '/node_modules/node-edge-tts/dist/drm.js',
  '/node_modules/https-proxy-agent/package.json',
  '/node_modules/ws/package.json',
  '/node_modules/yargs/package.json',
];
const archiveEntries = new Set(asar.listPackage(archivePath).map(normalizeEntry));
const missing = requiredEntries.filter((entry) => !archiveEntries.has(entry));
if (missing.length > 0) {
  throw new Error(`Packaged runtime dependencies are incomplete: ${missing.join(', ')}`);
}

const rendererAssets = [...archiveEntries].filter((entry) => entry.startsWith('/dist-renderer/assets/'));
if (!rendererAssets.some((entry) => entry.endsWith('.js')) ||
    !rendererAssets.some((entry) => entry.endsWith('.css'))) {
  throw new Error('Packaged renderer assets are incomplete: JavaScript and CSS bundles are required.');
}

const forbiddenEntries = [
  '/dist-electron/smoke.js',
];
const includedForbiddenEntries = forbiddenEntries.filter((entry) => archiveEntries.has(entry));
if (includedForbiddenEntries.length > 0) {
  throw new Error(`Packaged application contains development-only files: ${includedForbiddenEntries.join(', ')}`);
}

const forbiddenDevelopmentEntries = [...archiveEntries].filter((entry) => {
  const normalized = entry.toLowerCase();
  return /\.(?:map|ts|tsx|cs|csproj|sln|slnx|pdb|vault|vlic|vreq|pem|key)$/.test(normalized) ||
    /^\/(?:src|test|tests|\.git|license-studio)(?:\/|$)/.test(normalized) ||
    normalized.includes('license-ledger') || normalized.includes('production-issuer');
});
if (forbiddenDevelopmentEntries.length > 0) {
  throw new Error(
    `Packaged application contains source, test, or issuer files: ${forbiddenDevelopmentEntries.slice(0, 5).join(', ')}`,
  );
}

const protocolManifest = JSON.parse(
  asar.extractFile(archivePath, 'node_modules\\@velia\\protocol\\package.json').toString('utf8'),
);
if (protocolManifest.name !== '@velia/protocol') {
  throw new Error('Packaged protocol manifest is invalid.');
}

const forbiddenProtocolEntries = [...archiveEntries].filter((entry) =>
  entry.startsWith('/node_modules/@velia/protocol/') &&
  (entry.includes('/src/') || entry.includes('/test/') || entry.includes('/dotnet/') ||
    entry.endsWith('.map') || entry.endsWith('.ts') || entry.endsWith('.cs')),
);
if (forbiddenProtocolEntries.length > 0) {
  throw new Error(`Packaged protocol contains development files: ${forbiddenProtocolEntries.slice(0, 5).join(', ')}`);
}

const wakeRuntime = join(dirname(archivePath), 'wake-word');
const requiredWakeFiles = [
  'whisper-cli.exe',
  'ggml-base.bin',
  'WHISPER_CPP_LICENSE.txt',
  'OPENAI_WHISPER_LICENSE.txt',
  'RUNTIME-MANIFEST.json',
];
for (const name of requiredWakeFiles) {
  if (!statSync(join(wakeRuntime, name)).isFile()) throw new Error(`Packaged wake runtime is missing ${name}.`);
}
if (statSync(join(wakeRuntime, 'ggml-base.bin')).size !== 147951465) {
  throw new Error('Packaged wake model size is invalid.');
}
if (!readdirSync(wakeRuntime).some((name) => name.toLowerCase().endsWith('.dll'))) {
  throw new Error('Packaged whisper.cpp runtime DLLs are missing.');
}
const wakeManifest = JSON.parse(readFileSync(join(wakeRuntime, 'RUNTIME-MANIFEST.json'), 'utf8'));
if (wakeManifest.schema !== 'velia-wake-runtime/v1' || wakeManifest.whisperCppVersion !== 'v1.9.2' ||
    wakeManifest.modelSha1 !== '465707469ff3a37a2b9b8d8f89f2f99de7299dac') {
  throw new Error('Packaged wake runtime manifest is invalid.');
}

process.stdout.write('VELIA_PACKAGE_CONTENT_OK\n');
