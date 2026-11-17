export const LIVE_INPUT_SAMPLE_RATE = 16_000;
export const LIVE_OUTPUT_SAMPLE_RATE = 24_000;
export const LIVE_CHUNK_SAMPLES = 640;

export class StreamingLinearResampler {
  readonly #ratio: number;
  #input: number[] = [];
  #position = 0;

  constructor(sourceSampleRate: number, targetSampleRate = LIVE_INPUT_SAMPLE_RATE) {
    if (!Number.isFinite(sourceSampleRate) || !Number.isFinite(targetSampleRate) ||
        sourceSampleRate < targetSampleRate || targetSampleRate < 8_000) {
      throw new Error('不支持当前麦克风采样率。');
    }
    this.#ratio = sourceSampleRate / targetSampleRate;
  }

  process(chunk: Float32Array): Float32Array {
    for (const sample of chunk) this.#input.push(sample);
    const output: number[] = [];
    while (this.#position + 1 < this.#input.length) {
      const left = Math.floor(this.#position);
      const fraction = this.#position - left;
      const a = this.#input[left] ?? 0;
      const b = this.#input[left + 1] ?? a;
      output.push(a + (b - a) * fraction);
      this.#position += this.#ratio;
    }
    // Preserve one sample so interpolation remains continuous across worklet messages.
    const consumed = Math.min(Math.floor(this.#position), Math.max(0, this.#input.length - 1));
    if (consumed > 0) {
      this.#input.splice(0, consumed);
      this.#position -= consumed;
    }
    return Float32Array.from(output);
  }
}

export class Pcm16Chunker {
  readonly #samplesPerChunk: number;
  #pending: number[] = [];

  constructor(samplesPerChunk = LIVE_CHUNK_SAMPLES) {
    if (!Number.isInteger(samplesPerChunk) || samplesPerChunk <= 0) throw new Error('Invalid Live chunk size.');
    this.#samplesPerChunk = samplesPerChunk;
  }

  append(samples: Float32Array): Uint8Array[] {
    for (const sample of samples) this.#pending.push(sample);
    const chunks: Uint8Array[] = [];
    while (this.#pending.length >= this.#samplesPerChunk) {
      const output = new Uint8Array(this.#samplesPerChunk * 2);
      const view = new DataView(output.buffer);
      for (let index = 0; index < this.#samplesPerChunk; index += 1) {
        const value = Math.max(-1, Math.min(1, this.#pending[index] ?? 0));
        view.setInt16(index * 2, value < 0 ? Math.round(value * 0x8000) : Math.round(value * 0x7fff), true);
      }
      this.#pending.splice(0, this.#samplesPerChunk);
      chunks.push(output);
    }
    return chunks;
  }

  clear(): void { this.#pending = []; }
}

export function decodePcm16Base64(base64: string): Float32Array {
  const binary = atob(base64);
  if (binary.length === 0 || binary.length % 2 !== 0 || binary.length > 384_000) {
    throw new Error('Gemini Live 返回了无效音频。');
  }
  const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
  const view = new DataView(bytes.buffer);
  const output = new Float32Array(bytes.length / 2);
  for (let index = 0; index < output.length; index += 1) {
    const value = view.getInt16(index * 2, true);
    output[index] = value < 0 ? value / 0x8000 : value / 0x7fff;
  }
  return output;
}
