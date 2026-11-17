const maximumSpeechTextLength = 4_000;
export const maximumSpeechChunkLength = 220;
const preferredBoundaries = new Set(['。', '！', '？', '!', '?', '；', ';', '，', ',', '、', '：', ':']);

export function normalizeSpeechText(text: string): string {
  return text
    .replace(/https?:\/\/\S+/giu, '链接')
    .replace(/[*_`#>\[\]()]/gu, '')
    .replace(/\s+/gu, ' ')
    .trim()
    .slice(0, maximumSpeechTextLength);
}

export function splitSpeechText(text: string, maximumLength = maximumSpeechChunkLength): readonly string[] {
  if (!Number.isInteger(maximumLength) || maximumLength < 40 || maximumLength > maximumSpeechTextLength) {
    throw new TypeError('Speech chunk length is invalid.');
  }
  let remaining = normalizeSpeechText(text);
  const chunks: string[] = [];
  while (remaining.length > maximumLength) {
    const window = remaining.slice(0, maximumLength);
    let splitAt = -1;
    for (let index = window.length - 1; index >= Math.floor(maximumLength * 0.45); index -= 1) {
      if (preferredBoundaries.has(window[index]!)) {
        splitAt = index + 1;
        break;
      }
    }
    if (splitAt < 0) splitAt = maximumLength;
    const chunk = remaining.slice(0, splitAt).trim();
    if (chunk) chunks.push(chunk);
    remaining = remaining.slice(splitAt).trim();
  }
  if (remaining) chunks.push(remaining);
  return chunks;
}

const streamingSentenceBoundaries = new Set(['。', '！', '？', '!', '?', '；', ';', '\n']);

/**
 * Buffers model deltas until a natural sentence boundary is available. Long
 * punctuation-free output is still released with a bounded delay, while the
 * final flush reuses the normal speech chunking rules.
 */
export class IncrementalSpeechTextBuffer {
  #pending = '';

  push(delta: string): readonly string[] {
    if (delta) this.#pending += delta;
    return this.#take(false);
  }

  flush(): readonly string[] { return this.#take(true); }

  clear(): void { this.#pending = ''; }

  #take(force: boolean): readonly string[] {
    const ready: string[] = [];
    while (this.#pending) {
      const boundary = findSentenceBoundary(this.#pending);
      if (boundary >= 0) {
        const raw = this.#pending.slice(0, boundary + 1);
        this.#pending = this.#pending.slice(boundary + 1);
        const normalized = normalizeSpeechText(raw);
        if (normalized) ready.push(normalized);
        continue;
      }
      if (this.#pending.length >= 72) {
        const splitAt = findStreamingLengthBoundary(this.#pending, 72);
        const raw = this.#pending.slice(0, splitAt);
        this.#pending = this.#pending.slice(splitAt);
        const normalized = normalizeSpeechText(raw);
        if (normalized) ready.push(normalized);
        continue;
      }
      break;
    }
    if (force && this.#pending) {
      ready.push(...splitSpeechText(this.#pending));
      this.#pending = '';
    }
    return ready;
  }
}

function findSentenceBoundary(text: string): number {
  for (let index = 0; index < text.length; index += 1) {
    if (streamingSentenceBoundaries.has(text[index]!)) return index;
  }
  return -1;
}

function findStreamingLengthBoundary(text: string, maximum: number): number {
  const window = text.slice(0, maximum);
  for (let index = window.length - 1; index >= Math.floor(maximum * 0.55); index -= 1) {
    if (preferredBoundaries.has(window[index]!) || /\s/u.test(window[index]!)) return index + 1;
  }
  return maximum;
}
