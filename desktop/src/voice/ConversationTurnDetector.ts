export type ConversationTurnStopReason = 'initialSilence' | 'endOfSpeech' | 'maximumDuration';

export interface ConversationTurnDetectorOptions {
  readonly initialSilenceMs?: number;
  readonly endSilenceMs?: number;
  readonly minimumSpeechMs?: number;
  readonly maximumDurationMs?: number;
  readonly minimumRms?: number;
}

/**
 * Bounded local VAD used only to decide when one ordinary long-conversation
 * turn ends. Audio remains local until the completed WAV is sent to ASR.
 */
export class ConversationTurnDetector {
  readonly #initialSilenceMs: number;
  readonly #endSilenceMs: number;
  readonly #minimumSpeechMs: number;
  readonly #maximumDurationMs: number;
  readonly #minimumRms: number;
  #totalFrames = 0;
  #voicedFrames = 0;
  #lastVoicedFrame = 0;
  #noiseFloor = 0.002;
  #speechDetected = false;
  #stopped = false;

  constructor(options: ConversationTurnDetectorOptions = {}) {
    this.#initialSilenceMs = bounded(options.initialSilenceMs, 12_000, 3_000, 30_000);
    this.#endSilenceMs = bounded(options.endSilenceMs, 950, 400, 3_000);
    this.#minimumSpeechMs = bounded(options.minimumSpeechMs, 180, 80, 2_000);
    this.#maximumDurationMs = bounded(options.maximumDurationMs, 20_000, 5_000, 60_000);
    this.#minimumRms = bounded(options.minimumRms, 0.012, 0.002, 0.2);
  }

  get speechDetected(): boolean { return this.#speechDetected; }

  accept(samples: Float32Array, sampleRate: number): ConversationTurnStopReason | undefined {
    if (this.#stopped || samples.length === 0) return undefined;
    if (!Number.isFinite(sampleRate) || sampleRate < 8_000) throw new Error('Audio sample rate is invalid.');

    const rms = rootMeanSquare(samples);
    const threshold = Math.max(this.#minimumRms, this.#noiseFloor * 3.2);
    const voiced = rms >= threshold;
    this.#totalFrames += samples.length;

    if (!this.#speechDetected && !voiced) {
      this.#noiseFloor = Math.min(this.#minimumRms, this.#noiseFloor * 0.97 + rms * 0.03);
    }
    if (voiced) {
      this.#voicedFrames += samples.length;
      this.#lastVoicedFrame = this.#totalFrames;
      if (framesToMs(this.#voicedFrames, sampleRate) >= this.#minimumSpeechMs) this.#speechDetected = true;
    }

    const elapsedMs = framesToMs(this.#totalFrames, sampleRate);
    let reason: ConversationTurnStopReason | undefined;
    if (elapsedMs >= this.#maximumDurationMs) reason = 'maximumDuration';
    else if (!this.#speechDetected && elapsedMs >= this.#initialSilenceMs) reason = 'initialSilence';
    else if (this.#speechDetected && framesToMs(this.#totalFrames - this.#lastVoicedFrame, sampleRate) >= this.#endSilenceMs) reason = 'endOfSpeech';
    if (reason) this.#stopped = true;
    return reason;
  }
}

function rootMeanSquare(samples: Float32Array): number {
  let sum = 0;
  for (const sample of samples) sum += sample * sample;
  return Math.sqrt(sum / samples.length);
}

function framesToMs(frames: number, sampleRate: number): number { return frames * 1_000 / sampleRate; }

function bounded(value: number | undefined, fallback: number, minimum: number, maximum: number): number {
  return typeof value === 'number' && Number.isFinite(value)
    ? Math.max(minimum, Math.min(maximum, value))
    : fallback;
}
