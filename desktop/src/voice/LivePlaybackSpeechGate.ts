export class LivePlaybackSpeechGate {
  #playbackActive = false;
  #open = false;
  #aboveCount = 0;
  #belowCount = 0;
  #noiseFloor = 0.006;
  #preRoll: Uint8Array[] = [];

  setPlaybackActive(active: boolean): void {
    if (this.#playbackActive === active) return;
    this.#playbackActive = active;
    this.#resetDetection();
  }

  process(frame: Uint8Array): readonly Uint8Array[] {
    if (!this.#playbackActive) return [frame];
    const { rms, peak } = pcmLevel(frame);
    this.#preRoll.push(frame);
    if (this.#preRoll.length > 5) this.#preRoll.shift();

    const threshold = Math.min(0.11, Math.max(0.05, this.#noiseFloor * 2.6 + 0.008));
    const strongVoice = rms >= 0.085 && peak >= 0.18;
    const voiceCandidate = strongVoice || (rms >= threshold && peak >= Math.max(0.16, threshold * 2.3));

    if (!this.#open && !voiceCandidate && rms <= Math.max(0.024, this.#noiseFloor * 1.55)) {
      this.#noiseFloor = this.#noiseFloor * 0.9 + Math.max(0.002, rms) * 0.1;
    }

    if (!this.#open) {
      this.#aboveCount = voiceCandidate ? this.#aboveCount + 1 : 0;
      if (this.#aboveCount < (strongVoice ? 2 : 3)) return [];
      this.#open = true;
      this.#aboveCount = 0;
      this.#belowCount = 0;
      const frames = this.#preRoll;
      this.#preRoll = [];
      return frames;
    }

    const belowVoice = rms < Math.max(0.01, threshold * 0.58);
    this.#belowCount = belowVoice ? this.#belowCount + 1 : 0;
    // Keep 320 ms of trailing quiet audio so the local utterance detector can
    // close an interruption before the echo gate shuts again.
    if (this.#belowCount >= 8) {
      this.#open = false;
      this.#aboveCount = 0;
      this.#belowCount = 0;
      this.#preRoll = [];
    }
    return [frame];
  }

  #resetDetection(): void {
    this.#open = false;
    this.#aboveCount = 0;
    this.#belowCount = 0;
    this.#noiseFloor = 0.006;
    this.#preRoll = [];
  }
}

export function pcmLevel(frame: Uint8Array): { readonly rms: number; readonly peak: number } {
  if (frame.byteLength < 2 || frame.byteLength % 2 !== 0) return { rms: 0, peak: 0 };
  const view = new DataView(frame.buffer, frame.byteOffset, frame.byteLength);
  let sumSquares = 0;
  let peak = 0;
  const samples = frame.byteLength / 2;
  for (let index = 0; index < samples; index += 1) {
    const value = view.getInt16(index * 2, true) / 0x8000;
    sumSquares += value * value;
    peak = Math.max(peak, Math.abs(value));
  }
  return { rms: Math.sqrt(sumSquares / samples), peak };
}
