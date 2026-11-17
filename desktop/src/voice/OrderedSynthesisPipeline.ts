export interface OrderedSynthesisItem<T> {
  readonly index: number;
  readonly total: number;
  readonly value: T;
}

export interface OrderedSynthesisOptions {
  readonly maximumConcurrent?: number;
  readonly isCancelled?: () => boolean;
}

type Settled<T> =
  | { readonly ok: true; readonly value: T }
  | { readonly ok: false; readonly error: unknown };

/**
 * Generates several speech chunks concurrently while yielding them in text
 * order. This keeps playback natural even when a later TTS request finishes
 * before an earlier one.
 */
export async function* synthesizeInOrder<T>(
  chunks: readonly string[],
  synthesize: (text: string, index: number) => Promise<T>,
  options: OrderedSynthesisOptions = {},
): AsyncGenerator<OrderedSynthesisItem<T>> {
  const maximumConcurrent = options.maximumConcurrent ?? 4;
  if (!Number.isInteger(maximumConcurrent) || maximumConcurrent < 1 || maximumConcurrent > 8) {
    throw new RangeError('Speech synthesis concurrency must be between 1 and 8.');
  }

  const pending = new Map<number, Promise<Settled<T>>>();
  let nextToStart = 0;
  const cancelled = (): boolean => options.isCancelled?.() === true;
  const fill = (): void => {
    while (!cancelled() && nextToStart < chunks.length && pending.size < maximumConcurrent) {
      const index = nextToStart;
      nextToStart += 1;
      pending.set(index, Promise.resolve()
        .then(() => synthesize(chunks[index]!, index))
        .then(
          (value): Settled<T> => ({ ok: true, value }),
          (error: unknown): Settled<T> => ({ ok: false, error }),
        ));
    }
  };

  fill();
  for (let index = 0; index < chunks.length; index += 1) {
    if (cancelled()) return;
    const task = pending.get(index);
    if (!task) return;
    const result = await task;
    pending.delete(index);
    if (cancelled()) return;
    fill();
    if (!result.ok) throw result.error;
    yield { index, total: chunks.length, value: result.value };
  }
}
