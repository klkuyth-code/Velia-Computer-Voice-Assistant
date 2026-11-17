import { writeFile } from 'node:fs/promises';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { EdgeTtsService, isMp3Audio } from '../electron/voice/EdgeTtsService.js';
import { edgeVoices } from '../src/voice/edgeVoices.js';

const edgeTtsMock = vi.hoisted(() => ({
  calls: [] as Array<{ text: string; audioPath: string }>,
  constructorSettings: [] as Array<Record<string, unknown>>,
  attempts: [] as Array<(text: string, audioPath: string) => Promise<unknown>>,
}));

vi.mock('node-edge-tts', () => ({
  EdgeTTS: class {
    constructor(settings: Record<string, unknown>) {
      edgeTtsMock.constructorSettings.push({ ...settings });
    }

    ttsPromise(text: string, audioPath: string): Promise<unknown> {
      edgeTtsMock.calls.push({ text, audioPath });
      const attempt = edgeTtsMock.attempts.shift();
      if (!attempt) return Promise.reject(new Error('Unexpected Edge TTS call.'));
      return attempt(text, audioPath);
    }
  },
}));

const validMp3 = Uint8Array.of(0x49, 0x44, 0x33, 0x04);

async function writeValidMp3(_text: string, audioPath: string): Promise<void> {
  await writeFile(audioPath, validMp3);
}

describe('Edge TTS policy', () => {
  beforeEach(() => {
    edgeTtsMock.calls.length = 0;
    edgeTtsMock.constructorSettings.length = 0;
    edgeTtsMock.attempts.length = 0;
  });

  it('exposes exactly the nine allowlisted Chinese voices', () => {
    expect(edgeVoices).toHaveLength(9);
    expect(new Set(edgeVoices.map((voice) => voice.id)).size).toBe(9);
    expect(edgeVoices.map((voice) => voice.group)).toEqual([
      'female', 'female', 'male', 'male', 'male', 'dialect', 'dialect', 'dialect', 'dialect',
    ]);
  });

  it('rejects arbitrary voices and unbounded rates before network access', async () => {
    const service = new EdgeTtsService();
    await expect(service.synthesize('测试', 'shell' as never, 20)).rejects.toThrow('invalid');
    await expect(service.synthesize('测试', 'zh-CN-XiaoxiaoNeural', 55)).rejects.toThrow('invalid');
    await expect(service.synthesize('', 'zh-CN-XiaoxiaoNeural', 20)).rejects.toThrow('invalid');
  });

  it('accepts tagged and raw-frame MP3 headers only', () => {
    expect(isMp3Audio(Uint8Array.of(0x49, 0x44, 0x33))).toBe(true);
    expect(isMp3Audio(Uint8Array.of(0xff, 0xf3, 0x64))).toBe(true);
    expect(isMp3Audio(Uint8Array.of(0x52, 0x49, 0x46, 0x46))).toBe(false);
    expect(isMp3Audio(Uint8Array.of(0xff))).toBe(false);
  });

  it('retries one failed chunk with the same voice settings and then succeeds', async () => {
    edgeTtsMock.attempts.push(
      async () => { throw new Error('Temporary network failure.'); },
      writeValidMp3,
    );
    const retryDelay = vi.fn(async () => undefined);

    const result = await new EdgeTtsService(retryDelay).synthesize(' 第一段 ', 'zh-CN-XiaoxiaoNeural', 20);

    expect(result).toEqual({
      audioBase64: Buffer.from(validMp3).toString('base64'),
      mimeType: 'audio/mpeg',
      provider: 'edgeOnline',
    });
    expect(edgeTtsMock.calls.map((call) => call.text)).toEqual(['第一段', '第一段']);
    expect(new Set(edgeTtsMock.calls.map((call) => call.audioPath)).size).toBe(1);
    expect(retryDelay).toHaveBeenCalledTimes(1);
    expect(retryDelay).toHaveBeenCalledWith(100);
    expect(edgeTtsMock.constructorSettings).toEqual([
      {
        voice: 'zh-CN-XiaoxiaoNeural',
        lang: 'zh-CN',
        outputFormat: 'audio-24khz-48kbitrate-mono-mp3',
        rate: '+20%',
        timeout: 15_000,
      },
      {
        voice: 'zh-CN-XiaoxiaoNeural',
        lang: 'zh-CN',
        outputFormat: 'audio-24khz-48kbitrate-mono-mp3',
        rate: '+20%',
        timeout: 15_000,
      },
    ]);
  });

  it('throws to the caller after three failed online attempts', async () => {
    edgeTtsMock.attempts.push(
      async () => { throw new Error('Failure 1.'); },
      async () => { throw new Error('Failure 2.'); },
      async () => { throw new Error('Failure 3.'); },
    );
    const retryDelay = vi.fn(async () => undefined);

    let failure: unknown;
    try {
      await new EdgeTtsService(retryDelay).synthesize('失败分片', 'zh-CN-XiaoxiaoNeural', 0);
    } catch (error: unknown) {
      failure = error;
    }

    expect(failure).toBeInstanceOf(Error);
    expect(failure).toMatchObject({
      message: 'Edge TTS synthesis failed after 3 attempts.',
      cause: { message: 'Failure 3.' },
    });
    expect(edgeTtsMock.calls.map((call) => call.text)).toEqual(['失败分片', '失败分片', '失败分片']);
    expect(retryDelay).toHaveBeenNthCalledWith(1, 100);
    expect(retryDelay).toHaveBeenNthCalledWith(2, 250);
  });

  it('finishes a chunk retry before synthesizing the next chunk', async () => {
    edgeTtsMock.attempts.push(
      async () => { throw new Error('Retry the first chunk.'); },
      writeValidMp3,
      writeValidMp3,
    );
    const service = new EdgeTtsService(async () => undefined);

    await service.synthesize('第一段', 'zh-CN-XiaoxiaoNeural', 20);
    await service.synthesize('第二段', 'zh-CN-XiaoyiNeural', -10);

    expect(edgeTtsMock.calls.map((call) => call.text)).toEqual(['第一段', '第一段', '第二段']);
    expect(edgeTtsMock.constructorSettings.map(({ voice, rate, outputFormat }) => ({ voice, rate, outputFormat }))).toEqual([
      { voice: 'zh-CN-XiaoxiaoNeural', rate: '+20%', outputFormat: 'audio-24khz-48kbitrate-mono-mp3' },
      { voice: 'zh-CN-XiaoxiaoNeural', rate: '+20%', outputFormat: 'audio-24khz-48kbitrate-mono-mp3' },
      { voice: 'zh-CN-XiaoyiNeural', rate: '-10%', outputFormat: 'audio-24khz-48kbitrate-mono-mp3' },
    ]);
  });
});
