export const wakeWordRuntimeStates = [
  'disabled',
  'starting',
  'listening',
  'processing',
  'paused',
  'error',
  'unavailable',
] as const;

export type WakeWordRuntimeState = (typeof wakeWordRuntimeStates)[number];

export interface WakeWordStatus {
  readonly enabled: boolean;
  readonly available: boolean;
  readonly keyword: '嘿 Velia';
  readonly localOnly: true;
  readonly unavailableReason?: string;
}

export interface WakeWordDetectionResult {
  readonly detected: boolean;
  readonly transcript: string;
  readonly latencyMs: number;
}

export interface WakeWordPrivacyEvent {
  readonly paused: boolean;
  readonly reason: string;
}
