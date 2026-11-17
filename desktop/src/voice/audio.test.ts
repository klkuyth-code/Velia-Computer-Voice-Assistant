import { describe, expect, it } from 'vitest';
import { downsampleTo16Khz, encodePcm16Wav } from './audio';

describe('voice audio codec', () => {
  it('downsamples 48kHz audio to 16kHz', () => {
    const input = Float32Array.from({ length: 48_000 }, (_, index) => Math.sin(index / 20));
    const result = downsampleTo16Khz([input.subarray(0, 20_000), input.subarray(20_000)], 48_000);
    expect(result).toHaveLength(16_000);
  });

  it('writes a valid mono PCM WAV header', () => {
    const wav = encodePcm16Wav(new Float32Array(16_000));
    const view = new DataView(wav.buffer);
    expect(new TextDecoder().decode(wav.subarray(0, 4))).toBe('RIFF');
    expect(new TextDecoder().decode(wav.subarray(8, 12))).toBe('WAVE');
    expect(view.getUint16(22, true)).toBe(1);
    expect(view.getUint32(24, true)).toBe(16_000);
    expect(view.getUint16(34, true)).toBe(16);
    expect(view.getUint32(40, true)).toBe(32_000);
  });
});
