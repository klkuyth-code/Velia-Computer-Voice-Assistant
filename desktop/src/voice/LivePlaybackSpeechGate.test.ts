import { describe, expect, it } from 'vitest';
import { LivePlaybackSpeechGate, pcmLevel } from './LivePlaybackSpeechGate';

describe('LivePlaybackSpeechGate', () => {
  it('passes microphone audio unchanged while Velia is not speaking', () => {
    const gate = new LivePlaybackSpeechGate();
    const speech = frame(0.2);

    expect(gate.process(speech)).toEqual([speech]);
  });

  it('withholds quiet speaker residue while playback is active', () => {
    const gate = new LivePlaybackSpeechGate();
    gate.setPlaybackActive(true);

    for (let index = 0; index < 12; index += 1) expect(gate.process(frame(0.01))).toEqual([]);
  });

  it('withholds moderate speaker leakage instead of treating it as a new user turn', () => {
    const gate = new LivePlaybackSpeechGate();
    gate.setPlaybackActive(true);

    for (let index = 0; index < 12; index += 1) expect(gate.process(frame(0.04))).toEqual([]);
  });

  it('opens after sustained near-field speech and returns pre-roll for interruption', () => {
    const gate = new LivePlaybackSpeechGate();
    gate.setPlaybackActive(true);

    expect(gate.process(frame(0.2))).toEqual([]);
    expect(gate.process(frame(0.2))).toHaveLength(2);
    expect(gate.process(frame(0.2))).toHaveLength(1);
  });

  it('measures little-endian PCM without accepting malformed frames', () => {
    expect(pcmLevel(frame(0.25)).rms).toBeCloseTo(0.25, 2);
    expect(pcmLevel(new Uint8Array([1]))).toEqual({ rms: 0, peak: 0 });
  });
});

function frame(level: number, samples = 640): Uint8Array {
  const bytes = new Uint8Array(samples * 2);
  const view = new DataView(bytes.buffer);
  const value = Math.round(Math.max(-1, Math.min(1, level)) * 0x7fff);
  for (let index = 0; index < samples; index += 1) view.setInt16(index * 2, value, true);
  return bytes;
}
