import { describe, expect, it } from 'vitest';
import { maximumSpeechChunkLength, normalizeSpeechText, splitSpeechText } from '../src/voice/speechText.js';

describe('speech text chunking', () => {
  it('normalizes model formatting and URLs before synthesis', () => {
    expect(normalizeSpeechText('**结果**：参见 https://example.com/a\n下一行')).toBe('结果：参见 链接 下一行');
  });

  it('splits long answers at natural punctuation within the online TTS limit', () => {
    const text = `${'第一部分内容，'.repeat(24)}这是第二部分。${'最后一部分。'.repeat(24)}`;
    const chunks = splitSpeechText(text);

    expect(chunks.length).toBeGreaterThan(1);
    expect(chunks.every((chunk) => chunk.length <= maximumSpeechChunkLength)).toBe(true);
    expect(chunks.join('')).toBe(normalizeSpeechText(text).replaceAll(' ', ''));
  });

  it('hard-splits text without punctuation and rejects unsafe limits', () => {
    const chunks = splitSpeechText('测'.repeat(500));
    expect(chunks.map((chunk) => chunk.length)).toEqual([220, 220, 60]);
    expect(() => splitSpeechText('测试', 20)).toThrow('invalid');
  });
});
