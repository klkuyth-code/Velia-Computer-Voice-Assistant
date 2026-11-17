import type { WakeWordDetectionResult, WakeWordRuntimeState } from '@velia/protocol';
import { bytesToBase64, downsampleTo16Khz, encodePcm16Wav } from './audio';
import { audioModeCoordinator, type AudioMode } from './AudioModeCoordinator';
import { WakeUtteranceDetector } from './WakeUtteranceDetector';

export interface WakeWordRuntimeSnapshot {
  readonly state: WakeWordRuntimeState;
  readonly detail: string;
}

interface WakeWordApi {
  readonly detect: (wavBase64: string) => Promise<WakeWordDetectionResult>;
}

/** Owns the passive microphone only while every privacy and audio-mode gate allows it. */
export class WakeWordDetector {
  readonly #utterances = new WakeUtteranceDetector();
  readonly #api: WakeWordApi;
  readonly #unsubscribeCoordinator: () => void;
  #desired = false;
  #applicationPaused = false;
  #applicationPauseReason = '当前语音任务占用麦克风';
  #privacyPaused = false;
  #privacyReason = '系统已暂停麦克风';
  #processing = false;
  #disposed = false;
  #generation = 0;
  #restartTimer: number | undefined;
  #context: AudioContext | undefined;
  #stream: MediaStream | undefined;
  #source: MediaStreamAudioSourceNode | undefined;
  #worklet: AudioWorkletNode | undefined;
  #silentGain: GainNode | undefined;
  #onWake: ((transcript: string) => void) | undefined;
  #onState: ((snapshot: WakeWordRuntimeSnapshot) => void) | undefined;

  constructor(api: WakeWordApi = window.velia.wake) {
    this.#api = api;
    this.#unsubscribeCoordinator = audioModeCoordinator.subscribe((owner) => this.#handleAudioOwner(owner));
  }

  start(onWake: (transcript: string) => void, onState: (snapshot: WakeWordRuntimeSnapshot) => void): void {
    this.#onWake = onWake;
    this.#onState = onState;
    this.#desired = true;
    this.#reconcile();
  }

  stop(): void {
    this.#desired = false;
    this.#processing = false;
    this.#clearRestart();
    this.#stopCapture(true);
    this.#publish('disabled', '唤醒词已关闭');
  }

  setApplicationPaused(paused: boolean, reason = '当前语音任务占用麦克风'): void {
    this.#applicationPaused = paused;
    this.#applicationPauseReason = reason;
    this.#reconcile();
  }

  setPrivacyPaused(paused: boolean, reason: string): void {
    this.#privacyPaused = paused;
    this.#privacyReason = reason;
    this.#reconcile();
  }

  retry(): void { this.#reconcile(); }

  dispose(): void {
    this.#disposed = true;
    this.#desired = false;
    this.#clearRestart();
    this.#stopCapture(true);
    this.#unsubscribeCoordinator();
    this.#onWake = undefined;
    this.#onState = undefined;
  }

  #reconcile(): void {
    if (this.#disposed || !this.#desired) {
      this.#stopCapture(true);
      if (!this.#disposed) this.#publish('disabled', '唤醒词已关闭');
      return;
    }
    if (this.#privacyPaused) {
      this.#stopCapture(true);
      this.#publish('paused', this.#privacyReason);
      return;
    }
    if (this.#applicationPaused) {
      this.#stopCapture(true);
      this.#publish('paused', this.#applicationPauseReason);
      return;
    }
    if (this.#processing) return;
    if (audioModeCoordinator.owner && audioModeCoordinator.owner !== 'wake') {
      this.#publish('paused', '其他语音模式正在使用麦克风');
      return;
    }
    if (this.#context) return;
    this.#scheduleCapture(120);
  }

  #scheduleCapture(delayMs: number): void {
    if (this.#restartTimer !== undefined || this.#disposed || !this.#desired) return;
    this.#restartTimer = window.setTimeout(() => {
      this.#restartTimer = undefined;
      void this.#startCapture();
    }, delayMs);
  }

  async #startCapture(): Promise<void> {
    if (this.#disposed || !this.#desired || this.#applicationPaused || this.#privacyPaused || this.#processing || this.#context) return;
    if (!audioModeCoordinator.acquire('wake')) {
      this.#publish('paused', '其他语音模式正在使用麦克风');
      return;
    }
    const generation = this.#generation + 1;
    this.#generation = generation;
    this.#publish('starting', '正在启动本地唤醒监听…');
    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
        video: false,
      });
    } catch (error: unknown) {
      audioModeCoordinator.release('wake');
      this.#publish('error', readMicrophoneError(error));
      return;
    }
    if (generation !== this.#generation || audioModeCoordinator.owner !== 'wake' || this.#applicationPaused || this.#privacyPaused) {
      for (const track of stream.getTracks()) track.stop();
      if (audioModeCoordinator.owner === 'wake') audioModeCoordinator.release('wake');
      return;
    }
    try {
      const context = new AudioContext({ latencyHint: 'interactive' });
      await context.audioWorklet.addModule(new URL(`${import.meta.env.BASE_URL}audio-capture-worklet.js`, window.location.href));
      if (generation !== this.#generation || audioModeCoordinator.owner !== 'wake') {
        for (const track of stream.getTracks()) track.stop();
        await context.close();
        return;
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
      this.#publish('listening', '本地待机监听“嘿 Velia”');
    } catch (error: unknown) {
      for (const track of stream.getTracks()) track.stop();
      this.#stopCapture(true);
      this.#publish('error', readMicrophoneError(error));
    }
  }

  #accept(value: unknown, sampleRate: number): void {
    if (!(value instanceof Float32Array) || this.#processing || audioModeCoordinator.owner !== 'wake') return;
    const utterance = this.#utterances.accept(value, sampleRate);
    if (!utterance) return;
    this.#processing = true;
    this.#stopCapture(true);
    this.#publish('processing', '正在本机确认唤醒词…');
    const samples = downsampleTo16Khz(utterance.chunks, utterance.sampleRate);
    const wavBase64 = bytesToBase64(encodePcm16Wav(samples));
    void this.#detect(wavBase64);
  }

  async #detect(wavBase64: string): Promise<void> {
    try {
      const result = await this.#api.detect(wavBase64);
      if (this.#disposed || !this.#desired || this.#applicationPaused || this.#privacyPaused) return;
      if (result.detected && !audioModeCoordinator.owner) {
        this.#publish('paused', '已听到“嘿 Velia”，正在打开普通语音');
        this.#onWake?.(result.transcript);
      }
    } catch (error: unknown) {
      this.#publish('error', error instanceof Error ? error.message : '本地唤醒识别失败');
    } finally {
      this.#processing = false;
      if (!this.#disposed) this.#scheduleCapture(350);
    }
  }

  #handleAudioOwner(owner: AudioMode | undefined): void {
    if (owner && owner !== 'wake') {
      this.#stopCapture(false);
      if (this.#desired) this.#publish('paused', '其他语音模式正在使用麦克风');
      return;
    }
    if (!owner) this.#reconcile();
  }

  #stopCapture(releaseOwnership: boolean): void {
    this.#generation += 1;
    this.#clearRestart();
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
    if (releaseOwnership) audioModeCoordinator.release('wake');
  }

  #clearRestart(): void {
    if (this.#restartTimer === undefined) return;
    window.clearTimeout(this.#restartTimer);
    this.#restartTimer = undefined;
  }

  #publish(state: WakeWordRuntimeState, detail: string): void { this.#onState?.({ state, detail }); }
}

function readMicrophoneError(error: unknown): string {
  if (error instanceof DOMException && error.name === 'NotAllowedError') return '麦克风权限未开启，唤醒监听已暂停';
  if (error instanceof DOMException && error.name === 'NotFoundError') return '没有检测到可用麦克风';
  return error instanceof Error ? error.message : '无法启动本地唤醒麦克风';
}
