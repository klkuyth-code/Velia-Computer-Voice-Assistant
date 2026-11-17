import { describe, expect, it, vi } from 'vitest';
import type { LiveEvent } from '@velia/protocol';
import { LiveVisualToolCoordinator } from './LiveVisualToolCoordinator';

const toolEvent: LiveEvent = {
  schema: 'velia-live-event/v1',
  sessionId: '1196ea21-1692-46a8-b87b-92c608429ead',
  sequence: 3,
  atUtc: '2026-08-14T01:00:00Z',
  type: 'toolCall',
  toolCall: { id: 'call-1', name: 'capture_current_screen', question: '当前是什么软件？' },
};

const frame = {
  imageBase64: '/9j/2Q==', mimeType: 'image/jpeg' as const, digest: 'digest',
  source: 'Codex', displayId: 'current-window', capturedAtUtc: '2026-08-14T01:00:01Z',
  width: 1280, height: 720, inputBytes: 4,
};

describe('LiveVisualToolCoordinator', () => {
  it('captures a fresh frame, uses the dedicated vision model, and returns verified text', async () => {
    const captureLiveFrame = vi.fn(async () => frame);
    const request = vi.fn(async (method: string) => method === 'vision.analyze'
      ? { text: '这是 Codex。', model: 'gemini-3.5-flash-lite', capturedAtUtc: frame.capturedAtUtc, providerLatencyMs: 10, inputBytes: 4 }
      : { sessionId: toolEvent.sessionId, callId: 'call-1', accepted: true });
    const coordinator = new LiveVisualToolCoordinator({ request }, { captureLiveFrame });

    await coordinator.handle(toolEvent);

    expect(captureLiveFrame).toHaveBeenCalledTimes(1);
    expect(request).toHaveBeenNthCalledWith(1, 'vision.analyze', expect.objectContaining({
      model: 'gemini-3.5-flash-lite', imageBase64: frame.imageBase64,
    }), 75_000);
    expect(request).toHaveBeenNthCalledWith(2, 'live.submitToolResult', expect.objectContaining({
      ok: true, result: '这是 Codex。',
    }), 10_000);
  });

  it('returns a bounded safe error when fresh analysis fails', async () => {
    const request = vi.fn(async (method: string) => {
      if (method === 'vision.analyze') throw new Error('vision_failed: 暂时无法识别当前画面。');
      return { sessionId: toolEvent.sessionId, callId: 'call-1', accepted: true };
    });
    const coordinator = new LiveVisualToolCoordinator({ request }, { captureLiveFrame: vi.fn(async () => frame) });

    await coordinator.handle(toolEvent);

    expect(request).toHaveBeenLastCalledWith('live.submitToolResult', expect.objectContaining({
      ok: false, error: '暂时无法识别当前画面。',
    }), 10_000);
  });

  it('reuses one fresh analysis for duplicate calls in the same turn and resets next turn', async () => {
    const captureLiveFrame = vi.fn(async () => frame);
    const request = vi.fn(async (method: string) => method === 'vision.analyze'
      ? { text: '这是 Codex。', model: 'gemini-3.5-flash-lite', capturedAtUtc: frame.capturedAtUtc, providerLatencyMs: 10, inputBytes: 4 }
      : { sessionId: toolEvent.sessionId, callId: 'call', accepted: true });
    const coordinator = new LiveVisualToolCoordinator({ request }, { captureLiveFrame });
    const duplicate: LiveEvent = {
      ...toolEvent,
      sequence: 4,
      toolCall: { ...toolEvent.toolCall!, id: 'call-2' },
    };

    await Promise.all([coordinator.handle(toolEvent), coordinator.handle(duplicate)]);

    expect(captureLiveFrame).toHaveBeenCalledTimes(1);
    expect(request.mock.calls.filter(([method]) => method === 'vision.analyze')).toHaveLength(1);
    expect(request.mock.calls.filter(([method]) => method === 'live.submitToolResult')).toHaveLength(2);

    await coordinator.handle({
      schema: 'velia-live-event/v1', sessionId: toolEvent.sessionId, sequence: 5,
      atUtc: '2026-08-14T01:00:03Z', type: 'turnComplete',
    });
    await coordinator.handle({
      ...toolEvent,
      sequence: 6,
      atUtc: '2026-08-14T01:00:04Z',
      toolCall: { ...toolEvent.toolCall!, id: 'call-3' },
    });

    expect(captureLiveFrame).toHaveBeenCalledTimes(2);
    expect(request.mock.calls.filter(([method]) => method === 'vision.analyze')).toHaveLength(2);
  });
});
