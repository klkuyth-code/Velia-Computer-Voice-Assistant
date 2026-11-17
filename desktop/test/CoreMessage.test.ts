import { describe, expect, it } from 'vitest';
import { isCoreEvent, isInputEvent, isLiveEvent, isRpcResponse } from '../electron/core/CoreMessage.js';

describe('Core message guards', () => {
  it('distinguishes stream events from RPC responses', () => {
    expect(isCoreEvent({
      schema: 'velia-core-event/v1',
      operationId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 1,
      atUtc: '2026-08-10T09:00:00Z',
      type: 'content',
      origin: 'text',
      delta: '你好',
    })).toBe(true);

    expect(isRpcResponse({
      schema: 'velia-rpc-response/v1',
      id: '1196ea21-1692-46a8-b87b-92c608429ead',
      ok: true,
      result: {},
    })).toBe(true);
  });

  it('rejects malformed event payloads', () => {
    expect(isCoreEvent({
      schema: 'velia-core-event/v1',
      operationId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 0,
      atUtc: 'not-a-time',
      type: 'content',
      origin: 'text',
    })).toBe(false);
  });

  it('accepts only bounded physical input edges', () => {
    const event = {
      schema: 'velia-input-event/v1',
      eventId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 1,
      atUtc: '2026-08-13T05:00:00Z',
      shortcut: 'voicePushToTalk',
      key: 'F8',
      edge: 'released',
    };
    expect(isInputEvent(event)).toBe(true);
    expect(isInputEvent({ ...event, key: 'Ctrl+Shift+Space' })).toBe(true);
    expect(isInputEvent({ ...event, key: 'Ctrl+Alt+Space' })).toBe(false);
    expect(isInputEvent({ ...event, shortcut: 'voiceLongConversation', key: 'Ctrl+Alt+L', edge: 'pressed' })).toBe(true);
    expect(isInputEvent({ ...event, key: 'Ctrl+Alt+L' })).toBe(false);
    expect(isInputEvent({ ...event, key: 'Ctrl+Alt+Delete' })).toBe(false);
    expect(isInputEvent({ ...event, edge: 'repeat' })).toBe(false);
    expect(isInputEvent({ ...event, command: 'not allowed' })).toBe(false);
  });

  it('accepts a strict mission lifecycle event', () => {
    const event = {
      schema: 'velia-core-event/v1',
      operationId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 2,
      atUtc: '2026-08-15T02:00:00Z',
      type: 'mission',
      origin: 'voice',
      mission: {
        schema: 'velia-mission-snapshot/v1',
        missionId: '88dbc31f-e8e4-4500-8af5-cad59b87acdf',
        revision: 1,
        origin: 'voice',
        state: 'running',
        currentRequest: '读取当前页面',
        requirementCount: 1,
        createdAtUtc: '2026-08-15T02:00:00Z',
        updatedAtUtc: '2026-08-15T02:00:00Z',
      },
    };
    expect(isCoreEvent(event)).toBe(true);
    expect(isCoreEvent({ ...event, mission: { ...event.mission, state: 'shell' } })).toBe(false);
  });

  it('accepts only a bounded timer notification', () => {
    const event = {
      schema: 'velia-core-event/v1',
      operationId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 1,
      atUtc: '2026-08-16T06:00:00Z',
      type: 'notification',
      origin: 'system',
      notification: { kind: 'timerElapsed', title: 'Velia 计时器', message: '计时结束。' },
    };
    expect(isCoreEvent(event)).toBe(true);
    expect(isCoreEvent({ ...event, origin: 'browser' })).toBe(false);
    const { origin: _origin, ...withoutOrigin } = event;
    expect(isCoreEvent(withoutOrigin)).toBe(false);
    expect(isCoreEvent({ ...event, shell: 'powershell.exe' })).toBe(false);
    expect(isCoreEvent({ ...event, notification: { ...event.notification, command: 'anything' } })).toBe(false);
    expect(isCoreEvent({ ...event, notification: { ...event.notification, kind: 'openApplication' } })).toBe(false);
  });

  it('accepts a bounded local Live result without allowing encoded audio', () => {
    const event = {
      schema: 'velia-live-event/v1',
      sessionId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 3,
      atUtc: '2026-08-14T02:00:00Z',
      type: 'localResult',
      transcript: '第三页已经打开。',
      resultOk: true,
    };
    expect(isLiveEvent(event)).toBe(true);
    expect(isLiveEvent({ ...event, transcript: '' })).toBe(false);
    expect(isLiveEvent({ ...event, transcript: 'a'.repeat(4_001) })).toBe(false);
    expect(isLiveEvent({ ...event, resultOk: undefined })).toBe(false);
    expect(isLiveEvent({ ...event, type: 'listening' })).toBe(false);
    expect(isLiveEvent({ ...event, audioBase64: 'AA==' })).toBe(false);
  });

  it('accepts only strict bounded Live events', () => {
    const event = {
      schema: 'velia-live-event/v1',
      sessionId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 1,
      atUtc: '2026-08-14T02:00:00Z',
      type: 'audio',
      state: 'speaking',
      audioBase64: 'AAECAw==',
      mimeType: 'audio/pcm;rate=24000',
    };
    expect(isLiveEvent(event)).toBe(true);
    expect(isLiveEvent({ ...event, apiKey: 'secret' })).toBe(false);
    expect(isLiveEvent({ ...event, mimeType: 'audio/mpeg' })).toBe(false);
    expect(isLiveEvent({ ...event, sequence: 0 })).toBe(false);
  });

  it('accepts only the allowlisted read-only Live screen tool', () => {
    const event = {
      schema: 'velia-live-event/v1',
      sessionId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 2,
      atUtc: '2026-08-14T02:00:00Z',
      type: 'toolCall',
      toolCall: { id: 'call-1', name: 'capture_current_screen', question: '当前是什么软件？' },
    };
    expect(isLiveEvent(event)).toBe(true);
    expect(isLiveEvent({ ...event, toolCall: { ...event.toolCall, name: 'run_shell' } })).toBe(false);
    expect(isLiveEvent({ ...event, toolCall: { ...event.toolCall, command: 'anything' } })).toBe(false);
  });
});
