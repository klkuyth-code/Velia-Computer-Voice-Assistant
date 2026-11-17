import { decodePcm16Base64, LIVE_OUTPUT_SAMPLE_RATE } from './LiveAudioCodec';

interface StoppableAudioSource { stop(): void; disconnect(): void; }

export interface PlaybackClockOptions {
  readonly initialLeadSeconds: number;
  readonly minimumLeadSeconds: number;
  readonly maximumLeadSeconds: number;
  readonly rebufferSeconds: number;
  readonly leadAdjustmentSeconds: number;
  readonly cleanResponseReductionSeconds: number;
  readonly rebufferAtTargetLead: boolean;
}

export const DEFAULT_PLAYBACK_CLOCK_OPTIONS: PlaybackClockOptions = {
  initialLeadSeconds: 0.180,
  minimumLeadSeconds: 0.140,
  maximumLeadSeconds: 0.350,
  rebufferSeconds: 0.080,
  leadAdjustmentSeconds: 0.040,
  cleanResponseReductionSeconds: 0.005,
  rebufferAtTargetLead: false,
};

/** A deeper jitter buffer for Gemini Live traffic routed through a local proxy. */
export const LIVE_PLAYBACK_CLOCK_OPTIONS: PlaybackClockOptions = {
  initialLeadSeconds: 0.700,
  minimumLeadSeconds: 0.550,
  maximumLeadSeconds: 1.100,
  rebufferSeconds: 0.240,
  leadAdjustmentSeconds: 0.100,
  cleanResponseReductionSeconds: 0.025,
  rebufferAtTargetLead: false,
};

export class AdaptivePlaybackClock {
  #nextStart = 0;
  #targetLead: number;
  #responseActive = false;
  #hadUnderrun = false;
  readonly #options: PlaybackClockOptions;

  constructor(options: PlaybackClockOptions = DEFAULT_PLAYBACK_CLOCK_OPTIONS) {
    validateOptions(options);
    this.#options = options;
    this.#targetLead = options.initialLeadSeconds;
  }

  get targetLeadSeconds(): number { return this.#targetLead; }

  schedule(now: number, duration: number): number {
    if (!Number.isFinite(now) || now < 0 || !Number.isFinite(duration) || duration <= 0) {
      throw new RangeError('Playback timing is invalid.');
    }

    let start: number;
    if (!this.#responseActive) {
      this.#responseActive = true;
      this.#hadUnderrun = false;
      start = now + this.#targetLead;
    } else if (this.#nextStart <= now) {
      this.#hadUnderrun = true;
      this.#targetLead = Math.min(
        this.#options.maximumLeadSeconds,
        this.#targetLead + this.#options.leadAdjustmentSeconds,
      );
      start = now + (this.#options.rebufferAtTargetLead
        ? Math.max(this.#options.rebufferSeconds, this.#targetLead)
        : this.#options.rebufferSeconds);
    } else {
      start = this.#nextStart;
    }
    this.#nextStart = start + duration;
    return start;
  }

  completeResponse(): void {
    if (this.#responseActive && !this.#hadUnderrun) {
      this.#targetLead = Math.max(
        this.#options.minimumLeadSeconds,
        this.#targetLead - this.#options.cleanResponseReductionSeconds,
      );
    }
    this.#responseActive = false;
    this.#hadUnderrun = false;
    this.#nextStart = 0;
  }

  clear(): void {
    this.#responseActive = false;
    this.#hadUnderrun = false;
    this.#nextStart = 0;
  }
}

export class PlaybackSourceRegistry {
  readonly #sources = new Set<StoppableAudioSource>();
  add(source: StoppableAudioSource): void { this.#sources.add(source); }
  remove(source: StoppableAudioSource): void { this.#sources.delete(source); }
  clear(): void {
    for (const source of this.#sources) {
      try { source.stop(); } catch { }
      source.disconnect();
    }
    this.#sources.clear();
  }
  get size(): number { return this.#sources.size; }
}

export class PcmPlaybackQueue {
  readonly #context: AudioContext;
  readonly #sources = new PlaybackSourceRegistry();
  readonly #clock = new AdaptivePlaybackClock(LIVE_PLAYBACK_CLOCK_OPTIONS);
  readonly #onPlaybackStateChange: (active: boolean) => void;
  #tail: Promise<void> = Promise.resolve();
  #generation = 0;
  #closed = false;
  #active = false;
  #activationTimer: ReturnType<typeof setTimeout> | undefined;
  #activationAt = Number.POSITIVE_INFINITY;

  get active(): boolean { return this.#active; }

  constructor(
    onPlaybackStateChange: (active: boolean) => void = () => undefined,
    context = new AudioContext({ sampleRate: LIVE_OUTPUT_SAMPLE_RATE, latencyHint: 0.04 }),
  ) {
    this.#onPlaybackStateChange = onPlaybackStateChange;
    this.#context = context;
  }

  enqueue(base64: string): Promise<void> {
    if (this.#closed) return Promise.reject(new Error('Playback queue is closed.'));
    const generation = this.#generation;
    const scheduled = this.#tail.then(() => this.#enqueueNow(base64, generation));
    this.#tail = scheduled.catch(() => undefined);
    return scheduled;
  }

  async #enqueueNow(base64: string, generation: number): Promise<void> {
    if (this.#closed || generation !== this.#generation) return;
    const samples = decodePcm16Base64(base64);
    if (this.#context.state === 'suspended') await this.#context.resume();
    if (this.#closed || generation !== this.#generation) return;
    const buffer = this.#context.createBuffer(1, samples.length, LIVE_OUTPUT_SAMPLE_RATE);
    buffer.copyToChannel(samples, 0);
    const source = this.#context.createBufferSource();
    source.buffer = buffer;
    source.connect(this.#context.destination);
    this.#sources.add(source);
    source.onended = () => {
      source.disconnect();
      this.#sources.remove(source);
      if (this.#sources.size === 0) {
        this.#cancelPendingActivation();
        this.#setActive(false);
      }
    };
    const start = this.#clock.schedule(this.#context.currentTime, buffer.duration);
    this.#activateWhenPlaybackStarts(start, generation);
    source.start(start);
  }

  completeResponse(): void { this.#clock.completeResponse(); }

  clear(): void {
    this.#generation += 1;
    this.#cancelPendingActivation();
    this.#sources.clear();
    this.#clock.clear();
    this.#setActive(false);
  }

  async close(): Promise<void> {
    if (this.#closed) return;
    this.#closed = true;
    this.clear();
    await this.#tail;
    await this.#context.close();
  }

  #setActive(active: boolean): void {
    if (this.#active === active) return;
    this.#active = active;
    try { this.#onPlaybackStateChange(active); } catch { }
  }

  #activateWhenPlaybackStarts(start: number, generation: number): void {
    if (this.#active || start >= this.#activationAt) return;
    this.#cancelPendingActivation();
    this.#activationAt = start;
    const delayMs = Math.max(0, (start - this.#context.currentTime) * 1_000);
    this.#activationTimer = setTimeout(() => {
      this.#activationTimer = undefined;
      this.#activationAt = Number.POSITIVE_INFINITY;
      if (!this.#closed && generation === this.#generation && this.#sources.size > 0)
        this.#setActive(true);
    }, delayMs);
  }

  #cancelPendingActivation(): void {
    if (this.#activationTimer !== undefined) clearTimeout(this.#activationTimer);
    this.#activationTimer = undefined;
    this.#activationAt = Number.POSITIVE_INFINITY;
  }
}

function validateOptions(options: PlaybackClockOptions): void {
  const values = [
    options.initialLeadSeconds,
    options.minimumLeadSeconds,
    options.maximumLeadSeconds,
    options.rebufferSeconds,
    options.leadAdjustmentSeconds,
    options.cleanResponseReductionSeconds,
  ];
  if (values.some(value => !Number.isFinite(value) || value <= 0) ||
      options.minimumLeadSeconds > options.initialLeadSeconds ||
      options.initialLeadSeconds > options.maximumLeadSeconds ||
      options.rebufferSeconds > options.maximumLeadSeconds) {
    throw new RangeError('Playback clock options are invalid.');
  }
}
