import { downsampleTo16Khz, encodePcm16Wav, MAXIMUM_RECORDING_FRAMES } from './audio';
import { audioModeCoordinator } from './AudioModeCoordinator';
import type { AudioMode } from './AudioModeCoordinator';
import { ConversationTurnDetector, type ConversationTurnStopReason } from './ConversationTurnDetector';

export interface RecordingResult {
  readonly wav: Uint8Array;
  readonly durationMs: number;
  readonly speechDetected: boolean;
  readonly stopReason?: ConversationTurnStopReason;
}

export class MicrophoneRecorder {
  #context: AudioContext | undefined;
  #stream: MediaStream | undefined;
  #source: MediaStreamAudioSourceNode | undefined;
  #worklet: AudioWorkletNode | undefined;
  #silentGain: GainNode | undefined;
  #chunks: Float32Array[] = [];
  #frames = 0;
  #stopped = false;
  #autoStop: (() => void) | undefined;
  #autoStopQueued = false;
  #stopReason: ConversationTurnStopReason | undefined;
  readonly #turnDetector: ConversationTurnDetector | undefined;

  constructor(readonly mode: Extract<AudioMode, 'pushToTalk' | 'longConversation' | 'wakeCommand'> = 'pushToTalk') {
    this.#turnDetector = mode === 'longConversation'
      ? new ConversationTurnDetector()
      : mode === 'wakeCommand'
        ? new ConversationTurnDetector({ initialSilenceMs: 9_000, endSilenceMs: 850, maximumDurationMs: 20_000 })
        : undefined;
  }

  async start(onAutoStop: () => void): Promise<void> {
    if (this.#context) throw new Error('录音已经开始。');
    if (!audioModeCoordinator.acquire(this.mode)) throw new Error('其他语音模式正在使用麦克风，请先结束后重试。');
    this.#autoStop = onAutoStop;
    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          channelCount: 1,
          echoCancellation: true,
          noiseSuppression: true,
          autoGainControl: true,
        },
        video: false,
      });
    } catch (error) {
      audioModeCoordinator.release(this.mode);
      throw error;
    }
    try {
      const context = new AudioContext({ latencyHint: 'interactive' });
      await context.audioWorklet.addModule(new URL(`${import.meta.env.BASE_URL}audio-capture-worklet.js`, window.location.href));
      const source = context.createMediaStreamSource(stream);
      const worklet = new AudioWorkletNode(context, 'velia-audio-capture');
      const silentGain = context.createGain();
      silentGain.gain.value = 0;
      worklet.port.onmessage = (event: MessageEvent<unknown>) => this.#acceptChunk(event.data);
      source.connect(worklet).connect(silentGain).connect(context.destination);
      this.#context = context;
      this.#stream = stream;
      this.#source = source;
      this.#worklet = worklet;
      this.#silentGain = silentGain;
      await context.resume();
    } catch (error) {
      for (const track of stream.getTracks()) track.stop();
      audioModeCoordinator.release(this.mode);
      throw error;
    }
  }

  async stop(): Promise<RecordingResult> {
    if (this.#stopped || !this.#context) throw new Error('当前没有正在进行的录音。');
    this.#stopped = true;
    const context = this.#context;
    this.#worklet?.port.close();
    this.#source?.disconnect();
    this.#worklet?.disconnect();
    this.#silentGain?.disconnect();
    for (const track of this.#stream?.getTracks() ?? []) track.stop();
    await context.close();
    audioModeCoordinator.release(this.mode);
    const samples = downsampleTo16Khz(this.#chunks, context.sampleRate);
    return {
      wav: encodePcm16Wav(samples),
      durationMs: Math.round(samples.length * 1_000 / 16_000),
      speechDetected: this.#turnDetector?.speechDetected ?? samples.length > 0,
      ...(this.#stopReason ? { stopReason: this.#stopReason } : {}),
    };
  }

  cancel(): void {
    if (this.#stopped) return;
    this.#stopped = true;
    this.#worklet?.port.close();
    this.#source?.disconnect();
    this.#worklet?.disconnect();
    this.#silentGain?.disconnect();
    for (const track of this.#stream?.getTracks() ?? []) track.stop();
    void this.#context?.close();
    audioModeCoordinator.release(this.mode);
    this.#chunks = [];
  }

  #acceptChunk(value: unknown): void {
    if (this.#stopped || !(value instanceof Float32Array)) return;
    const maximumSourceFrames = Math.round(MAXIMUM_RECORDING_FRAMES * (this.#context?.sampleRate ?? 16_000) / 16_000);
    const remaining = maximumSourceFrames - this.#frames;
    if (remaining <= 0) return;
    const chunk = value.length <= remaining ? value : value.slice(0, remaining);
    this.#chunks.push(chunk);
    this.#frames += chunk.length;
    const reason = this.#turnDetector?.accept(chunk, this.#context?.sampleRate ?? 16_000);
    if (reason) this.#queueAutoStop(reason);
    else if (this.#frames >= maximumSourceFrames) this.#queueAutoStop('maximumDuration');
  }

  #queueAutoStop(reason: ConversationTurnStopReason): void {
    if (this.#autoStopQueued) return;
    this.#autoStopQueued = true;
    this.#stopReason = reason;
    queueMicrotask(() => this.#autoStop?.());
  }
}
