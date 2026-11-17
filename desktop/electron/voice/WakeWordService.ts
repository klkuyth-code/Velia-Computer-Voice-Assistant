import { execFile } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { mkdir, mkdtemp, rename, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { promisify } from 'node:util';
import type { WakeWordDetectionResult, WakeWordStatus } from '@velia/protocol';
import { containsVeliaWakeWord } from './WakeWordMatcher.js';

const execFileAsync = promisify(execFile);
const maximumWavBytes = 700_000;

export interface WakeWordServiceOptions {
  readonly runtimeDirectory: string;
  readonly settingsPath: string;
}

export class WakeWordService {
  readonly #executablePath: string;
  readonly #modelPath: string;
  #enabled: boolean;

  constructor(readonly options: WakeWordServiceOptions) {
    this.#executablePath = join(options.runtimeDirectory, 'whisper-cli.exe');
    this.#modelPath = join(options.runtimeDirectory, 'ggml-base.bin');
    this.#enabled = loadEnabledSetting(options.settingsPath);
  }

  status(): WakeWordStatus {
    const requiredPaths = [
      this.#executablePath,
      this.#modelPath,
      join(this.options.runtimeDirectory, 'ggml.dll'),
      join(this.options.runtimeDirectory, 'ggml-base.dll'),
      join(this.options.runtimeDirectory, 'whisper.dll'),
    ];
    const available = requiredPaths.every((path) => existsSync(path));
    return {
      enabled: this.#enabled,
      available,
      keyword: '嘿 Velia',
      localOnly: true,
      ...(!available ? { unavailableReason: '本地唤醒组件尚未随安装包完整部署。' } : {}),
    };
  }

  async setEnabled(enabled: boolean): Promise<WakeWordStatus> {
    if (enabled && !this.status().available) throw new Error('本地唤醒组件不可用，请重新安装完整版本。');
    this.#enabled = enabled;
    await saveEnabledSetting(this.options.settingsPath, enabled);
    return this.status();
  }

  async detect(wavBase64: string): Promise<WakeWordDetectionResult> {
    if (!this.#enabled) throw new Error('唤醒词未开启。');
    if (!this.status().available) throw new Error('本地唤醒组件不可用。');
    const wav = Buffer.from(wavBase64, 'base64');
    if (!isPcmWav(wav) || wav.length > maximumWavBytes) throw new TypeError('Wake-word audio is invalid.');

    const temporaryDirectory = await mkdtemp(join(tmpdir(), 'velia-wake-'));
    const wavPath = join(temporaryDirectory, 'wake.wav');
    const startedAt = performance.now();
    try {
      await writeFile(wavPath, wav, { flag: 'wx' });
      const { stdout } = await execFileAsync(this.#executablePath, [
        '-m', this.#modelPath,
        '-f', wavPath,
        '-l', 'en',
        '--no-timestamps',
        '-t', '4',
        '-np',
        '--prompt', 'Hey Velia',
        '-sns',
      ], {
        timeout: 15_000,
        maxBuffer: 1024 * 1024,
        windowsHide: true,
        env: { ...process.env, PATH: `${this.options.runtimeDirectory};${process.env.PATH ?? ''}` },
      });
      const transcript = stdout.trim().replace(/\[[^\]]*\]/gu, '').trim().slice(0, 500);
      return {
        detected: containsVeliaWakeWord(transcript),
        transcript,
        latencyMs: Math.round(performance.now() - startedAt),
      };
    } finally {
      await rm(temporaryDirectory, { recursive: true, force: true });
    }
  }
}

function loadEnabledSetting(path: string): boolean {
  try {
    const raw = requireFile(path);
    const parsed: unknown = JSON.parse(raw);
    return isRecord(parsed) && parsed.schema === 'velia-wake-word-setting/v1' && parsed.enabled === true;
  } catch {
    return false;
  }
}

function requireFile(path: string): string {
  if (!existsSync(path)) return '';
  return readFileSync(path, 'utf8');
}

async function saveEnabledSetting(path: string, enabled: boolean): Promise<void> {
  await mkdir(dirname(path), { recursive: true });
  const temporaryPath = `${path}.${process.pid}.tmp`;
  await writeFile(temporaryPath, JSON.stringify({ schema: 'velia-wake-word-setting/v1', enabled }), { encoding: 'utf8', flag: 'w' });
  await rm(path, { force: true });
  await rename(temporaryPath, path);
}

export function isPcmWav(value: Buffer): boolean {
  if (value.length < 44 || value.subarray(0, 4).toString('ascii') !== 'RIFF' ||
      value.subarray(8, 12).toString('ascii') !== 'WAVE') return false;
  let validFormat = false;
  let validData = false;
  let offset = 12;
  while (offset + 8 <= value.length) {
    const chunkId = value.subarray(offset, offset + 4).toString('ascii');
    const chunkSize = value.readUInt32LE(offset + 4);
    const dataOffset = offset + 8;
    const endOffset = dataOffset + chunkSize;
    if (endOffset > value.length) return false;
    if (chunkId === 'fmt ' && chunkSize >= 16) {
      const format = value.readUInt16LE(dataOffset);
      const channels = value.readUInt16LE(dataOffset + 2);
      const sampleRate = value.readUInt32LE(dataOffset + 4);
      const bitsPerSample = value.readUInt16LE(dataOffset + 14);
      validFormat = format === 1 && channels === 1 && sampleRate >= 8_000 && sampleRate <= 48_000 && bitsPerSample === 16;
    } else if (chunkId === 'data') {
      validData = chunkSize > 0;
    }
    offset = endOffset + (chunkSize % 2);
  }
  return validFormat && validData;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
