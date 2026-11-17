import { describe, expect, it } from 'vitest';
import { isSpeechCancellationError } from './speech';

describe('speech cancellation classification', () => {
  it.each(['canceled', 'interrupted'])('treats %s as a normal stop', (error) => {
    expect(isSpeechCancellationError(error)).toBe(true);
  });

  it.each(['audio-busy', 'audio-hardware', 'network', 'synthesis-failed', undefined])(
    'keeps %s as a real playback failure',
    (error) => {
      expect(isSpeechCancellationError(error)).toBe(false);
    },
  );
});
