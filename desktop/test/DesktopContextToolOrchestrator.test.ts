import { describe, expect, it } from 'vitest';
import type { CoreEvent, RpcMethod } from '@velia/protocol';
import { DesktopContextToolOrchestrator } from '../electron/desktopContext/DesktopContextToolOrchestrator.js';

describe('Desktop context tool orchestrator', () => {
  it('captures an allowlisted selection and submits only bounded metadata to Core', async () => {
    const core = new FakeCore();
    const orchestrator = new DesktopContextToolOrchestrator(core as never);

    orchestrator.handle(toolEvent('desktop_context', '{"method":"capture","mode":"selection"}'));
    await core.waitForSubmission();

    expect(core.requests[0]).toEqual({
      method: 'desktopContext.capture',
      params: { operationId: OPERATION_ID, mode: 'selection' },
    });
    expect(core.requests[1]).toMatchObject({
      method: 'conversation.submitToolResult',
      params: { operationId: OPERATION_ID, toolCallId: 'call_context', ok: true },
    });
  });

  it('returns malformed arguments without requesting a desktop capture', async () => {
    const core = new FakeCore();
    const orchestrator = new DesktopContextToolOrchestrator(core as never);

    orchestrator.handle(toolEvent('desktop_context', '{"method":"capture","mode":"shell"}'));
    await core.waitForSubmission();

    expect(core.requests).toHaveLength(1);
    expect(core.requests[0]).toMatchObject({
      method: 'conversation.submitToolResult',
      params: { ok: false, error: { code: 'invalid_desktop_context' } },
    });
  });

  it('replaces only through the context-bound Core method', async () => {
    const core = new FakeCore();
    const orchestrator = new DesktopContextToolOrchestrator(core as never);

    orchestrator.handle(toolEvent(
      'desktop_context',
      '{"method":"replaceSelection","contextId":"2196ea21-1692-46a8-b87b-92c608429ead","text":"新文字"}',
    ));
    await core.waitForSubmission();

    expect(core.requests[0]).toEqual({
      method: 'desktopContext.replaceSelection',
      params: {
        operationId: OPERATION_ID,
        contextId: '2196ea21-1692-46a8-b87b-92c608429ead',
        text: '新文字',
      },
    });
    expect(core.requests[1]).toMatchObject({
      method: 'conversation.submitToolResult',
      params: { ok: true, result: { replaced: true, verified: true, clipboardRestored: true } },
    });
  });

  it('moves selected files only through the context-bound Core method', async () => {
    const core = new FakeCore();
    const orchestrator = new DesktopContextToolOrchestrator(core as never);

    orchestrator.handle(toolEvent(
      'desktop_context',
      '{"method":"operateSelectedFiles","contextId":"2196ea21-1692-46a8-b87b-92c608429ead","action":"moveToSelectedFolder"}',
    ));
    await core.waitForSubmission();

    expect(core.requests[0]).toEqual({
      method: 'desktopContext.operateSelectedFiles',
      params: {
        operationId: OPERATION_ID,
        contextId: '2196ea21-1692-46a8-b87b-92c608429ead',
        action: 'moveToSelectedFolder',
      },
    });
  });

  it('rejects path and overwrite fields before requesting a file operation', async () => {
    const core = new FakeCore();
    const orchestrator = new DesktopContextToolOrchestrator(core as never);

    orchestrator.handle(toolEvent(
      'desktop_context',
      '{"method":"operateSelectedFiles","contextId":"2196ea21-1692-46a8-b87b-92c608429ead","action":"copyToSelectedFolder","destinationPath":"C:\\\\Temp","overwrite":true}',
    ));
    await core.waitForSubmission();

    expect(core.requests).toHaveLength(1);
    expect(core.requests[0]).toMatchObject({
      method: 'conversation.submitToolResult',
      params: { ok: false, error: { code: 'invalid_desktop_context' } },
    });
  });

  it('ignores tool events owned by another orchestrator', async () => {
    const core = new FakeCore();
    const orchestrator = new DesktopContextToolOrchestrator(core as never);

    orchestrator.handle(toolEvent('browser', '{"method":"getPage","params":{}}'));
    await Promise.resolve();

    expect(core.requests).toHaveLength(0);
  });

  it('releases the operation context on a terminal event', async () => {
    const core = new FakeCore();
    const orchestrator = new DesktopContextToolOrchestrator(core as never);

    orchestrator.handle(terminalEvent());
    await core.waitForRelease();

    expect(core.requests).toEqual([
      { method: 'desktopContext.release', params: { operationId: OPERATION_ID } },
    ]);
  });
});

const OPERATION_ID = '1196ea21-1692-46a8-b87b-92c608429ead';

function toolEvent(name: string, argumentsFragment: string): CoreEvent {
  return {
    schema: 'velia-core-event/v1',
    operationId: OPERATION_ID,
    sequence: 1,
    atUtc: new Date().toISOString(),
    type: 'tool',
    origin: 'text',
    toolCall: { index: 0, id: 'call_context', name, argumentsFragment },
  };
}

function terminalEvent(): CoreEvent {
  return {
    schema: 'velia-core-event/v1',
    operationId: OPERATION_ID,
    sequence: 2,
    atUtc: new Date().toISOString(),
    type: 'completed',
    origin: 'text',
  };
}

class FakeCore {
  readonly requests: Array<{ method: RpcMethod; params: Readonly<Record<string, unknown>> }> = [];
  readonly #submitted = Promise.withResolvers<void>();
  readonly #released = Promise.withResolvers<void>();

  async request<TResult>(method: RpcMethod, params: Record<string, unknown>): Promise<TResult> {
    this.requests.push({ method, params });
    if (method === 'desktopContext.capture') {
      return {
        contextId: '2196ea21-1692-46a8-b87b-92c608429ead',
        kind: 'selectedText',
        source: { processName: 'notepad', windowTitle: '测试窗口' },
        clipboardRestored: true,
        expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
        text: '测试文字',
      } as TResult;
    }
    if (method === 'conversation.submitToolResult') {
      this.#submitted.resolve();
      return { operationId: OPERATION_ID, toolCallId: 'call_context', accepted: true } as TResult;
    }
    if (method === 'desktopContext.replaceSelection') {
      return {
        operationId: OPERATION_ID,
        contextId: '2196ea21-1692-46a8-b87b-92c608429ead',
        replaced: true,
        verified: true,
        clipboardRestored: true,
        source: { processName: 'notepad', windowTitle: '测试窗口' },
      } as TResult;
    }
    if (method === 'desktopContext.operateSelectedFiles') {
      return {
        operationId: OPERATION_ID,
        contextId: '2196ea21-1692-46a8-b87b-92c608429ead',
        action: 'moveToSelectedFolder',
        verified: true,
        files: [{ name: 'FILE-A-0816.txt', extension: '.txt', sizeBytes: 4, isDirectory: false }],
        destinationFolder: { name: '目标文件夹', extension: '', isDirectory: true },
      } as TResult;
    }
    if (method === 'desktopContext.release') {
      this.#released.resolve();
      return { operationId: OPERATION_ID, released: true } as TResult;
    }
    throw new Error(`Unexpected method: ${method}`);
  }

  waitForSubmission(): Promise<void> {
    return this.#submitted.promise;
  }

  waitForRelease(): Promise<void> {
    return this.#released.promise;
  }
}
