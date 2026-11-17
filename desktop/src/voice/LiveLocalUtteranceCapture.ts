import { LivePlaybackSpeechGate } from './LivePlaybackSpeechGate';
import { WakeUtteranceDetector } from './WakeUtteranceDetector';
import type { WakeUtterance } from './WakeUtteranceDetector';

const sampleRate = 16_000;

/**
 * Shares the same echo-gated audio with Gemini and the local command ASR.
 * This lets genuine near-field speech become a local command even while Velia
 * is speaking, without sending ordinary speaker residue to the recognizer.
 */
export class LiveLocalUtteranceCapture {
  readonly #speechGate = new LivePlaybackSpeechGate();
  readonly #utterances = new WakeUtteranceDetector({
    preRollMs: 450,
    speechStartMs: 100,
    endSilenceMs: 300,
    minimumSpeechMs: 180,
    maximumUtteranceMs: 18_000,
    minimumRms: 0.01,
  });
  #playbackActive = false;

  setPlaybackActive(active: boolean): void {
    if (this.#playbackActive === active) return;
    this.#playbackActive = active;
    if (active || !this.#utterances.speechActive) this.#utterances.reset();
    this.#speechGate.setPlaybackActive(active);
  }

  accept(frame: Uint8Array): {
    readonly allowedFrames: readonly Uint8Array[];
    readonly speechStarted: boolean;
    readonly utterance?: WakeUtterance;
  } {
    const allowedFrames = this.#speechGate.process(frame);
    let utterance: WakeUtterance | undefined;
    const wasSpeechActive = this.#utterances.speechActive;
    for (const allowed of allowedFrames) {
      utterance = this.#utterances.accept(decodePcm16(allowed), sampleRate) ?? utterance;
    }
    const speechStarted = !wasSpeechActive && this.#utterances.speechActive;
    return utterance ? { allowedFrames, speechStarted, utterance } : { allowedFrames, speechStarted };
  }

  reset(): void {
    this.#playbackActive = false;
    this.#utterances.reset();
    this.#speechGate.setPlaybackActive(false);
  }
}

function decodePcm16(bytes: Uint8Array): Float32Array {
  if (bytes.byteLength === 0 || bytes.byteLength % 2 !== 0) return new Float32Array();
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const output = new Float32Array(bytes.byteLength / 2);
  for (let index = 0; index < output.length; index += 1) {
    const value = view.getInt16(index * 2, true);
    output[index] = value < 0 ? value / 0x8000 : value / 0x7fff;
  }
  return output;
}
