import { describe, expect, it } from 'vitest';
import { synthesizeInOrder } from './OrderedSynthesisPipeline';

describe('synthesizeInOrder', () => {
  it('starts a bounded look-ahead window and yields out-of-order completions in text order', async () => {
    const started: number[] = [];
    const resolvers = new Map<number, (value: string) => void>();
    const output: string[] = [];
    const run = (async () => {
      for await (const item of synthesizeInOrder(
        ['一', '二', '三', '四', '五'],
        async (_text, index) => {
          started.push(index);
          return await new Promise<string>((resolve) => resolvers.set(index, resolve));
        },
        { maximumConcurrent: 4 },
      )) output.push(item.value);
    })();

    await until(() => resolvers.size === 4);
    expect(started).toEqual([0, 1, 2, 3]);
    resolvers.get(2)!('三');
    resolvers.get(1)!('二');
    resolvers.get(0)!('一');
    await until(() => resolvers.has(4));
    resolvers.get(4)!('五');
    resolvers.get(3)!('四');
    await run;

    expect(output).toEqual(['一', '二', '三', '四', '五']);
  });

  it('stops yielding when the owning speech turn is cancelled', async () => {
    let cancelled = false;
    const output: string[] = [];
    for await (const item of synthesizeInOrder(
      ['一', '二', '三'],
      async (text) => text,
      { maximumConcurrent: 2, isCancelled: () => cancelled },
    )) {
      output.push(item.value);
      cancelled = true;
    }

    expect(output).toEqual(['一']);
  });

  it('propagates the failing chunk without changing order', async () => {
    const failure = new Error('tts failed');
    const consume = async (): Promise<void> => {
      for await (const _item of synthesizeInOrder(
        ['一', '二'],
        async (_text, index) => index === 1 ? Promise.reject(failure) : '一',
      )) { /* consume */ }
    };

    await expect(consume()).rejects.toBe(failure);
  });
});

async function until(predicate: () => boolean): Promise<void> {
  const deadline = Date.now() + 1_000;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error('Timed out waiting for synthesis state.');
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}
