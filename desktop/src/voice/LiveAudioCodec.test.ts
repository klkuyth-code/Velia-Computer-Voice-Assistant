import { describe, expect, it } from 'vitest';
import { Pcm16Chunker, StreamingLinearResampler, decodePcm16Base64 } from './LiveAudioCodec';

describe('Live audio codec', () => {
  it('statefully resamples split 48 kHz input to approximately 16 kHz', () => {
    const input = Float32Array.from({ length: 48_000 }, (_, index) => Math.sin(index / 31));
    const resampler = new StreamingLinearResampler(48_000);
    const first = resampler.process(input.subarray(0, 17_777));
    const second = resampler.process(input.subarray(17_777));
    expect(first.length + second.length).toBeGreaterThanOrEqual(15_999);
    expect(first.length + second.length).toBeLessThanOrEqual(16_000);
  });

  it('emits exact 40 ms little-endian PCM16 chunks', () => {
    const chunker = new Pcm16Chunker();
    expect(chunker.append(new Float32Array(200))).toHaveLength(0);
    const chunks = chunker.append(Float32Array.from({ length: 1_080 }, (_, index) => index % 2 === 0 ? 1 : -1));
    expect(chunks).toHaveLength(2);
    expect(chunks[0]).toHaveLength(1_280);
    const view = new DataView(chunks[0]!.buffer);
    expect(view.getInt16(400, true)).toBeGreaterThan(32_000);
  });

  it('decodes bounded 24 kHz PCM output', () => {
    const bytes = Uint8Array.from([0xff, 0x7f, 0x00, 0x80]);
    const base64 = btoa(String.fromCharCode(...bytes));
    const samples = decodePcm16Base64(base64);
    expect(samples[0]).toBeCloseTo(1, 4);
    expect(samples[1]).toBe(-1);
  });
});
