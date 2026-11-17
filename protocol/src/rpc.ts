import type { MissionOrigin, MissionRelationship, MissionSnapshot } from './runtime.js';

export const PROTOCOL_VERSION = '1.0' as const;

export const rpcMethods = [
  'system.health',
  'app.bootstrap',
  'config.getStatus',
  'config.getProfiles',
  'config.selectProfile',
  'config.testProvider',
  'config.setSecret',
  'config.removeSecret',
  'web.test',
  'voice.transcribe',
  'voice.testAsr',
  'voice.getShortcut',
  'voice.setShortcut',
  'live.start',
  'live.sendAudio',
  'live.sendVideo',
  'live.submitLocalTranscript',
  'live.submitToolResult',
  'live.stop',
  'live.getStatus',
  'vision.test',
  'vision.analyze',
  'document.open',
  'document.analyze',
  'document.close',
  'conversation.submitText',
  'conversation.submitToolResult',
  'conversation.cancel',
  'conversation.history.getSettings',
  'conversation.history.updateSettings',
  'conversation.history.list',
  'conversation.history.get',
  'conversation.history.open',
  'conversation.history.startNew',
  'conversation.history.delete',
  'conversation.history.clear',
  'desktopContext.capture',
  'desktopContext.replaceSelection',
  'desktopContext.operateSelectedFiles',
  'desktopContext.resolveSelectedDocument',
  'desktopContext.release',
  'desktopVisual.getForeground',
  'desktopVisual.executeAction',
] as const;

export type RpcMethod = (typeof rpcMethods)[number];

export interface RpcRequest<TParams extends Record<string, unknown> = Record<string, unknown>> {
  readonly schema: 'velia-rpc-request/v1';
  readonly id: string;
  readonly method: RpcMethod;
  readonly params: TParams;
}

export interface RpcError {
  readonly code: string;
  readonly message: string;
  readonly retryable: boolean;
  readonly details?: Readonly<Record<string, unknown>>;
}

export type RpcResponse<TResult = unknown> =
  | {
      readonly schema: 'velia-rpc-response/v1';
      readonly id: string;
      readonly ok: true;
      readonly result: TResult;
    }
  | {
      readonly schema: 'velia-rpc-response/v1';
      readonly id: string;
      readonly ok: false;
      readonly error: RpcError;
    };

export interface HealthResult {
  readonly service: 'velia-core';
  readonly version: string;
  readonly protocolVersion: typeof PROTOCOL_VERSION;
  readonly status: 'ready' | 'degraded';
  readonly nowUtc: string;
}

export const coreEventTypes = [
  'accepted',
  'state',
  'reasoning',
  'content',
  'tool',
  'completed',
  'failed',
  'cancelled',
  'mission',
  'notification',
] as const;

export type CoreEventType = (typeof coreEventTypes)[number];
export type ConversationState = 'accepted' | 'thinking' | 'responding';

export interface ConversationToolCallDelta {
  readonly index: number;
  readonly id?: string;
  readonly name?: string;
  readonly argumentsFragment?: string;
}

export interface ConversationUsage {
  readonly promptTokens: number;
  readonly completionTokens: number;
  readonly totalTokens: number;
}

export interface CoreNotification {
  readonly kind: 'timerElapsed';
  readonly title: string;
  readonly message: string;
}

export interface CoreEvent {
  readonly schema: 'velia-core-event/v1';
  readonly operationId: string;
  readonly sequence: number;
  readonly atUtc: string;
  readonly type: CoreEventType;
  readonly origin: MissionOrigin;
  readonly state?: ConversationState;
  readonly delta?: string;
  readonly toolCall?: ConversationToolCallDelta;
  readonly finishReason?: string;
  readonly usage?: ConversationUsage;
  readonly error?: RpcError;
  readonly mission?: MissionSnapshot;
  readonly notification?: CoreNotification;
}

export interface SubmitTextParams extends Record<string, unknown> {
  readonly text: string;
  readonly origin?: MissionOrigin;
  readonly relationship?: MissionRelationship | 'auto';
}

export interface SubmitTextResult {
  readonly operationId: string;
  readonly mission: MissionSnapshot;
}

export interface CancelConversationParams extends Record<string, unknown> {
  readonly operationId: string;
}

export interface CancelConversationResult {
  readonly operationId: string;
  readonly accepted: boolean;
}

export interface SubmitToolResultParams extends Record<string, unknown> {
  readonly operationId: string;
  readonly toolCallId: string;
  readonly ok: boolean;
  readonly result?: unknown;
  readonly error?: RpcError;
}

export interface SubmitToolResultResult {
  readonly operationId: string;
  readonly toolCallId: string;
  readonly accepted: boolean;
}
