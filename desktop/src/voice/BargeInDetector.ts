import { downsampleTo16Khz, encodePcm16Wav } from './audio';
import { audioModeCoordinator } from './AudioModeCoordinator';
import type { RecordingResult } from './MicrophoneRecorder';
import { WakeUtteranceDetector } from './WakeUtteranceDetector';

/**
 * Playback-aware local speech gate. Chromium echo cancellation runs before
 * this stricter VAD. Audio is not uploaded until a complete user utterance is
 * captured after an actual interruption.
 */
export class BargeInDetector {
  readonly #utterances = new WakeUtteranceDetector({
    preRollMs: 500,
    speechStartMs: 260,
    endSilenceMs: 700,
    minimumSpeechMs: 260,
    maximumUtteranceMs: 8_000,
    minimumRms: 0.025,
  });
  #context: AudioContext | undefined;
  #stream: MediaStream | undefined;
  #source: MediaStreamAudioSourceNode | undefined;
  #worklet: AudioWorkletNode | undefined;
  #silentGain: GainNode | undefined;
  #generation = 0;
  #speechStarted = false;
  #onSpeechStart: (() => void) | undefined;
  #onRecording: ((recording: RecordingResult) => void) | undefined;

  async start(onSpeechStart: () => void, onRecording: (recording: RecordingResult) => void): Promise<boolean> {
    if (this.#context || this.#stream) return true;
    if (!audioModeCoordinator.acquire('bargeIn')) return false;
    this.#onSpeechStart = onSpeechStart;
    this.#onRecording = onRecording;
    const generation = this.#generation + 1;
    this.#generation = generation;
    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
        video: false,
      });
    } catch {
      audioModeCoordinator.release('bargeIn');
      return false;
    }
    if (generation !== this.#generation || audioModeCoordinator.owner !== 'bargeIn') {
      for (const track of stream.getTracks()) track.stop();
      if (audioModeCoordinator.owner === 'bargeIn') audioModeCoordinator.release('bargeIn');
      return false;
    }
    try {
      const context = new AudioContext({ latencyHint: 'interactive' });
      await context.audioWorklet.addModule(new URL(`${import.meta.env.BASE_URL}audio-capture-worklet.js`, window.location.href));
      if (generation !== this.#generation || audioModeCoordinator.owner !== 'bargeIn') {
        for (const track of stream.getTracks()) track.stop();
        await context.close();
        return false;
      }
      const source = context.createMediaStreamSource(stream);
      const worklet = new AudioWorkletNode(context, 'velia-audio-capture');
      const silentGain = context.createGain();
      silentGain.gain.value = 0;
      worklet.port.onmessage = (event: MessageEvent<unknown>) => this.#accept(event.data, context.sampleRate);
      source.connect(worklet).connect(silentGain).connect(context.destination);
      this.#context = context;
      this.#stream = stream;
      this.#source = source;
      this.#worklet = worklet;
      this.#silentGain = silentGain;
      await context.resume();
      return true;
    } catch {
      for (const track of stream.getTracks()) track.stop();
      this.stop();
      return false;
    }
  }

  stop(): void {
    this.#generation += 1;
    this.#utterances.reset();
    this.#worklet?.port.close();
    this.#source?.disconnect();
    this.#worklet?.disconnect();
    this.#silentGain?.disconnect();
    for (const track of this.#stream?.getTracks() ?? []) track.stop();
    void this.#context?.close().catch(() => undefined);
    this.#context = undefined;
    this.#stream = undefined;
    this.#source = undefined;
    this.#worklet = undefined;
    this.#silentGain = undefined;
    this.#speechStarted = false;
    audioModeCoordinator.release('bargeIn');
  }

  #accept(value: unknown, sampleRate: number): void {
    if (!(value instanceof Float32Array) || audioModeCoordinator.owner !== 'bargeIn') return;
    const wasActive = this.#utterances.speechActive;
    const utterance = this.#utterances.accept(value, sampleRate);
    if (!wasActive && this.#utterances.speechActive && !this.#speechStarted) {
      this.#speechStarted = true;
      this.#onSpeechStart?.();
    }
    if (!utterance) return;
    const samples = downsampleTo16Khz(utterance.chunks, utterance.sampleRate);
    const recording: RecordingResult = {
      wav: encodePcm16Wav(samples),
      durationMs: Math.round(samples.length * 1_000 / 16_000),
      speechDetected: true,
      stopReason: 'endOfSpeech',
    };
    const onRecording = this.#onRecording;
    this.stop();
    onRecording?.(recording);
  }
}
