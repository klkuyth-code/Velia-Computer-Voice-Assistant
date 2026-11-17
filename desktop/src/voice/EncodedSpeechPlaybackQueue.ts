import { AdaptivePlaybackClock } from './PcmPlaybackQueue';

interface ScheduledSpeech {
  readonly done: Promise<boolean>;
}

/** Schedules decoded TTS chunks on one AudioContext so chunk boundaries do not create new players. */
export class EncodedSpeechPlaybackQueue {
  readonly #context: AudioContext;
  readonly #clock = new AdaptivePlaybackClock();
  readonly #sources = new Map<AudioBufferSourceNode, (completed: boolean) => void>();
  readonly #onPlaybackStateChange: (active: boolean) => void;
  #generation = 0;
  #closed = false;
  #active = false;

  get active(): boolean { return this.#active; }

  constructor(
    onPlaybackStateChange: (active: boolean) => void = () => undefined,
    context = new AudioContext({ latencyHint: 'interactive' }),
  ) {
    this.#onPlaybackStateChange = onPlaybackStateChange;
    this.#context = context;
  }

  async schedule(audioBase64: string): Promise<ScheduledSpeech> {
    if (this.#closed) throw new Error('Speech playback queue is closed.');
    const generation = this.#generation;
    const bytes = Uint8Array.from(atob(audioBase64), character => character.charCodeAt(0));
    if (bytes.length === 0) throw new Error('Synthesized speech is empty.');
    if (this.#context.state === 'suspended') await this.#context.resume();
    const buffer = await this.#context.decodeAudioData(bytes.buffer);
    if (this.#closed || generation !== this.#generation) return { done: Promise.resolve(false) };

    const source = this.#context.createBufferSource();
    source.buffer = buffer;
    source.connect(this.#context.destination);
    let settle!: (completed: boolean) => void;
    const done = new Promise<boolean>((resolve) => { settle = resolve; });
    this.#sources.set(source, settle);
    this.#setActive(true);
    source.onended = () => this.#finish(source, generation === this.#generation);
    source.start(this.#clock.schedule(this.#context.currentTime, buffer.duration));
    return { done };
  }

  completeResponse(): void { this.#clock.completeResponse(); }

  clear(): void {
    this.#generation += 1;
    for (const [source, settle] of this.#sources) {
      source.onended = null;
      try { source.stop(); } catch { }
      source.disconnect();
      settle(false);
    }
    this.#sources.clear();
    this.#clock.clear();
    this.#setActive(false);
  }

  async close(): Promise<void> {
    if (this.#closed) return;
    this.#closed = true;
    this.clear();
    await this.#context.close();
  }

  #finish(source: AudioBufferSourceNode, completed: boolean): void {
    const settle = this.#sources.get(source);
    if (!settle) return;
    this.#sources.delete(source);
    source.onended = null;
    source.disconnect();
    settle(completed);
    if (this.#sources.size === 0) this.#setActive(false);
  }

  #setActive(active: boolean): void {
    if (active === this.#active) return;
    this.#active = active;
    try { this.#onPlaybackStateChange(active); } catch { }
  }
}
