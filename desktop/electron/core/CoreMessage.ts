import { coreEventTypes, inputEventEdges, liveEventTypes, liveStates, missionLifecycleStates, missionOrigins, type CoreEvent, type InputEvent, type LiveEvent, type RpcResponse } from '@velia/protocol';

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu;

export function isRpcResponse(value: unknown): value is RpcResponse {
  if (!isRecord(value) || value.schema !== 'velia-rpc-response/v1' || !isUuid(value.id) || typeof value.ok !== 'boolean') {
    return false;
  }

  if (value.ok) {
    return Object.hasOwn(value, 'result');
  }

  return isRecord(value.error) &&
    typeof value.error.code === 'string' &&
    typeof value.error.message === 'string' &&
    typeof value.error.retryable === 'boolean';
}

export function isCoreEvent(value: unknown): value is CoreEvent {
  if (!isRecord(value) || value.schema !== 'velia-core-event/v1') {
    return false;
  }

  if (!isUuid(value.operationId) || !Number.isSafeInteger(value.sequence) || Number(value.sequence) < 1) {
    return false;
  }

  if (typeof value.atUtc !== 'string' || Number.isNaN(Date.parse(value.atUtc))) {
    return false;
  }

  if (typeof value.type !== 'string' || !coreEventTypes.includes(value.type as CoreEvent['type'])) {
    return false;
  }

  if (typeof value.origin !== 'string' || !missionOrigins.includes(value.origin as CoreEvent['origin'])) {
    return false;
  }

  const allowed = [
    'schema', 'operationId', 'sequence', 'atUtc', 'type', 'origin', 'state', 'delta',
    'toolCall', 'finishReason', 'usage', 'error', 'mission', 'notification',
  ];
  if (!Object.keys(value).every((key) => allowed.includes(key))) return false;

  return (value.type !== 'content' || typeof value.delta === 'string') &&
    (value.type !== 'tool' || isToolCall(value.toolCall)) &&
    (value.type !== 'failed' || isRpcError(value.error)) &&
    (value.type !== 'mission' || isMissionSnapshot(value.mission)) &&
    (value.type !== 'notification' || isCoreNotification(value.notification));
}

function isCoreNotification(value: unknown): boolean {
  return isRecord(value) &&
    Object.keys(value).every((key) => ['kind', 'title', 'message'].includes(key)) &&
    value.kind === 'timerElapsed' &&
    typeof value.title === 'string' && value.title.length >= 1 && value.title.length <= 80 &&
    typeof value.message === 'string' && value.message.length >= 1 && value.message.length <= 200;
}

export function isInputEvent(value: unknown): value is InputEvent {
  return isRecord(value) &&
    value.schema === 'velia-input-event/v1' &&
    isUuid(value.eventId) &&
    Number.isSafeInteger(value.sequence) && Number(value.sequence) >= 1 &&
    typeof value.atUtc === 'string' && !Number.isNaN(Date.parse(value.atUtc)) &&
    ((value.shortcut === 'voicePushToTalk' && ['F8', 'Ctrl+Shift+Space'].includes(String(value.key))) ||
      (value.shortcut === 'voiceLongConversation' && value.key === 'Ctrl+Alt+L')) &&
    typeof value.edge === 'string' && inputEventEdges.includes(value.edge as InputEvent['edge']) &&
    Object.keys(value).every((key) => ['schema', 'eventId', 'sequence', 'atUtc', 'shortcut', 'key', 'edge'].includes(key));
}

function isMissionSnapshot(value: unknown): boolean {
  return isRecord(value) && value.schema === 'velia-mission-snapshot/v1' &&
    isUuid(value.missionId) && Number.isSafeInteger(value.revision) && Number(value.revision) >= 1 &&
    typeof value.origin === 'string' && missionOrigins.includes(value.origin as (typeof missionOrigins)[number]) &&
    typeof value.state === 'string' && missionLifecycleStates.includes(value.state as (typeof missionLifecycleStates)[number]) &&
    typeof value.currentRequest === 'string' && value.currentRequest.length >= 1 && value.currentRequest.length <= 32_768 &&
    Number.isSafeInteger(value.requirementCount) && Number(value.requirementCount) >= 1 &&
    typeof value.createdAtUtc === 'string' && !Number.isNaN(Date.parse(value.createdAtUtc)) &&
    typeof value.updatedAtUtc === 'string' && !Number.isNaN(Date.parse(value.updatedAtUtc)) &&
    (value.message === undefined || (typeof value.message === 'string' && value.message.length >= 1 && value.message.length <= 4_096));
}

export function isLiveEvent(value: unknown): value is LiveEvent {
  if (!isRecord(value) || value.schema !== 'velia-live-event/v1' || !isUuid(value.sessionId) ||
      !Number.isSafeInteger(value.sequence) || Number(value.sequence) < 1 ||
      typeof value.atUtc !== 'string' || Number.isNaN(Date.parse(value.atUtc)) ||
      typeof value.type !== 'string' || !liveEventTypes.includes(value.type as LiveEvent['type'])) return false;
  const allowed = ['schema', 'sessionId', 'sequence', 'atUtc', 'type', 'state', 'audioBase64', 'mimeType', 'transcript', 'toolCall', 'message', 'retryable', 'resultOk'];
  if (!Object.keys(value).every((key) => allowed.includes(key))) return false;
  if (value.type !== 'localResult' && value.resultOk !== undefined) return false;
  if (value.state !== undefined && (typeof value.state !== 'string' || !liveStates.includes(value.state as (typeof liveStates)[number]))) return false;
  if (value.type === 'audio') return typeof value.audioBase64 === 'string' && value.audioBase64.length <= 524_288 &&
    value.mimeType === 'audio/pcm;rate=24000';
  if (value.type === 'inputTranscript' || value.type === 'outputTranscript')
    return typeof value.transcript === 'string' && value.transcript.length <= 32_768;
  if (value.type === 'localResult')
    return typeof value.transcript === 'string' && value.transcript.length >= 1 && value.transcript.length <= 4_000 &&
      typeof value.resultOk === 'boolean' &&
      value.state === undefined && value.audioBase64 === undefined && value.mimeType === undefined &&
      value.toolCall === undefined && value.message === undefined && value.retryable === undefined;
  if (value.type === 'toolCall') return isLiveToolCall(value.toolCall);
  if (value.type === 'failed') return typeof value.message === 'string' && value.message.length <= 300 && typeof value.retryable === 'boolean';
  return value.audioBase64 === undefined && value.mimeType === undefined && value.toolCall === undefined &&
    value.resultOk === undefined;
}

function isLiveToolCall(value: unknown): boolean {
  return isRecord(value) &&
    Object.keys(value).every((key) => ['id', 'name', 'question'].includes(key)) &&
    typeof value.id === 'string' && value.id.length >= 1 && value.id.length <= 256 &&
    value.name === 'capture_current_screen' &&
    typeof value.question === 'string' && value.question.length >= 1 && value.question.length <= 1_000;
}

function isToolCall(value: unknown): boolean {
  return isRecord(value) && Number.isInteger(value.index) && Number(value.index) >= 0 &&
    typeof value.id === 'string' && value.id.length >= 1 && value.id.length <= 256 &&
    typeof value.name === 'string' && value.name.length >= 1 && value.name.length <= 64 &&
    typeof value.argumentsFragment === 'string' && value.argumentsFragment.length <= 65_536;
}

function isRpcError(value: unknown): boolean {
  return isRecord(value) &&
    typeof value.code === 'string' &&
    typeof value.message === 'string' &&
    typeof value.retryable === 'boolean';
}

function isUuid(value: unknown): value is string {
  return typeof value === 'string' && uuidPattern.test(value);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
