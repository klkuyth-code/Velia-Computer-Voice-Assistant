import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { EdgeTTS } from 'node-edge-tts';
import { edgeVoiceIds, type EdgeVoiceId, type TtsSynthesisResult } from '@velia/protocol';

const maximumTextLength = 4_000;
const maximumAudioBytes = 12 * 1024 * 1024;
const maximumAttempts = 3;
const retryDelaysMs = [100, 250] as const;
const outputFormat = 'audio-24khz-48kbitrate-mono-mp3';

type RetryDelay = (delayMs: number) => Promise<void>;

function wait(delayMs: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, delayMs));
}

export class EdgeTtsService {
  constructor(private readonly retryDelay: RetryDelay = wait) {}

  async synthesize(text: string, voiceId: EdgeVoiceId, rate: number): Promise<TtsSynthesisResult> {
    const normalizedText = text.trim();
    if (normalizedText.length === 0 || normalizedText.length > maximumTextLength) {
      throw new TypeError('TTS text is invalid.');
    }
    if (!edgeVoiceIds.includes(voiceId) || !Number.isInteger(rate) || rate < -50 || rate > 50 || rate % 5 !== 0) {
      throw new TypeError('TTS voice settings are invalid.');
    }

    const temporaryDirectory = await mkdtemp(join(tmpdir(), 'velia-tts-'));
    const audioPath = join(temporaryDirectory, 'speech.mp3');
    try {
      const settings = {
        voice: voiceId,
        lang: voiceId.startsWith('zh-HK-') ? 'zh-HK' : voiceId.startsWith('zh-TW-') ? 'zh-TW' : 'zh-CN',
        outputFormat,
        rate: `${rate >= 0 ? '+' : ''}${rate}%`,
        timeout: 15_000,
      };

      let lastError: unknown;
      for (let attempt = 1; attempt <= maximumAttempts; attempt += 1) {
        try {
          // A failed request may leave a partial MP3 behind. Never let a later
          // attempt accidentally accept bytes produced by an earlier attempt.
          await rm(audioPath, { force: true });
          const tts = new EdgeTTS(settings);
          await tts.ttsPromise(normalizedText, audioPath);
          const audio = await readFile(audioPath);
          if (audio.length === 0 || audio.length > maximumAudioBytes || !isMp3Audio(audio)) {
            throw new Error('Edge TTS returned invalid audio.');
          }
          return { audioBase64: audio.toString('base64'), mimeType: 'audio/mpeg', provider: 'edgeOnline' };
        } catch (error: unknown) {
          lastError = error;
          if (attempt < maximumAttempts) {
            await this.retryDelay(retryDelaysMs[attempt - 1]!);
          }
        }
      }

      throw new Error(`Edge TTS synthesis failed after ${maximumAttempts} attempts.`, { cause: lastError });
    } finally {
      await rm(temporaryDirectory, { recursive: true, force: true });
    }
  }
}

export function isMp3Audio(audio: Uint8Array): boolean {
  if (audio.length < 3) return false;
  if (audio[0] === 0x49 && audio[1] === 0x44 && audio[2] === 0x33) return true;
  return audio[0] === 0xff && (audio[1]! & 0xe0) === 0xe0;
}
