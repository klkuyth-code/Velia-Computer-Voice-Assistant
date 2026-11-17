import { describe, expect, it, vi } from 'vitest';
import { LiveLocalCommandPipeline } from './LiveLocalCommandPipeline';
import type { LiveLocalUtterance } from './LiveLocalCommandPipeline';

describe('LiveLocalCommandPipeline', () => {
  it('submits out-of-order ASR results in capture order without dropping commands', async () => {
    const transcriptions = [deferred<string>(), deferred<string>(), deferred<string>()];
    const submitted: string[] = [];
    const pipeline = new LiveLocalCommandPipeline({
      transcribe: (utterance) => transcriptions[Number(utterance.utteranceId)]!.promise,
      submit: async (transcript) => { submitted.push(transcript); return transcript; },
      transcriptionConcurrency: 3,
    });

    expect(pipeline.enqueue(utterance('0'))).toBe(true);
    expect(pipeline.enqueue(utterance('1'))).toBe(true);
    expect(pipeline.enqueue(utterance('2'))).toBe(true);
    transcriptions[2]!.resolve('进入他的主页');
    transcriptions[1]!.resolve('搜索罗永浩');
    await Promise.resolve();
    expect(submitted).toEqual([]);

    transcriptions[0]!.resolve('打开抖音');
    await vi.waitFor(() => expect(submitted).toEqual(['打开抖音', '搜索罗永浩', '进入他的主页']));
    expect(pipeline.pendingCount).toBe(0);
  });

  it('continues with later commands after an ASR or submit failure', async () => {
    const submitted: string[] = [];
    const failures: string[] = [];
    const pipeline = new LiveLocalCommandPipeline({
      transcribe: async (item) => {
        if (item.utteranceId === '0') throw new Error('asr failed');
        return item.utteranceId === '1' ? '第二条' : '第三条';
      },
      submit: async (transcript) => {
        submitted.push(transcript);
        if (transcript === '第二条') throw new Error('submit failed');
        return transcript;
      },
      onFailure: (failure) => failures.push(failure),
    });

    pipeline.enqueue(utterance('0'));
    pipeline.enqueue(utterance('1'));
    pipeline.enqueue(utterance('2'));

    await vi.waitFor(() => expect(pipeline.pendingCount).toBe(0));
    expect(submitted).toEqual(['第二条', '第三条']);
    expect(failures).toEqual(['transcribe', 'submit']);
  });

  it('reports capacity immediately and ignores stale work after stop', async () => {
    const first = deferred<string>();
    const submitted: string[] = [];
    const pipeline = new LiveLocalCommandPipeline({
      transcribe: (item) => item.utteranceId === '0' ? first.promise : Promise.resolve('第二条'),
      submit: async (transcript) => { submitted.push(transcript); return transcript; },
      maximumPending: 2,
      transcriptionConcurrency: 1,
    });

    expect(pipeline.enqueue(utterance('0'))).toBe(true);
    expect(pipeline.enqueue(utterance('1'))).toBe(true);
    expect(pipeline.enqueue(utterance('2'))).toBe(false);
    pipeline.stop();
    first.resolve('第一条');
    await Promise.resolve();
    await Promise.resolve();

    expect(pipeline.pendingCount).toBe(0);
    expect(submitted).toEqual([]);
  });

  it('intercepts a local conversation-control utterance without submitting it to Core', async () => {
    const submitted: string[] = [];
    const intercepted: string[] = [];
    const pipeline = new LiveLocalCommandPipeline({
      transcribe: async (item) => item.utteranceId === '0' ? '停止' : '打开记事本',
      interceptTranscript: async (transcript) => {
        if (transcript !== '停止') return false;
        intercepted.push(transcript);
        return true;
      },
      submit: async (transcript) => { submitted.push(transcript); return transcript; },
    });

    pipeline.enqueue(utterance('0'));
    pipeline.enqueue(utterance('1'));

    await vi.waitFor(() => expect(pipeline.pendingCount).toBe(0));
    expect(intercepted).toEqual(['停止']);
    expect(submitted).toEqual(['打开记事本']);
  });
});

function utterance(id: string): LiveLocalUtterance {
  return {
    wavBase64: 'test',
    utteranceId: id,
    capturedAtUtc: new Date().toISOString(),
    durationMs: 500,
  };
}

function deferred<T>(): { readonly promise: Promise<T>; readonly resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((next) => { resolve = next; });
  return { promise, resolve };
}
