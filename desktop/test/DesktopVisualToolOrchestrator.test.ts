import { describe, expect, it } from 'vitest';
import type { CoreEvent, RpcMethod } from '@velia/protocol';
import { DesktopVisualToolOrchestrator } from '../electron/desktopVisual/DesktopVisualToolOrchestrator.js';
import type { DesktopAutomationCapture } from '../electron/vision/ScreenCaptureService.js';

describe('Desktop visual tool orchestrator', () => {
  it('locates, executes, recaptures and accepts one locally evidenced low-risk result', async () => {
    const core = new FakeCore();
    const capture = new FakeCapture([frame('native-before'), frame('before'), frame('after')]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    orchestrator.handle(toolEvent('call_success', '{"action":"click","target":"确定按钮"}'));
    await core.waitForSubmissions(1);

    expect(capture.calls).toBe(3);
    expect(core.requests.map((request) => request.method)).toEqual([
      'desktopVisual.getForeground',
      'desktopVisual.executeAction',
      'desktopVisual.getForeground',
      'vision.analyze',
      'desktopVisual.executeAction',
      'conversation.submitToolResult',
    ]);
    expect(core.submissions[0]).toMatchObject({
      operationId: OPERATION_ID,
      toolCallId: 'call_success',
      ok: true,
      result: { action: 'click', target: '确定按钮', executed: true, verified: true },
    });
    expect(core.requests.find((request) => request.method === 'desktopVisual.executeAction')?.params)
      .toMatchObject({ targetToken: TARGET_TOKEN });
  });

  it('uses a named Windows UIA control before requesting visual coordinates', async () => {
    const core = new FakeCore({ nativeAvailable: true, executeVerification: 'uiaInvoked' });
    const capture = new FakeCapture([frame('native-before')]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    orchestrator.handle(toolEvent('call_native', '{"action":"click","target":"确定按钮"}'));
    await core.waitForSubmissions(1);

    expect(core.requests.map((request) => request.method)).toEqual([
      'desktopVisual.getForeground',
      'desktopVisual.executeAction',
      'conversation.submitToolResult',
    ]);
    expect(core.requests.some((request) => request.method === 'vision.analyze')).toBe(false);
    expect(core.submissions[0]).toMatchObject({
      ok: true,
      result: { verified: true, state: 'uiaInvoked', verificationSource: 'uia' },
    });
  });

  it('uses one visual location directly after Core reports that UIA was insufficient', async () => {
    const core = new FakeCore();
    const capture = new FakeCapture([frame('before'), frame('after')]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    orchestrator.handle(toolEvent(
      'call_direct_visual',
      '{"action":"click","target":"搜索按钮","preferVisual":true}',
    ));
    await core.waitForSubmissions(1);

    expect(core.requests.map((request) => request.method)).toEqual([
      'desktopVisual.getForeground',
      'vision.analyze',
      'desktopVisual.executeAction',
      'conversation.submitToolResult',
    ]);
    expect(core.requests.filter((request) => request.method === 'vision.analyze')).toHaveLength(1);
    expect(core.requests.find((request) => request.method === 'desktopVisual.executeAction')?.params)
      .not.toHaveProperty('structuredTargetOnly');
    expect(core.submissions[0]).toMatchObject({
      ok: true,
      result: { target: '搜索按钮', verified: true, verificationSource: 'targetRegionVisualChange' },
    });
  });

  it('visually locates a closed-app message editor and verifies ordinary text entry', async () => {
    const core = new FakeCore({
      location: '{"ok":true,"x":600,"y":500,"confidence":0.96,"target":"聊天输入区域"}',
    });
    const capture = new FakeCapture([frame('message-before'), frame('message-after')]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    orchestrator.handle(toolEvent(
      'call_visual_type',
      '{"action":"type","target":"消息输入框","text":"你好，明天下午三点见"}',
    ));
    await core.waitForSubmissions(1);

    expect(core.requests.map((request) => request.method)).toEqual([
      'desktopVisual.getForeground',
      'vision.analyze',
      'desktopVisual.executeAction',
      'conversation.submitToolResult',
    ]);
    expect(core.requests.find((request) => request.method === 'desktopVisual.executeAction')?.params)
      .toMatchObject({ action: 'type', target: '消息输入框', text: '你好，明天下午三点见' });
    expect(core.submissions[0]).toMatchObject({
      ok: true,
      result: { action: 'type', target: '消息输入框', verified: true, state: 'typed' },
    });
  });

  it('stages accepted message text when the screenshot crop misses the visible mutation', async () => {
    const core = new FakeCore({
      location: '{"ok":true,"x":600,"y":500,"confidence":0.96,"target":"聊天输入区域"}',
      executeVerification: 'inputAccepted',
    });
    const capture = new FakeCapture([frame('same-message-frame'), frame('same-message-frame')]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    orchestrator.handle(toolEvent(
      'call_visual_type_crop_miss',
      '{"action":"type","target":"消息输入框","text":"已由用户确认后再发送"}',
    ));
    await core.waitForSubmissions(1);

    expect(core.submissions[0]).toMatchObject({
      ok: true,
      result: {
        action: 'type',
        target: '消息输入框',
        verified: true,
        state: 'typed',
        verificationSource: 'windowsInputAccepted',
      },
    });
  });

  it('does not execute a low-confidence location', async () => {
    const core = new FakeCore({ location: '{"ok":true,"x":400,"y":300,"confidence":0.50,"target":"可能的按钮"}' });
    const capture = new FakeCapture([frame('native-before'), frame('before'), frame('retry-before')]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    orchestrator.handle(toolEvent('call_ambiguous', '{"action":"click","target":"确定按钮"}'));
    await core.waitForSubmissions(1);

    expect(capture.calls).toBe(3);
    expect(core.requests.filter((request) => request.method === 'vision.analyze')).toHaveLength(2);
    expect(core.requests.filter((request) => request.method === 'desktopVisual.executeAction')).toHaveLength(1);
    expect(core.submissions[0]).toMatchObject({ ok: false, error: { code: 'desktop_visual_target_ambiguous' } });
  });

  it('does not execute when the locator describes a different control', async () => {
    const core = new FakeCore({ location: '{"ok":true,"x":400,"y":300,"confidence":0.99,"target":"正文区域"}' });
    const capture = new FakeCapture([frame('native-before'), frame('before'), frame('retry-before')]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    orchestrator.handle(toolEvent('call_mismatch', '{"action":"click","target":"确定按钮"}'));
    await core.waitForSubmissions(1);

    expect(core.requests.filter((request) => request.method === 'desktopVisual.executeAction')).toHaveLength(1);
    expect(core.requests.filter((request) => request.method === 'vision.analyze')).toHaveLength(2);
    expect(core.submissions[0]).toMatchObject({ ok: false, error: { code: 'desktop_visual_target_mismatch' } });
  });

  it('rejects sensitive actions before capturing the screen', async () => {
    const core = new FakeCore();
    const capture = new FakeCapture([]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    orchestrator.handle(toolEvent('call_sensitive', '{"action":"click","target":"立即付款按钮"}'));
    await core.waitForSubmissions(1);

    expect(capture.calls).toBe(0);
    expect(core.requests).toHaveLength(1);
    expect(core.submissions[0]).toMatchObject({ ok: false, error: { code: 'desktop_visual_sensitive_action' } });
  });

  it('returns a failure when Core detects that the foreground window changed', async () => {
    const core = new FakeCore({ executeError: new Error('desktop_visual_target_changed: 前台窗口在截图后发生变化。') });
    const capture = new FakeCapture([frame('native-before'), frame('before')]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    orchestrator.handle(toolEvent('call_changed', '{"action":"click","target":"确定按钮"}'));
    await core.waitForSubmissions(1);

    expect(capture.calls).toBe(1);
    expect(core.submissions[0]).toMatchObject({ ok: false, error: { code: 'desktop_visual_target_changed' } });
  });

  it('stops the chain when the foreground process changes after an action', async () => {
    const core = new FakeCore({ afterProcessName: 'chrome' });
    const capture = new FakeCapture([frame('native-before'), frame('before')]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    orchestrator.handle(toolEvent('call_after_changed', '{"action":"click","target":"确定按钮"}'));
    await core.waitForSubmissions(1);

    expect(capture.calls).toBe(2);
    expect(core.submissions[0]).toMatchObject({
      ok: false,
      error: { code: 'desktop_visual_target_changed_after_action' },
    });
  });

  it('never reports success when the second screenshot has no visible change', async () => {
    const core = new FakeCore();
    const capture = new FakeCapture([frame('native-before'), frame('same'), frame('same')]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    orchestrator.handle(toolEvent('call_unchanged', '{"action":"click","target":"确定按钮"}'));
    await core.waitForSubmissions(1);

    expect(core.requests.filter((request) => request.method === 'vision.analyze')).toHaveLength(1);
    expect(core.submissions[0]).toMatchObject({ ok: false, error: { code: 'desktop_visual_no_visible_change' } });
  });

  it('uses UI Automation evidence for a common menu without a vision round trip', async () => {
    const core = new FakeCore({ executeVerification: 'menuExpanded', nativeAvailable: true });
    const capture = new FakeCapture([frame('before')]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    orchestrator.handle(toolEvent('call_menu', '{"action":"click","target":"编辑"}'));
    await core.waitForSubmissions(1);

    expect(capture.calls).toBe(1);
    expect(core.requests.some((request) => request.method === 'vision.analyze')).toBe(false);
    expect(core.submissions[0]).toMatchObject({
      ok: true,
      result: { verified: true, state: 'menuExpanded', verificationSource: 'uia' },
    });
  });

  it('executes up to six low-risk actions in one model tool round', async () => {
    const core = new FakeCore({ executeVerification: 'menuExpanded', nativeAvailable: true });
    const capture = new FakeCapture([frame('menu-before'), frame('item-before'), frame('item-after')]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    orchestrator.handle(toolEvent('call_batch', JSON.stringify({
      actions: [
        { action: 'click', target: '编辑' },
        { action: 'click', target: '复制' },
      ],
    })));
    await core.waitForSubmissions(1);

    expect(core.requests.filter((request) => request.method === 'desktopVisual.executeAction')).toHaveLength(2);
    expect(core.requests.filter((request) => request.method === 'vision.analyze')).toHaveLength(0);
    expect(core.submissions[0]).toMatchObject({
      ok: true,
      result: { executed: true, verified: true, actionCount: 2, actions: [{ state: 'menuExpanded' }, { state: 'menuExpanded' }] },
    });
  });

  it('stops a third identical action to prevent repeated clicks', async () => {
    const core = new FakeCore();
    const capture = new FakeCapture([
      frame('native-before-1'), frame('before-1'), frame('after-1'),
      frame('native-before-2'), frame('before-2'), frame('after-2'),
      frame('native-before-3'),
    ]);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);
    const argumentsFragment = '{"action":"click","target":"刷新按钮"}';

    orchestrator.handle(toolEvent('repeat_1', argumentsFragment));
    await core.waitForSubmissions(1);
    orchestrator.handle(toolEvent('repeat_2', argumentsFragment));
    await core.waitForSubmissions(2);
    orchestrator.handle(toolEvent('repeat_3', argumentsFragment));
    await core.waitForSubmissions(3);

    expect(core.requests.filter((request) =>
      request.method === 'desktopVisual.executeAction' && request.params.structuredTargetOnly !== true)).toHaveLength(2);
    expect(core.submissions[2]).toMatchObject({ ok: false, error: { code: 'desktop_visual_repeated_action' } });
  });

  it('enforces the eight-action budget for one operation', async () => {
    const core = new FakeCore();
    const captures = Array.from({ length: 24 }, (_, index) => frame(`budget-${index}`));
    const capture = new FakeCapture(captures);
    const orchestrator = new DesktopVisualToolOrchestrator(core as never, capture);

    for (let index = 1; index <= 9; index += 1) {
      orchestrator.handle(toolEvent(`budget_${index}`, `{"action":"click","target":"按钮 ${index}"}`));
      await core.waitForSubmissions(index);
    }

    expect(core.requests.filter((request) =>
      request.method === 'desktopVisual.executeAction' && request.params.structuredTargetOnly !== true)).toHaveLength(8);
    expect(core.submissions[8]).toMatchObject({ ok: false, error: { code: 'desktop_visual_action_budget' } });
  });
});

const OPERATION_ID = '1196ea21-1692-46a8-b87b-92c608429ead';

function toolEvent(id: string, argumentsFragment: string): CoreEvent {
  return {
    schema: 'velia-core-event/v1',
    operationId: OPERATION_ID,
    sequence: 1,
    atUtc: new Date().toISOString(),
    type: 'tool',
    origin: 'text',
    toolCall: { index: 0, id, name: 'desktop_visual', argumentsFragment },
  };
}

function frame(digest: string): DesktopAutomationCapture {
  return {
    imageBase64: 'AA==',
    mimeType: 'image/jpeg',
    digest,
    displayId: '1',
    capturedAtUtc: new Date().toISOString(),
    width: 800,
    height: 600,
    inputBytes: 1,
    displayBounds: { x: 0, y: 0, width: 1600, height: 1200 },
    scaleFactor: 1,
  };
}

class FakeCapture {
  calls = 0;

  constructor(readonly frames: readonly DesktopAutomationCapture[]) {}

  async captureDesktopAutomationFrame(): Promise<DesktopAutomationCapture> {
    return this.#next();
  }

  async captureBoundDesktopAutomationFrame<T>(
    bindTarget: () => Promise<T>,
  ): Promise<{ readonly capture: DesktopAutomationCapture; readonly target: T }> {
    const target = await bindTarget();
    return { capture: this.#next(), target };
  }

  hasTargetRegionChanged(
    before: DesktopAutomationCapture,
    after: DesktopAutomationCapture,
  ): boolean {
    return before.digest !== after.digest;
  }

  #next(): DesktopAutomationCapture {
    const next = this.frames[this.calls];
    this.calls += 1;
    if (!next) throw new Error('Unexpected capture.');
    return next;
  }
}

interface FakeCoreOptions {
  readonly location?: string;
  readonly executeError?: Error;
  readonly executeVerification?: 'inputAccepted' | 'menuExpanded' | 'menuInvoked' | 'uiaInvoked';
  readonly nativeAvailable?: boolean;
  readonly afterProcessName?: string;
}

class FakeCore {
  readonly requests: Array<{ readonly method: RpcMethod; readonly params: Readonly<Record<string, unknown>> }> = [];
  readonly submissions: Array<Readonly<Record<string, unknown>>> = [];
  readonly options: FakeCoreOptions;

  constructor(options: FakeCoreOptions = {}) {
    this.options = options;
  }

  async request<TResult>(method: RpcMethod, params: Record<string, unknown>): Promise<TResult> {
    this.requests.push({ method, params });
    if (method === 'desktopVisual.getForeground') {
      return {
        targetToken: TARGET_TOKEN,
        processId: 123,
        processName: this.options.afterProcessName ?? 'notepad',
        windowTitle: '测试窗口',
        screenX: 400,
        screenY: 300,
        capturedAtUtc: new Date().toISOString(),
      } as TResult;
    }
    if (method === 'vision.analyze') {
      const prompt = String(params.prompt ?? '');
      if (prompt.includes('只读桌面控件定位器')) {
        const target = /目标控件：(.+?)。/u.exec(prompt)?.[1] ?? '确定按钮';
        return { text: this.options.location ?? `{"ok":true,"x":400,"y":300,"confidence":0.95,"target":"${target}"}` } as TResult;
      }
      throw new Error('Unexpected verification request.');
    }
    if (method === 'desktopVisual.executeAction') {
      if (this.options.executeError) throw this.options.executeError;
      if (params.structuredTargetOnly === true && this.options.nativeAvailable !== true) {
        throw new Error('desktop_visual_target_not_found: 没有匹配的 UIA 控件。');
      }
      return {
        operationId: OPERATION_ID,
        action: params.action,
        executed: true,
        screenX: params.screenX,
        screenY: params.screenY,
        processId: 123,
        processName: 'notepad',
        windowTitle: '测试窗口',
        verification: this.options.executeVerification ?? 'inputAccepted',
      } as TResult;
    }
    if (method === 'conversation.submitToolResult') {
      this.submissions.push(params);
      return { operationId: OPERATION_ID, toolCallId: String(params.toolCallId), accepted: true } as TResult;
    }
    throw new Error(`Unexpected method: ${method}`);
  }

  async waitForSubmissions(count: number): Promise<void> {
    const deadline = Date.now() + 3_000;
    while (this.submissions.length < count) {
      if (Date.now() >= deadline) throw new Error(`Timed out waiting for submission ${count}.`);
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
  }
}

const TARGET_TOKEN = '88dbc31f-e8e4-4500-8af5-cad59b87acdf';
