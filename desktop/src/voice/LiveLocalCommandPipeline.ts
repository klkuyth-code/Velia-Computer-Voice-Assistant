export interface LiveLocalUtterance {
  readonly wavBase64: string;
  readonly utteranceId: string;
  readonly capturedAtUtc: string;
  readonly durationMs: number;
}

export type LiveLocalCommandPipelineFailure = 'transcribe' | 'submit';

export interface LiveLocalCommandPipelineOptions<TResult> {
  readonly transcribe: (utterance: LiveLocalUtterance) => Promise<string>;
  readonly submit: (transcript: string, utterance: LiveLocalUtterance) => Promise<TResult>;
  readonly interceptTranscript?: (transcript: string, utterance: LiveLocalUtterance) => boolean | Promise<boolean>;
  readonly onSubmitted?: (transcript: string, result: TResult) => void;
  readonly onFailure?: (failure: LiveLocalCommandPipelineFailure) => void;
  readonly maximumPending?: number;
  readonly transcriptionConcurrency?: number;
}

interface PendingUtterance {
  readonly sequence: number;
  readonly utterance: LiveLocalUtterance;
  state: 'queued' | 'transcribing' | 'ready';
  transcript?: string;
}

/**
 * Transcribes several captured utterances concurrently, then submits them to
 * Core in capture order. A slow first ASR response can no longer let a later
 * command overtake it, and one failed transcription cannot block the queue.
 */
export class LiveLocalCommandPipeline<TResult> {
  readonly #transcribe: LiveLocalCommandPipelineOptions<TResult>['transcribe'];
  readonly #submit: LiveLocalCommandPipelineOptions<TResult>['submit'];
  readonly #interceptTranscript: LiveLocalCommandPipelineOptions<TResult>['interceptTranscript'];
  readonly #onSubmitted: LiveLocalCommandPipelineOptions<TResult>['onSubmitted'];
  readonly #onFailure: LiveLocalCommandPipelineOptions<TResult>['onFailure'];
  readonly #maximumPending: number;
  readonly #transcriptionConcurrency: number;
  readonly #entries = new Map<number, PendingUtterance>();
  readonly #transcriptionQueue: PendingUtterance[] = [];
  #nextCaptureSequence = 0;
  #nextSubmitSequence = 0;
  #activeTranscriptions = 0;
  #generation = 0;
  #flushing = false;
  #stopped = false;

  constructor(options: LiveLocalCommandPipelineOptions<TResult>) {
    this.#transcribe = options.transcribe;
    this.#submit = options.submit;
    this.#interceptTranscript = options.interceptTranscript;
    this.#onSubmitted = options.onSubmitted;
    this.#onFailure = options.onFailure;
    this.#maximumPending = boundedInteger(options.maximumPending, 12, 1, 64);
    this.#transcriptionConcurrency = boundedInteger(options.transcriptionConcurrency, 3, 1, 8);
  }

  get pendingCount(): number { return this.#entries.size; }

  enqueue(utterance: LiveLocalUtterance): boolean {
    if (this.#stopped || this.#entries.size >= this.#maximumPending) return false;
    const entry: PendingUtterance = {
      sequence: this.#nextCaptureSequence,
      utterance,
      state: 'queued',
    };
    this.#nextCaptureSequence += 1;
    this.#entries.set(entry.sequence, entry);
    this.#transcriptionQueue.push(entry);
    this.#pumpTranscriptions(this.#generation);
    return true;
  }

  stop(): void {
    if (this.#stopped) return;
    this.#stopped = true;
    this.#generation += 1;
    this.#entries.clear();
    this.#transcriptionQueue.length = 0;
  }

  #pumpTranscriptions(generation: number): void {
    while (!this.#stopped && generation === this.#generation &&
      this.#activeTranscriptions < this.#transcriptionConcurrency) {
      const entry = this.#transcriptionQueue.shift();
      if (!entry) return;
      entry.state = 'transcribing';
      this.#activeTranscriptions += 1;
      void this.#transcribe(entry.utterance).then((transcript) => {
        if (this.#stopped || generation !== this.#generation) return;
        entry.transcript = transcript.trim();
      }).catch(() => {
        if (this.#stopped || generation !== this.#generation) return;
        entry.transcript = '';
        this.#onFailure?.('transcribe');
      }).finally(() => {
        this.#activeTranscriptions = Math.max(0, this.#activeTranscriptions - 1);
        if (this.#stopped || generation !== this.#generation) return;
        entry.state = 'ready';
        void this.#flushReadyEntries(generation);
        this.#pumpTranscriptions(generation);
      });
    }
  }

  async #flushReadyEntries(generation: number): Promise<void> {
    if (this.#flushing || this.#stopped || generation !== this.#generation) return;
    this.#flushing = true;
    try {
      while (!this.#stopped && generation === this.#generation) {
        const entry = this.#entries.get(this.#nextSubmitSequence);
        if (!entry || entry.state !== 'ready') break;
        const transcript = entry.transcript ?? '';
        if (transcript) {
          try {
            const intercepted = await this.#interceptTranscript?.(transcript, entry.utterance) ?? false;
            if (this.#stopped || generation !== this.#generation) return;
            if (!intercepted) {
              const result = await this.#submit(transcript, entry.utterance);
              if (this.#stopped || generation !== this.#generation) return;
              this.#onSubmitted?.(transcript, result);
            }
          } catch {
            if (!this.#stopped && generation === this.#generation) this.#onFailure?.('submit');
          }
        }
        this.#entries.delete(entry.sequence);
        this.#nextSubmitSequence += 1;
      }
    } finally {
      this.#flushing = false;
      const next = this.#entries.get(this.#nextSubmitSequence);
      if (!this.#stopped && generation === this.#generation && next?.state === 'ready')
        void this.#flushReadyEntries(generation);
    }
  }
}

function boundedInteger(value: number | undefined, fallback: number, minimum: number, maximum: number): number {
  return typeof value === 'number' && Number.isInteger(value)
    ? Math.max(minimum, Math.min(maximum, value))
    : fallback;
}
