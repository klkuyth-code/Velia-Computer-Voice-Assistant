import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { isPcmWav, WakeWordService } from './WakeWordService';

const temporaryDirectories: string[] = [];

afterEach(async () => {
  await Promise.all(temporaryDirectories.splice(0).map((path) => rm(path, { recursive: true, force: true })));
});

describe('WakeWordService settings', () => {
  it('defaults to off and refuses to enable an incomplete runtime', async () => {
    const root = await temporaryRoot();
    const service = new WakeWordService({ runtimeDirectory: join(root, 'runtime'), settingsPath: join(root, 'settings.json') });
    expect(service.status()).toMatchObject({ enabled: false, available: false, localOnly: true });
    await expect(service.setEnabled(true)).rejects.toThrow('本地唤醒组件不可用');
  });

  it('persists only the non-secret enabled flag when runtime files exist', async () => {
    const root = await temporaryRoot();
    const runtime = join(root, 'runtime');
    await mkdir(runtime);
    await Promise.all(['whisper-cli.exe', 'ggml-base.bin', 'ggml.dll', 'ggml-base.dll', 'whisper.dll']
      .map((name) => writeFile(join(runtime, name), 'test')));
    const settingsPath = join(root, 'settings.json');
    const service = new WakeWordService({ runtimeDirectory: runtime, settingsPath });
    expect((await service.setEnabled(true)).enabled).toBe(true);
    expect(JSON.parse(await readFile(settingsPath, 'utf8'))).toEqual({ schema: 'velia-wake-word-setting/v1', enabled: true });
    expect(new WakeWordService({ runtimeDirectory: runtime, settingsPath }).status().enabled).toBe(true);
  });
});

describe('wake-word WAV validation', () => {
  it('accepts standard PCM WAV chunks even when fmt has an extension', () => {
    expect(isPcmWav(createPcmWav(18))).toBe(true);
    expect(isPcmWav(createPcmWav(16))).toBe(true);
  });

  it('rejects malformed or non-mono PCM data', () => {
    const stereo = createPcmWav(16);
    stereo.writeUInt16LE(2, 22);
    expect(isPcmWav(stereo)).toBe(false);
    expect(isPcmWav(Buffer.from('not a wave'))).toBe(false);
  });
});

async function temporaryRoot(): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), 'velia-wake-test-'));
  temporaryDirectories.push(root);
  return root;
}

function createPcmWav(formatSize: 16 | 18): Buffer {
  const data = Buffer.alloc(320);
  const output = Buffer.alloc(12 + 8 + formatSize + 8 + data.length);
  output.write('RIFF', 0, 'ascii');
  output.writeUInt32LE(output.length - 8, 4);
  output.write('WAVE', 8, 'ascii');
  output.write('fmt ', 12, 'ascii');
  output.writeUInt32LE(formatSize, 16);
  output.writeUInt16LE(1, 20);
  output.writeUInt16LE(1, 22);
  output.writeUInt32LE(16_000, 24);
  output.writeUInt32LE(32_000, 28);
  output.writeUInt16LE(2, 32);
  output.writeUInt16LE(16, 34);
  output.write('data', 20 + formatSize, 'ascii');
  output.writeUInt32LE(data.length, 24 + formatSize);
  data.copy(output, 28 + formatSize);
  return output;
}
