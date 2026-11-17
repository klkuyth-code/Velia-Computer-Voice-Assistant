export const liveModels = ['gemini-3.1-flash-live-preview'] as const;
export type LiveModel = (typeof liveModels)[number];

export const liveVoiceNames = ['Zephyr', 'Kore'] as const;
export type LiveVoiceName = (typeof liveVoiceNames)[number];

export const liveStates = ['inactive', 'connecting', 'listening', 'speaking', 'reconnecting', 'error'] as const;
export type LiveState = (typeof liveStates)[number];

export const liveEventTypes = [
  'setup', 'listening', 'speaking', 'audio', 'inputTranscript', 'outputTranscript',
  'localResult', 'toolCall', 'interrupted', 'turnComplete', 'reconnecting', 'closed', 'failed',
] as const;
export type LiveEventType = (typeof liveEventTypes)[number];

export interface LiveStartParams extends Record<string, unknown> {
  readonly model: LiveModel;
  readonly voiceName: LiveVoiceName;
}

export interface LiveStartResult {
  readonly sessionId: string;
  readonly model: LiveModel;
  readonly inputSampleRate: 16000;
  readonly outputSampleRate: 24000;
}

export interface LiveSendAudioParams extends Record<string, unknown> {
  readonly sessionId: string;
  readonly audioBase64: string;
  readonly mimeType: 'audio/pcm;rate=16000';
}

export interface LiveSendAudioResult {
  readonly sessionId: string;
  readonly accepted: boolean;
  readonly bufferedChunks: number;
}

export interface LiveSendVideoParams extends Record<string, unknown> {
  readonly sessionId: string;
  readonly imageBase64: string;
  readonly mimeType: 'image/jpeg';
}

export interface LiveSendVideoResult {
  readonly sessionId: string;
  readonly accepted: boolean;
}

export interface LiveSubmitLocalTranscriptParams extends Record<string, unknown> {
  readonly sessionId: string;
  readonly transcript: string;
  readonly utteranceId: string;
  readonly capturedAtUtc: string;
}

export interface LiveSubmitLocalTranscriptResult {
  readonly sessionId: string;
  readonly handled: boolean;
  readonly verified: boolean;
  readonly duplicate: boolean;
}

export interface LiveVisionStatus {
  readonly active: boolean;
  readonly frames: number;
  readonly source?: string;
  readonly width?: number;
  readonly height?: number;
  readonly reason?: string;
  readonly error?: string;
}

export interface LiveToolCall {
  readonly id: string;
  readonly name: 'capture_current_screen';
  readonly question: string;
}

export interface LiveSubmitToolResultParams extends Record<string, unknown> {
  readonly sessionId: string;
  readonly callId: string;
  readonly name: 'capture_current_screen';
  readonly ok: boolean;
  readonly result?: string;
  readonly error?: string;
}

export interface LiveSubmitToolResultResult {
  readonly sessionId: string;
  readonly callId: string;
  readonly accepted: boolean;
}

export interface LiveStopParams extends Record<string, unknown> {
  readonly sessionId: string;
}

export interface LiveStopResult {
  readonly sessionId: string;
  readonly accepted: boolean;
}

export interface LiveStatusResult {
  readonly state: LiveState;
  readonly sessionId?: string;
  readonly model?: LiveModel;
  readonly message?: string;
}

export interface LiveEvent {
  readonly schema: 'velia-live-event/v1';
  readonly sessionId: string;
  readonly sequence: number;
  readonly atUtc: string;
  readonly type: LiveEventType;
  readonly state?: LiveState;
  readonly audioBase64?: string;
  readonly mimeType?: 'audio/pcm;rate=24000';
  readonly transcript?: string;
  readonly toolCall?: LiveToolCall;
  readonly message?: string;
  readonly retryable?: boolean;
  readonly resultOk?: boolean;
}
