import { describe, expect, it } from 'vitest';
import { WakeUtteranceDetector } from './WakeUtteranceDetector';

const sampleRate = 16_000;

describe('WakeUtteranceDetector', () => {
  it('keeps silence local and never emits it for transcription', () => {
    const detector = new WakeUtteranceDetector();
    expect(feed(detector, 0.001, 8_000)).toBeUndefined();
  });

  it('emits one bounded utterance after speech and trailing silence', () => {
    const detector = new WakeUtteranceDetector({ speechStartMs: 60, endSilenceMs: 500, minimumSpeechMs: 150 });
    expect(feed(detector, 0.001, 300)).toBeUndefined();
    expect(feed(detector, 0.08, 350)).toBeUndefined();
    expect(feed(detector, 0.001, 400)).toBeUndefined();
    const utterance = feed(detector, 0.001, 200);
    expect(utterance?.sampleRate).toBe(sampleRate);
    expect(utterance?.durationMs).toBeGreaterThanOrEqual(900);
    expect(utterance?.durationMs).toBeLessThan(1_600);
  });

  it('bounds continuous noise so memory cannot grow without limit', () => {
    const detector = new WakeUtteranceDetector({ maximumUtteranceMs: 1_500, minimumSpeechMs: 100 });
    expect(feed(detector, 0.08, 1_400)).toBeUndefined();
    const utterance = feed(detector, 0.08, 200);
    expect(utterance?.durationMs).toBeLessThanOrEqual(1_800);
  });

  it('allows a bounded long Live sentence without splitting it at eight seconds', () => {
    const detector = new WakeUtteranceDetector({
      maximumUtteranceMs: 18_000,
      endSilenceMs: 1_000,
      minimumSpeechMs: 180,
    });

    expect(feed(detector, 0.08, 9_000)).toBeUndefined();
    expect(detector.speechActive).toBe(true);
    expect(feed(detector, 0.001, 1_100)?.durationMs).toBeGreaterThan(9_500);
  });

  it('supports a stricter playback gate without treating low speaker leakage as speech', () => {
    const detector = new WakeUtteranceDetector({
      minimumRms: 0.025,
      speechStartMs: 260,
      minimumSpeechMs: 260,
      endSilenceMs: 500,
    });
    expect(feed(detector, 0.015, 3_000)).toBeUndefined();
    expect(detector.speechActive).toBe(false);
    expect(feed(detector, 0.08, 300)).toBeUndefined();
    expect(detector.speechActive).toBe(true);
    expect(feed(detector, 0.001, 600)).toBeDefined();
  });
});

function feed(detector: WakeUtteranceDetector, amplitude: number, milliseconds: number) {
  const chunkMs = 20;
  let result;
  for (let elapsed = 0; elapsed < milliseconds; elapsed += chunkMs) {
    result = detector.accept(new Float32Array(sampleRate * chunkMs / 1_000).fill(amplitude), sampleRate) ?? result;
  }
  return result;
}
