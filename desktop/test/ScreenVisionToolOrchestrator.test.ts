import { describe, expect, it } from 'vitest';
import type { CoreEvent, RpcMethod } from '@velia/protocol';
import { ScreenVisionToolOrchestrator } from '../electron/vision/ScreenVisionToolOrchestrator.js';

describe('ordinary screen vision tool', () => {
  it('captures one fresh hidden-assistant frame and submits bounded visual evidence', async () => {
    const core = new FakeCore();
    const capture = new FakeCapture();
    const tool = new ScreenVisionToolOrchestrator(core as never, capture as never);

    tool.handle(event('{"question":"读出当前窗口中的三行文字"}'));
    await core.waitForSubmission();

    expect(capture.calls).toBe(1);
    expect(core.requests.map((request) => request.method)).toEqual([
      'desktopVisual.getForeground',
      'vision.analyze',
      'conversation.submitToolResult',
    ]);
    expect(core.submission).toMatchObject({
      ok: true,
      result: { analysis: '第一行\n第二行\n第三行', evidence: 'freshCurrentScreen' },
    });
  });

  it('rejects malformed requests before a screenshot is taken', async () => {
    const core = new FakeCore();
    const capture = new FakeCapture();
    const tool = new ScreenVisionToolOrchestrator(core as never, capture as never);

    tool.handle(event('{"question":"读屏","extra":true}'));
    await core.waitForSubmission();

    expect(capture.calls).toBe(0);
    expect(core.submission).toMatchObject({ ok: false, error: { code: 'invalid_screen_vision' } });
  });
});

function event(argumentsFragment: string): CoreEvent {
  return {
    schema: 'velia-core-event/v1',
    operationId: '1196ea21-1692-46a8-b87b-92c608429ead',
    sequence: 1,
    atUtc: new Date().toISOString(),
    type: 'tool',
    origin: 'text',
    toolCall: { index: 0, id: 'screen-call', name: 'screen_vision', argumentsFragment },
  };
}

class FakeCapture {
  calls = 0;

  async captureBoundDesktopAutomationFrame<T>(bindTarget: () => Promise<T>): Promise<unknown> {
    this.calls += 1;
    return {
      target: await bindTarget(),
      capture: {
        imageBase64: 'AA==', mimeType: 'image/jpeg', displayId: '1', capturedAtUtc: new Date().toISOString(),
        width: 800, height: 600, inputBytes: 1, digest: 'fresh',
        displayBounds: { x: 0, y: 0, width: 800, height: 600 }, scaleFactor: 1,
      },
    };
  }
}

class FakeCore {
  readonly requests: Array<{ method: RpcMethod; params: Record<string, unknown> }> = [];
  submission: Record<string, unknown> | undefined;

  async request<TResult>(method: RpcMethod, params: Record<string, unknown>): Promise<TResult> {
    this.requests.push({ method, params });
    if (method === 'desktopVisual.getForeground') return {
      targetToken: '88dbc31f-e8e4-4500-8af5-cad59b87acdf',
      processId: 123,
      processName: 'notepad',
      windowTitle: '测试窗口',
      screenX: 200,
      screenY: 150,
      capturedAtUtc: new Date().toISOString(),
    } as TResult;
    if (method === 'vision.analyze') return { text: '第一行\n第二行\n第三行' } as TResult;
    if (method === 'conversation.submitToolResult') {
      this.submission = params;
      return { operationId: params.operationId, toolCallId: params.toolCallId, accepted: true } as TResult;
    }
    throw new Error(`Unexpected method: ${method}`);
  }

  async waitForSubmission(): Promise<void> {
    const deadline = Date.now() + 2_000;
    while (!this.submission) {
      if (Date.now() >= deadline) throw new Error('Timed out waiting for screen tool result.');
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
  }
}
