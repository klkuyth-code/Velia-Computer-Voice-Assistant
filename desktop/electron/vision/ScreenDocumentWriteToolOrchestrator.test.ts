import { describe, expect, it, vi } from 'vitest';
import type { CoreEvent } from '@velia/protocol';
import { ScreenDocumentWriteToolOrchestrator } from './ScreenDocumentWriteToolOrchestrator';

const operationId = '1196ea21-1692-46a8-b87b-92c608429ead';
const event: CoreEvent = {
  schema: 'velia-core-event/v1',
  operationId,
  sequence: 3,
  atUtc: '2026-08-18T08:00:00Z',
  type: 'tool',
  origin: 'live',
  toolCall: {
    index: 0,
    id: 'screen-write-1',
    name: 'screen_document_write',
    argumentsFragment: JSON.stringify({
      request: '请根据当前题目，在末尾另起一行，输入正确的答案。',
    }),
  },
};

const foreground = {
  targetToken: '2196ea21-1692-46a8-b87b-92c608429ead',
  processId: 42,
  processName: 'Notepad',
  windowTitle: '测试.txt - Notepad',
  screenX: 600,
  screenY: 400,
  capturedAtUtc: '2026-08-18T08:00:01Z',
};

const frame = {
  imageBase64: '/9j/2Q==',
  mimeType: 'image/jpeg' as const,
  digest: 'digest',
  source: 'Notepad',
  displayId: 'current-window',
  capturedAtUtc: '2026-08-18T08:00:01Z',
  width: 1280,
  height: 720,
  inputBytes: 4,
  displayBounds: { x: 0, y: 0, width: 1280, height: 720 },
};

describe('ScreenDocumentWriteToolOrchestrator', () => {
  it('uses one fresh vision result and native verified append', async () => {
    const request = vi.fn(async (method: string) => {
      if (method === 'desktopVisual.getForeground') return foreground;
      if (method === 'vision.analyze') {
        return {
          text: '{"ok":true,"text":"答案：A，198元；200×1.1×0.9=198"}',
          model: 'gemini-3.5-flash-lite',
          capturedAtUtc: frame.capturedAtUtc,
          providerLatencyMs: 10,
          inputBytes: 4,
        };
      }
      if (method === 'desktopVisual.executeAction') {
        return {
          operationId,
          action: 'appendText',
          executed: true,
          screenX: 600,
          screenY: 400,
          processId: 42,
          processName: 'Notepad',
          windowTitle: '*测试.txt - Notepad',
          verification: 'uiaTextAppended',
        };
      }
      return { operationId, toolCallId: 'screen-write-1', accepted: true };
    });
    const captureBoundDesktopAutomationFrame = vi.fn(async (bindTarget: () => Promise<unknown>) => ({
      capture: frame,
      target: await bindTarget(),
    }));
    const withOwnWindowsHidden = vi.fn(async (operation: () => Promise<unknown>) => operation());
    const orchestrator = new ScreenDocumentWriteToolOrchestrator(
      { request },
      { captureBoundDesktopAutomationFrame, withOwnWindowsHidden },
    );

    orchestrator.handle(event);
    await vi.waitFor(() => expect(request).toHaveBeenCalledTimes(4));

    expect(request).toHaveBeenNthCalledWith(2, 'vision.analyze', expect.objectContaining({
      model: 'gemini-3.5-flash-lite',
      imageBase64: frame.imageBase64,
    }), 45_000);
    expect(request).toHaveBeenNthCalledWith(3, 'desktopVisual.executeAction', expect.objectContaining({
      action: 'appendText',
      text: '答案：A，198元；200×1.1×0.9=198',
      expectedProcessId: foreground.processId,
    }), 10_000);
    expect(request).toHaveBeenNthCalledWith(4, 'conversation.submitToolResult', expect.objectContaining({
      ok: true,
      result: expect.objectContaining({ verified: true, verification: 'uiaTextAppended' }),
    }), 10_000);
    expect(withOwnWindowsHidden).toHaveBeenCalledTimes(1);
  });

  it('rejects an invalid vision answer before any desktop input', async () => {
    const request = vi.fn(async (method: string) => {
      if (method === 'desktopVisual.getForeground') return foreground;
      if (method === 'vision.analyze') return { text: 'not json' };
      return { operationId, toolCallId: 'screen-write-1', accepted: true };
    });
    const captureBoundDesktopAutomationFrame = vi.fn(async (bindTarget: () => Promise<unknown>) => ({
      capture: frame,
      target: await bindTarget(),
    }));
    const withOwnWindowsHidden = vi.fn(async (operation: () => Promise<unknown>) => operation());
    const orchestrator = new ScreenDocumentWriteToolOrchestrator(
      { request },
      { captureBoundDesktopAutomationFrame, withOwnWindowsHidden },
    );

    orchestrator.handle(event);
    await vi.waitFor(() => expect(request).toHaveBeenCalledTimes(3));

    expect(request.mock.calls.some(([method]) => method === 'desktopVisual.executeAction')).toBe(false);
    expect(request).toHaveBeenLastCalledWith('conversation.submitToolResult', expect.objectContaining({
      ok: false,
      error: expect.objectContaining({ code: 'screen_document_write_invalid_vision_result' }),
    }), 10_000);
  });
});
