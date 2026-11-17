import { bytesToBase64, downsampleTo16Khz, encodePcm16Wav } from './audio';
import { Pcm16Chunker, StreamingLinearResampler } from './LiveAudioCodec';
import { LiveLocalUtteranceCapture } from './LiveLocalUtteranceCapture';
import { audioModeCoordinator } from './AudioModeCoordinator';
import type { LiveLocalUtterance } from './LiveLocalCommandPipeline';

export class LiveMicrophoneStream {
  #context: AudioContext | undefined;
  #stream: MediaStream | undefined;
  #source: MediaStreamAudioSourceNode | undefined;
  #worklet: AudioWorkletNode | undefined;
  #silentGain: GainNode | undefined;
  #resampler: StreamingLinearResampler | undefined;
  #chunker = new Pcm16Chunker();
  #localUtteranceCapture = new LiveLocalUtteranceCapture();
  #stopped = false;
  #onChunk: ((base64: string) => void) | undefined;
  #onLocalUtterance: ((utterance: LiveLocalUtterance) => void) | undefined;
  #onNearFieldSpeechStart: (() => void) | undefined;

  async start(
    onChunk: (base64: string) => void,
    onLocalUtterance?: (utterance: LiveLocalUtterance) => void,
    onNearFieldSpeechStart?: () => void,
  ): Promise<void> {
    if (this.#context) throw new Error('Live 麦克风已经开启。');
    if (!audioModeCoordinator.acquire('live')) throw new Error('普通录音正在使用麦克风，请先结束当前录音。');
    this.#onChunk = onChunk;
    this.#onLocalUtterance = onLocalUtterance;
    this.#onNearFieldSpeechStart = onNearFieldSpeechStart;
    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          sampleRate: 48_000,
          channelCount: 1,
          echoCancellation: true,
          noiseSuppression: true,
          autoGainControl: true,
        },
        video: false,
      });
    } catch (error) {
      audioModeCoordinator.release('live');
      throw error;
    }
    try {
      const context = new AudioContext({ latencyHint: 'interactive' });
      await context.audioWorklet.addModule(new URL(`${import.meta.env.BASE_URL}audio-capture-worklet.js`, window.location.href));
      const source = context.createMediaStreamSource(stream);
      const worklet = new AudioWorkletNode(context, 'velia-audio-capture');
      const silentGain = context.createGain();
      silentGain.gain.value = 0;
      this.#resampler = new StreamingLinearResampler(context.sampleRate);
      worklet.port.onmessage = (event: MessageEvent<unknown>) => this.#accept(event.data);
      source.connect(worklet).connect(silentGain).connect(context.destination);
      this.#context = context; this.#stream = stream; this.#source = source;
      this.#worklet = worklet; this.#silentGain = silentGain;
      await context.resume();
    } catch (error) {
      for (const track of stream.getTracks()) track.stop();
      audioModeCoordinator.release('live');
      throw error;
    }
  }

  async stop(): Promise<void> {
    if (this.#stopped) return;
    this.#stopped = true;
    this.#worklet?.port.close(); this.#source?.disconnect(); this.#worklet?.disconnect(); this.#silentGain?.disconnect();
    for (const track of this.#stream?.getTracks() ?? []) track.stop();
    this.#chunker.clear();
    this.#localUtteranceCapture.reset();
    if (this.#context) await this.#context.close();
    audioModeCoordinator.release('live');
  }

  setPlaybackActive(active: boolean): void {
    this.#localUtteranceCapture.setPlaybackActive(active);
  }

  #accept(value: unknown): void {
    if (this.#stopped || !(value instanceof Float32Array) || !this.#resampler) return;
    const samples = this.#resampler.process(value);
    for (const pcm of this.#chunker.append(samples)) {
      const { allowedFrames, speechStarted, utterance } = this.#localUtteranceCapture.accept(pcm);
      if (speechStarted) this.#onNearFieldSpeechStart?.();
      if (utterance) {
        const utterancePcm = downsampleTo16Khz(utterance.chunks, utterance.sampleRate);
        this.#onLocalUtterance?.({
          wavBase64: bytesToBase64(encodePcm16Wav(utterancePcm)),
          utteranceId: crypto.randomUUID(),
          capturedAtUtc: new Date().toISOString(),
          durationMs: utterance.durationMs,
        });
      }
      for (const allowed of allowedFrames) this.#onChunk?.(bytesToBase64(allowed));
    }
  }
}
