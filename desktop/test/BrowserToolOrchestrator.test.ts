import { describe, expect, it } from 'vitest';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { CoreEvent, RpcMethod, SubmitToolResultResult } from '@velia/protocol';
import { BrowserToolOrchestrator } from '../electron/browser/BrowserToolOrchestrator.js';
import type { BrowserMethod } from '../electron/browser/BrowserGateway.js';

describe('Browser tool orchestrator', () => {
  it('executes an allowlisted browser request and returns the bounded result to Core', async () => {
    const core = new FakeCore();
    const browser = new FakeBrowser();
    const orchestrator = new BrowserToolOrchestrator(core as never, browser as never);

    orchestrator.handle(toolEvent('browser', '{"method":"getPage","params":{"tabId":7}}'));
    await core.waitForResult();

    expect(browser.calls).toEqual([{ method: 'getPage', params: { tabId: 7 } }]);
    expect(core.lastParams).toMatchObject({ operationId: OPERATION_ID, toolCallId: 'call_1', ok: true });
  });

  it('allows bounded article extraction without affecting normal page snapshots', async () => {
    const core = new FakeCore();
    const browser = new FakeBrowser();
    const orchestrator = new BrowserToolOrchestrator(core as never, browser as never);

    orchestrator.handle(toolEvent('browser', '{"method":"getArticle","params":{}}'));
    await core.waitForResult();

    expect(browser.calls).toEqual([{ method: 'getArticle', params: {} }]);
    expect(core.lastParams).toMatchObject({ operationId: OPERATION_ID, toolCallId: 'call_1', ok: true });
  });

  it('rejects an arbitrary method without sending it to the browser bridge', async () => {
    const core = new FakeCore();
    const browser = new FakeBrowser();
    const orchestrator = new BrowserToolOrchestrator(core as never, browser as never);

    orchestrator.handle(toolEvent('browser', '{"method":"executeJavaScript","params":{}}'));
    await core.waitForResult();

    expect(browser.calls).toHaveLength(0);
    expect(core.lastParams).toMatchObject({ ok: false, error: { code: 'invalid_tool_arguments' } });
  });

  it('returns an actionable correction when act is missing the actions array', async () => {
    const core = new FakeCore();
    const browser = new FakeBrowser();
    const orchestrator = new BrowserToolOrchestrator(core as never, browser as never);

    orchestrator.handle(toolEvent('browser', '{"method":"act","params":{"action":{"type":"click"}}}'));
    await core.waitForResult();

    expect(browser.calls).toHaveLength(0);
    expect(core.lastParams).toMatchObject({
      ok: false,
      error: { code: 'invalid_actions' },
    });
    expect((core.lastParams?.error as { message?: string }).message).toContain('params.actions');
  });

  it('explains that click refs must come from getPage', async () => {
    const core = new FakeCore();
    const browser = new FakeBrowser();
    const orchestrator = new BrowserToolOrchestrator(core as never, browser as never);

    orchestrator.handle(toolEvent('browser', '{"method":"act","params":{"actions":[{"type":"click","selector":"a"}]}}'));
    await core.waitForResult();

    expect(browser.calls).toHaveLength(0);
    expect(core.lastParams).toMatchObject({ ok: false, error: { code: 'invalid_ref' } });
    expect((core.lastParams?.error as { message?: string }).message).toContain('getPage');
  });

  it('ignores tool events owned by another orchestrator', async () => {
    const core = new FakeCore();
    const browser = new FakeBrowser();
    const orchestrator = new BrowserToolOrchestrator(core as never, browser as never);

    orchestrator.handle(toolEvent('desktop_context', '{"method":"capture","mode":"selection"}'));
    await Promise.resolve();

    expect(browser.calls).toHaveLength(0);
    expect(core.lastParams).toBeUndefined();
  });

  it('records only redacted navigation evidence needed to diagnose pagination', async () => {
    const root = await mkdtemp(join(tmpdir(), 'velia-browser-evidence-'));
    try {
      const core = new FakeCore();
      const browser = new FakeNavigationBrowser();
      const logPath = join(root, 'browser-evidence.jsonl');
      const orchestrator = new BrowserToolOrchestrator(core as never, browser as never, logPath);

      orchestrator.handle(toolEvent(
        'browser',
        '{"method":"act","params":{"tabId":7,"actions":[{"type":"navigate","url":"https://www.google.com/search?q=private-search&page=3&utm_source=secret"}]}}',
      ));
      await core.waitForResult();

      const log = await readFile(logPath, 'utf8');
      const record = JSON.parse(log.trim()) as Record<string, unknown>;
      expect(record).toMatchObject({
        schema: 'velia-browser-evidence/v1',
        action: 'navigate',
        outerOk: true,
        resultOk: true,
        targets: [{ host: 'www.google.com', path: '/search', pagination: { page: '3' } }],
      });
      expect(log).not.toContain('private-search');
      expect(log).not.toContain('utm_source');
    } finally {
      await rm(root, { recursive: true, force: true });
    }
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
    toolCall: { index: 0, id: 'call_1', name, argumentsFragment },
  };
}

class FakeCore {
  lastParams: Readonly<Record<string, unknown>> | undefined;
  #submitted = Promise.withResolvers<void>();

  async request<TResult>(method: RpcMethod, params: Record<string, unknown>): Promise<TResult> {
    expect(method).toBe('conversation.submitToolResult');
    this.lastParams = params;
    this.#submitted.resolve();
    return { operationId: OPERATION_ID, toolCallId: 'call_1', accepted: true } as TResult;
  }

  waitForResult(): Promise<void> {
    return this.#submitted.promise;
  }
}

class FakeBrowser {
  calls: Array<{ method: BrowserMethod; params: Readonly<Record<string, unknown>> }> = [];

  async request<TResult>(method: BrowserMethod, params: Readonly<Record<string, unknown>>, signal?: AbortSignal): Promise<TResult> {
    if (signal?.aborted) throw new Error('Browser request was cancelled.');
    this.calls.push({ method, params });
    return { ok: true, page: { title: '测试页' } } as TResult;
  }
}

class FakeNavigationBrowser extends FakeBrowser {
  override async request<TResult>(
    method: BrowserMethod,
    params: Readonly<Record<string, unknown>>,
    signal?: AbortSignal,
  ): Promise<TResult> {
    if (signal?.aborted) throw new Error('Browser request was cancelled.');
    this.calls.push({ method, params });
    if (method === 'listTabs') {
      return {
        tabs: [{ id: 7, active: true, url: 'https://www.google.com/search?q=private-search&page=3' }],
      } as TResult;
    }
    return {
      ok: true,
      steps: [{
        type: 'navigate',
        ok: true,
        toUrl: 'https://www.google.com/search?q=private-search&page=3',
      }],
      finalPage: {
        page: { url: 'https://www.google.com/search?q=private-search&page=3' },
        tab: { id: 7, url: 'https://www.google.com/search?q=private-search&page=3' },
      },
    } as TResult;
  }
}
