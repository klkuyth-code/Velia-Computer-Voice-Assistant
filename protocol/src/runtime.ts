export const assistantVisualStates = ['idle', 'listening', 'thinking', 'speaking'] as const;
export type AssistantVisualState = (typeof assistantVisualStates)[number];

export const missionLifecycleStates = [
  'accepted',
  'running',
  'awaitingInput',
  'correcting',
  'completed',
  'failed',
  'cancelled',
] as const;
export type MissionLifecycleState = (typeof missionLifecycleStates)[number];

export const missionOrigins = ['voice', 'text', 'live', 'system'] as const;
export type MissionOrigin = (typeof missionOrigins)[number];

export const missionRelationships = ['new', 'append', 'replace', 'progress', 'stop'] as const;
export type MissionRelationship = (typeof missionRelationships)[number];

export interface AssistantEvent {
  readonly schema: 'velia-assistant-event/v1';
  readonly eventId: string;
  readonly sequence: number;
  readonly atUtc: string;
  readonly state: AssistantVisualState;
  readonly source: 'voice' | 'text' | 'task' | 'playback' | 'system';
  readonly missionId?: string;
  readonly message?: string;
}

export interface MissionSnapshot {
  readonly schema: 'velia-mission-snapshot/v1';
  readonly missionId: string;
  readonly revision: number;
  readonly origin: MissionOrigin;
  readonly state: MissionLifecycleState;
  readonly currentRequest: string;
  readonly requirementCount: number;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
  readonly message?: string;
}

export const toolRiskIds = [
  'screenCapture',
  'localMutation',
  'destructive',
  'externalCommunication',
  'financial',
  'accountOrSecurityChange',
] as const;
export type ToolRiskId = (typeof toolRiskIds)[number];

export interface ToolConsentRequest {
  readonly schema: 'velia-tool-consent-request/v1';
  readonly confirmationId: string;
  readonly toolName: string;
  readonly risks: readonly ToolRiskId[];
  readonly summary: string;
  readonly target?: string;
  readonly expiresAtUtc: string;
}

export const voiceOverlayPhases = [
  'idle',
  'starting',
  'recording',
  'transcribing',
  'thinking',
  'responding',
  'speaking',
  'error',
] as const;
export type VoiceOverlayPhase = (typeof voiceOverlayPhases)[number];

export interface VoicePerformanceMetrics {
  readonly recordingMs?: number;
  readonly asrMs?: number;
  readonly firstTokenMs?: number;
  readonly totalMs?: number;
}

export interface AssistantOverlaySnapshot {
  readonly phase: VoiceOverlayPhase;
  readonly status: string;
  readonly transcript: string;
  readonly answer: string;
  readonly canStop: boolean;
  readonly shortcutLabel: string;
  readonly wakeWordActive?: boolean;
  readonly metrics: VoicePerformanceMetrics;
  readonly updatedAtUtc: string;
}

export type AssistantOverlayCommand = 'toggleRecording' | 'stop' | 'collapse' | 'expand';
