const TARGET_SAMPLE_RATE = 16_000;
const MAXIMUM_RECORDING_SECONDS = 20;

export const MAXIMUM_RECORDING_FRAMES = TARGET_SAMPLE_RATE * MAXIMUM_RECORDING_SECONDS;

export function downsampleTo16Khz(chunks: readonly Float32Array[], sourceSampleRate: number): Float32Array {
  if (!Number.isFinite(sourceSampleRate) || sourceSampleRate < TARGET_SAMPLE_RATE) {
    throw new Error('麦克风采样率低于 16kHz，无法录音。');
  }
  const totalLength = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
  const input = new Float32Array(totalLength);
  let inputOffset = 0;
  for (const chunk of chunks) {
    input.set(chunk, inputOffset);
    inputOffset += chunk.length;
  }
  if (sourceSampleRate === TARGET_SAMPLE_RATE) return input;

  const ratio = sourceSampleRate / TARGET_SAMPLE_RATE;
  const output = new Float32Array(Math.floor(input.length / ratio));
  for (let outputIndex = 0; outputIndex < output.length; outputIndex += 1) {
    const start = Math.floor(outputIndex * ratio);
    const end = Math.min(input.length, Math.floor((outputIndex + 1) * ratio));
    let sum = 0;
    for (let inputIndex = start; inputIndex < end; inputIndex += 1) sum += input[inputIndex];
    output[outputIndex] = end > start ? sum / (end - start) : input[start] ?? 0;
  }
  return output;
}

export function encodePcm16Wav(samples: Float32Array): Uint8Array {
  const output = new Uint8Array(44 + samples.length * 2);
  const view = new DataView(output.buffer);
  writeAscii(output, 0, 'RIFF');
  view.setUint32(4, output.length - 8, true);
  writeAscii(output, 8, 'WAVE');
  writeAscii(output, 12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, TARGET_SAMPLE_RATE, true);
  view.setUint32(28, TARGET_SAMPLE_RATE * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  writeAscii(output, 36, 'data');
  view.setUint32(40, samples.length * 2, true);
  for (let index = 0; index < samples.length; index += 1) {
    const clamped = Math.max(-1, Math.min(1, samples[index] ?? 0));
    view.setInt16(44 + index * 2, clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff, true);
  }
  return output;
}

export function bytesToBase64(bytes: Uint8Array): string {
  let binary = '';
  const chunkSize = 32_768;
  for (let offset = 0; offset < bytes.length; offset += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + chunkSize));
  }
  return btoa(binary);
}

function writeAscii(target: Uint8Array, offset: number, value: string): void {
  for (let index = 0; index < value.length; index += 1) target[offset + index] = value.charCodeAt(index);
}
