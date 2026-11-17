import { describe, expect, it } from 'vitest';
import { IncrementalSpeechTextBuffer } from './speechText';

describe('IncrementalSpeechTextBuffer', () => {
  it('releases complete sentences without waiting for the final response', () => {
    const buffer = new IncrementalSpeechTextBuffer();

    expect(buffer.push('你好，我是')).toEqual([]);
    expect(buffer.push(' Velia。下一句')).toEqual(['你好，我是 Velia。']);
    expect(buffer.flush()).toEqual(['下一句']);
  });

  it('bounds punctuation-free latency without duplicating the final tail', () => {
    const buffer = new IncrementalSpeechTextBuffer();
    const longText = '这是一段没有标点但仍然需要尽快开始语音播报的连续内容'.repeat(3);

    const ready = buffer.push(longText);
    const tail = buffer.flush();

    expect(ready.length).toBeGreaterThan(0);
    expect([...ready, ...tail].join('')).toBe(longText);
    expect(buffer.flush()).toEqual([]);
  });

  it('normalizes markdown before a chunk is spoken', () => {
    const buffer = new IncrementalSpeechTextBuffer();

    expect(buffer.push('**答案**是这样。')).toEqual(['答案是这样。']);
  });
});
