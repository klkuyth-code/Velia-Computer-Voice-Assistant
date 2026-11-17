import { describe, expect, it } from 'vitest';
import { LiveLocalUtteranceCapture } from './LiveLocalUtteranceCapture';
import type { WakeUtterance } from './WakeUtteranceDetector';

describe('LiveLocalUtteranceCapture', () => {
  it('does not turn quiet playback residue into a local utterance', () => {
    const capture = new LiveLocalUtteranceCapture();
    capture.setPlaybackActive(true);

    for (let index = 0; index < 30; index += 1)
      expect(capture.accept(frame(0.01)).utterance).toBeUndefined();
  });

  it('captures sustained near-field speech while playback is active', () => {
    const capture = new LiveLocalUtteranceCapture();
    capture.setPlaybackActive(true);
    let utterance: WakeUtterance | undefined;

    let speechStarts = 0;
    for (let index = 0; index < 10; index += 1) {
      const accepted = capture.accept(frame(0.2));
      if (accepted.speechStarted) speechStarts += 1;
      utterance = accepted.utterance ?? utterance;
    }
    for (let index = 0; index < 8; index += 1)
      utterance = capture.accept(frame(0)).utterance ?? utterance;

    expect(utterance).toBeDefined();
    expect(utterance?.durationMs).toBeGreaterThanOrEqual(500);
    expect(speechStarts).toBe(1);
  });

  it('finishes an interruption after playback stops instead of resetting it', () => {
    const capture = new LiveLocalUtteranceCapture();
    capture.setPlaybackActive(true);
    for (let index = 0; index < 6; index += 1) capture.accept(frame(0.2));

    capture.setPlaybackActive(false);
    let utterance: WakeUtterance | undefined;
    for (let index = 0; index < 8; index += 1)
      utterance = capture.accept(frame(0)).utterance ?? utterance;

    expect(utterance).toBeDefined();
  });
});

function frame(level: number, samples = 640): Uint8Array {
  const bytes = new Uint8Array(samples * 2);
  const view = new DataView(bytes.buffer);
  const value = Math.round(Math.max(-1, Math.min(1, level)) * 0x7fff);
  for (let index = 0; index < samples; index += 1) view.setInt16(index * 2, value, true);
  return bytes;
}
