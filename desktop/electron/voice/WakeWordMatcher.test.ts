import { describe, expect, it } from 'vitest';
import { containsVeliaWakeWord } from './WakeWordMatcher';

describe('Velia wake-word matcher', () => {
  it.each([
    'Hey, Velia!',
    'hi velliA',
    '嘿 Velia。',
    '嗨，维莉娅',
    '你好薇莉娅',
  ])('accepts a conservative known transcription: %s', (text) => {
    expect(containsVeliaWakeWord(text)).toBe(true);
  });

  it.each([
    'Velia',
    'Hey Siri',
    '你好，今天怎么样',
    '播放音乐',
    '',
  ])('rejects text without the complete wake phrase: %s', (text) => {
    expect(containsVeliaWakeWord(text)).toBe(false);
  });
});
