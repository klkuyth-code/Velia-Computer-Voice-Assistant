import { describe, expect, it } from 'vitest';
import { ConversationTurnDetector } from './ConversationTurnDetector';

const sampleRate = 16_000;

describe('Conversation turn detector', () => {
  it('ends an idle session without uploading silent audio', () => {
    const detector = new ConversationTurnDetector({ initialSilenceMs: 3_000 });
    expect(feed(detector, 0, 2_900)).toBeUndefined();
    expect(feed(detector, 0, 200)).toBe('initialSilence');
    expect(detector.speechDetected).toBe(false);
  });

  it('waits for real speech and stops after trailing silence', () => {
    const detector = new ConversationTurnDetector({ endSilenceMs: 500, minimumSpeechMs: 100 });
    expect(feed(detector, 0, 300)).toBeUndefined();
    expect(feed(detector, 0.08, 300)).toBeUndefined();
    expect(detector.speechDetected).toBe(true);
    expect(feed(detector, 0, 400)).toBeUndefined();
    expect(feed(detector, 0, 200)).toBe('endOfSpeech');
  });

  it('always enforces the maximum turn duration', () => {
    const detector = new ConversationTurnDetector({ maximumDurationMs: 5_000 });
    expect(feed(detector, 0.05, 4_900)).toBeUndefined();
    expect(feed(detector, 0.05, 200)).toBe('maximumDuration');
  });
});

function feed(detector: ConversationTurnDetector, amplitude: number, milliseconds: number) {
  const frames = Math.round(sampleRate * milliseconds / 1_000);
  return detector.accept(new Float32Array(frames).fill(amplitude), sampleRate);
}
