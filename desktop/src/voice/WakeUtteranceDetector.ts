export interface WakeUtteranceDetectorOptions {
  readonly preRollMs?: number;
  readonly speechStartMs?: number;
  readonly endSilenceMs?: number;
  readonly minimumSpeechMs?: number;
  readonly maximumUtteranceMs?: number;
  readonly minimumRms?: number;
}

export interface WakeUtterance {
  readonly chunks: readonly Float32Array[];
  readonly sampleRate: number;
  readonly durationMs: number;
}

/**
 * A small, fully local energy gate. It only decides which short utterances are
 * worth sending to the bundled local Whisper process; it never transcribes or
 * uploads audio itself.
 */
export class WakeUtteranceDetector {
  readonly #preRollMs: number;
  readonly #speechStartMs: number;
  readonly #endSilenceMs: number;
  readonly #minimumSpeechMs: number;
  readonly #maximumUtteranceMs: number;
  readonly #minimumRms: number;
  #sampleRate = 0;
  #noiseFloor = 0.002;
  #preRoll: Float32Array[] = [];
  #preRollFrames = 0;
  #candidateVoicedFrames = 0;
  #capturing = false;
  #captured: Float32Array[] = [];
  #capturedFrames = 0;
  #voicedFrames = 0;
  #lastVoicedFrame = 0;

  constructor(options: WakeUtteranceDetectorOptions = {}) {
    this.#preRollMs = bounded(options.preRollMs, 450, 100, 1_000);
    this.#speechStartMs = bounded(options.speechStartMs, 80, 20, 500);
    this.#endSilenceMs = bounded(options.endSilenceMs, 850, 300, 2_500);
    this.#minimumSpeechMs = bounded(options.minimumSpeechMs, 240, 80, 1_500);
    this.#maximumUtteranceMs = bounded(options.maximumUtteranceMs, 4_500, 1_500, 20_000);
    this.#minimumRms = bounded(options.minimumRms, 0.012, 0.002, 0.2);
  }

  get speechActive(): boolean { return this.#capturing; }

  accept(samples: Float32Array, sampleRate: number): WakeUtterance | undefined {
    if (samples.length === 0) return undefined;
    if (!Number.isFinite(sampleRate) || sampleRate < 16_000) throw new Error('Audio sample rate is invalid.');
    if (this.#sampleRate !== 0 && this.#sampleRate !== sampleRate) this.reset();
    this.#sampleRate = sampleRate;

    const rms = rootMeanSquare(samples);
    const threshold = Math.max(this.#minimumRms, this.#noiseFloor * 3.2);
    const voiced = rms >= threshold;
    if (!this.#capturing && !voiced) {
      this.#noiseFloor = Math.min(this.#minimumRms, this.#noiseFloor * 0.97 + rms * 0.03);
    }

    if (!this.#capturing) {
      this.#appendPreRoll(samples);
      this.#candidateVoicedFrames = voiced ? this.#candidateVoicedFrames + samples.length : 0;
      if (framesToMs(this.#candidateVoicedFrames, sampleRate) < this.#speechStartMs) return undefined;
      this.#capturing = true;
      this.#captured = this.#preRoll;
      this.#capturedFrames = this.#preRollFrames;
      this.#voicedFrames = this.#candidateVoicedFrames;
      this.#lastVoicedFrame = this.#capturedFrames;
      this.#preRoll = [];
      this.#preRollFrames = 0;
      return undefined;
    }

    this.#captured.push(samples);
    this.#capturedFrames += samples.length;
    if (voiced) {
      this.#voicedFrames += samples.length;
      this.#lastVoicedFrame = this.#capturedFrames;
    }

    const maximumReached = framesToMs(this.#capturedFrames, sampleRate) >= this.#maximumUtteranceMs;
    const trailingSilenceReached = framesToMs(this.#capturedFrames - this.#lastVoicedFrame, sampleRate) >= this.#endSilenceMs;
    if (!maximumReached && !trailingSilenceReached) return undefined;

    const enoughSpeech = framesToMs(this.#voicedFrames, sampleRate) >= this.#minimumSpeechMs;
    const chunks = this.#captured;
    const durationMs = Math.round(framesToMs(this.#capturedFrames, sampleRate));
    this.#resetPhrase();
    return enoughSpeech ? { chunks, sampleRate, durationMs } : undefined;
  }

  reset(): void {
    this.#sampleRate = 0;
    this.#noiseFloor = 0.002;
    this.#resetPhrase();
  }

  #appendPreRoll(samples: Float32Array): void {
    this.#preRoll.push(samples);
    this.#preRollFrames += samples.length;
    const maximumFrames = Math.round(this.#sampleRate * this.#preRollMs / 1_000);
    while (this.#preRollFrames > maximumFrames && this.#preRoll.length > 1) {
      const first = this.#preRoll.shift();
      this.#preRollFrames -= first?.length ?? 0;
    }
  }

  #resetPhrase(): void {
    this.#preRoll = [];
    this.#preRollFrames = 0;
    this.#candidateVoicedFrames = 0;
    this.#capturing = false;
    this.#captured = [];
    this.#capturedFrames = 0;
    this.#voicedFrames = 0;
    this.#lastVoicedFrame = 0;
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
