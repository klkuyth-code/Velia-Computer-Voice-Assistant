import { describe, expect, it, vi } from 'vitest';
import type { TtsSynthesisResult } from '@velia/protocol';
import {
  LiveResultAcknowledgementStore,
  liveResultAcknowledgementStorageKey,
  liveFailureAcknowledgementText,
  liveSuccessAcknowledgementText,
} from './LiveResultAcknowledgements';

const setting = { voiceId: 'zh-CN-XiaoxiaoNeural', rate: 20 } as const;

describe('Live result acknowledgements', () => {
  it('prepares the configured voice once and reuses persisted audio', async () => {
    const storage = new MemoryStorage();
    const store = new LiveResultAcknowledgementStore(storage);
    const synthesize = vi.fn(async (text: string): Promise<TtsSynthesisResult> => ({
      audioBase64: text === liveSuccessAcknowledgementText ? 's'.repeat(32) : 'f'.repeat(32),
      mimeType: 'audio/mpeg',
      provider: 'edgeOnline',
    }));

    const first = await store.prepare(setting, true, synthesize);
    const second = await store.prepare(setting, true, synthesize);

    expect(synthesize).toHaveBeenCalledOnce();
    expect(first).toBe('s'.repeat(32));
    expect(second).toBe(first);
  });

  it('does not reuse audio after the voice setting changes', async () => {
    const storage = new MemoryStorage();
    const store = new LiveResultAcknowledgementStore(storage);
    const synthesize = vi.fn(async (): Promise<TtsSynthesisResult> => ({
      audioBase64: 'a'.repeat(32),
      mimeType: 'audio/mpeg',
      provider: 'edgeOnline',
    }));
    await Promise.all([
      store.prepare(setting, true, synthesize),
      store.prepare(setting, false, synthesize),
    ]);

    expect(store.load(setting)?.successAudioBase64).toBe('a'.repeat(32));
    expect(store.load(setting)?.failureAudioBase64).toBe('a'.repeat(32));
    expect(store.load({ ...setting, rate: 25 })).toBeUndefined();
    expect(synthesize).toHaveBeenCalledTimes(2);
    expect(synthesize.mock.calls.map(([text]) => text)).toEqual([
      liveSuccessAcknowledgementText,
      liveFailureAcknowledgementText,
    ]);
    expect(liveFailureAcknowledgementText).toContain('没有完成');
  });

  it('synthesizes and reuses the exact verified result text without persisting it', async () => {
    const storage = new MemoryStorage();
    const store = new LiveResultAcknowledgementStore(storage);
    const synthesize = vi.fn(async (text: string): Promise<TtsSynthesisResult> => ({
      audioBase64: Buffer.from(text).toString('base64').padEnd(32, 'a'),
      mimeType: 'audio/mpeg',
      provider: 'edgeOnline',
    }));

    const first = await store.prepareText(setting, '已写入未保存的记事本。', synthesize);
    const second = await store.prepareText(setting, '已写入未保存的记事本。', synthesize);

    expect(first).toBe(second);
    expect(synthesize).toHaveBeenCalledOnce();
    expect(synthesize).toHaveBeenCalledWith('已写入未保存的记事本。', setting.voiceId, setting.rate);
    expect(storage.getItem(liveResultAcknowledgementStorageKey)).toBeNull();
  });

  it('removes Markdown decoration before local TTS synthesis', async () => {
    const store = new LiveResultAcknowledgementStore(new MemoryStorage());
    const synthesize = vi.fn(async (): Promise<TtsSynthesisResult> => ({
      audioBase64: 'm'.repeat(32),
      mimeType: 'audio/mpeg',
      provider: 'edgeOnline',
    }));

    await store.prepareText(setting, '**重点**：请查看 [说明](https://example.com)。', synthesize);

    expect(synthesize).toHaveBeenCalledWith('重点：请查看 说明链接', setting.voiceId, setting.rate);
  });
});

class MemoryStorage implements Storage {
  readonly #values = new Map<string, string>();
  get length(): number { return this.#values.size; }
  clear(): void { this.#values.clear(); }
  getItem(key: string): string | null { return this.#values.get(key) ?? null; }
  key(index: number): string | null { return [...this.#values.keys()][index] ?? null; }
  removeItem(key: string): void { this.#values.delete(key); }
  setItem(key: string, value: string): void { this.#values.set(key, value); }
}
