import type { EdgeVoiceId, TtsSetting, TtsSynthesisResult } from '@velia/protocol';
import { normalizeSpeechText } from './speechText';

export const liveResultAcknowledgementStorageKey = 'velia.live-result-acknowledgements.v1';
export const liveSuccessAcknowledgementText = '已完成。';
export const liveFailureAcknowledgementText = '没有完成，请查看提示。';

export interface LiveResultAcknowledgements {
  readonly schema: 'velia-live-result-acknowledgements/v1';
  readonly voiceId: EdgeVoiceId;
  readonly rate: number;
  readonly successAudioBase64?: string;
  readonly failureAudioBase64?: string;
}

type Synthesizer = (
  text: string,
  voiceId: EdgeVoiceId,
  rate: number,
) => Promise<TtsSynthesisResult>;

export class LiveResultAcknowledgementStore {
  readonly #storage: Storage;
  readonly #pending = new Map<string, Promise<string>>();
  readonly #dynamicAudio = new Map<string, string>();

  constructor(storage: Storage) {
    this.#storage = storage;
  }

  load(setting: TtsSetting): LiveResultAcknowledgements | undefined {
    try {
      const parsed: unknown = JSON.parse(this.#storage.getItem(liveResultAcknowledgementStorageKey) ?? 'null');
      if (!isRecord(parsed) || parsed.schema !== 'velia-live-result-acknowledgements/v1' ||
          parsed.voiceId !== setting.voiceId || parsed.rate !== setting.rate ||
          (parsed.successAudioBase64 !== undefined && !isValidAudio(parsed.successAudioBase64)) ||
          (parsed.failureAudioBase64 !== undefined && !isValidAudio(parsed.failureAudioBase64)) ||
          (parsed.successAudioBase64 === undefined && parsed.failureAudioBase64 === undefined)) return undefined;
      return parsed as unknown as LiveResultAcknowledgements;
    } catch {
      return undefined;
    }
  }

  prepare(setting: TtsSetting, resultOk: boolean, synthesize: Synthesizer): Promise<string> {
    const cached = this.load(setting);
    const cachedAudio = resultOk ? cached?.successAudioBase64 : cached?.failureAudioBase64;
    if (cachedAudio) return Promise.resolve(cachedAudio);
    const key = `${setting.voiceId}:${setting.rate}:${resultOk ? 'success' : 'failure'}`;
    const current = this.#pending.get(key);
    if (current) return current;
    const acknowledgementText = resultOk
      ? liveSuccessAcknowledgementText
      : liveFailureAcknowledgementText;
    const preparation = Promise.resolve()
      .then(() => synthesize(acknowledgementText, setting.voiceId, setting.rate))
      .then((generated) => {
        if (generated.mimeType !== 'audio/mpeg' || !isValidAudio(generated.audioBase64))
          throw new Error('Live result acknowledgement audio is invalid.');
        const latest = this.load(setting);
        const acknowledgements: LiveResultAcknowledgements = {
          ...(latest ?? {}),
          schema: 'velia-live-result-acknowledgements/v1',
          voiceId: setting.voiceId,
          rate: setting.rate,
          ...(resultOk
            ? { successAudioBase64: generated.audioBase64 }
            : { failureAudioBase64: generated.audioBase64 }),
        };
        try {
          this.#storage.setItem(liveResultAcknowledgementStorageKey, JSON.stringify(acknowledgements));
        } catch { }
        return generated.audioBase64;
      }).finally(() => {
      if (this.#pending.get(key) === preparation) this.#pending.delete(key);
    });
    this.#pending.set(key, preparation);
    return preparation;
  }

  prepareText(
    setting: TtsSetting,
    text: string,
    synthesize: Synthesizer,
  ): Promise<string> {
    const normalized = normalizeSpeechText(text);
    if (!normalized) return this.prepare(setting, false, synthesize);
    // Local results are short-lived and operation-specific. Keep a small
    // in-memory cache for repeated announcements in the same Live session;
    // never persist arbitrary document or screen text in localStorage.
    const bounded = normalized.length > 2_000 ? normalized.slice(0, 2_000) : normalized;
    const key = `${setting.voiceId}:${setting.rate}:${bounded}`;
    const cached = this.#dynamicAudio.get(key);
    if (cached) return Promise.resolve(cached);
    const current = this.#pending.get(key);
    if (current) return current;
    const preparation = Promise.resolve()
      .then(() => synthesize(bounded, setting.voiceId, setting.rate))
      .then((generated) => {
        if (generated.mimeType !== 'audio/mpeg' || !isValidAudio(generated.audioBase64))
          throw new Error('Live dynamic result audio is invalid.');
        if (this.#dynamicAudio.size >= 16) {
          const oldest = this.#dynamicAudio.keys().next().value;
          if (oldest) this.#dynamicAudio.delete(oldest);
        }
        this.#dynamicAudio.set(key, generated.audioBase64);
        return generated.audioBase64;
      }).finally(() => {
        if (this.#pending.get(key) === preparation) this.#pending.delete(key);
      });
    this.#pending.set(key, preparation);
    return preparation;
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isValidAudio(value: unknown): value is string {
  return typeof value === 'string' && value.length >= 16 && value.length <= 500_000;
}
