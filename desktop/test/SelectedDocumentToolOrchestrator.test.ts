import { mkdtemp, rm, stat, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, describe, expect, it } from 'vitest';
import type { CoreEvent, RpcMethod } from '@velia/protocol';
import { SelectedDocumentToolOrchestrator } from '../electron/documents/SelectedDocumentToolOrchestrator.js';

describe('selected document conversation tool', () => {
  const temporaryDirectories: string[] = [];
  afterEach(async () => Promise.all(temporaryDirectories.splice(0).map((path) => rm(path, { recursive: true, force: true }))));

  it('keeps the internal path out of the model result', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'velia-selected-doc-'));
    temporaryDirectories.push(directory);
    const path = join(directory, '测试.txt');
    const contents = '第一行\n第二行';
    await writeFile(path, contents, 'utf8');
    const core = new FakeCore(path, Buffer.byteLength(contents));
    const tool = new SelectedDocumentToolOrchestrator(core as never);

    tool.handle(event('{"contextId":"88dbc31f-e8e4-4500-8af5-cad59b87acdf"}'));
    await core.waitForSubmission();

    expect(core.submission).toMatchObject({
      ok: true,
      result: { name: '测试.txt', kind: 'text', text: contents, evidence: 'selectedDocumentLocalExtraction' },
    });
    expect(JSON.stringify(core.submission)).not.toContain(path);
  });

  it('rejects an invalid context id before resolving a path', async () => {
    const core = new FakeCore('unused', 1);
    const tool = new SelectedDocumentToolOrchestrator(core as never);

    tool.handle(event('{"contextId":"old"}'));
    await core.waitForSubmission();

    expect(core.requests.some((request) => request.method === 'desktopContext.resolveSelectedDocument')).toBe(false);
    expect(core.submission).toMatchObject({ ok: false, error: { code: 'invalid_document_context' } });
  });

  it('never returns a native filesystem path when extraction fails', async () => {
    const missingPath = join(tmpdir(), 'velia-private-location', 'missing.txt');
    const core = new FakeCore(missingPath, 10);
    const tool = new SelectedDocumentToolOrchestrator(core as never);

    tool.handle(event('{"contextId":"88dbc31f-e8e4-4500-8af5-cad59b87acdf"}'));
    await core.waitForSubmission();

    expect(core.submission).toMatchObject({
      ok: false,
      error: { code: 'document_context_failed' },
    });
    expect(JSON.stringify(core.submission)).not.toContain(missingPath);
    expect(JSON.stringify(core.submission)).not.toContain('ENOENT');
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
    toolCall: { index: 0, id: 'document-call', name: 'document_context', argumentsFragment },
  };
}

class FakeCore {
  readonly requests: Array<{ method: RpcMethod; params: Record<string, unknown> }> = [];
  submission: Record<string, unknown> | undefined;

  constructor(private readonly path: string, private readonly size: number) {}

  async request<TResult>(method: RpcMethod, params: Record<string, unknown>): Promise<TResult> {
    this.requests.push({ method, params });
    if (method === 'desktopContext.resolveSelectedDocument') {
      const info = await stat(this.path);
      return {
        operationId: params.operationId,
        contextId: params.contextId,
        name: '测试.txt',
        extension: '.txt',
        sizeBytes: this.size,
        lastWriteTimeUnixMs: Math.trunc(info.mtimeMs),
        creationTimeUnixMs: Math.trunc(info.birthtimeMs),
        internalPath: this.path,
      } as TResult;
    }
    if (method === 'conversation.submitToolResult') {
      this.submission = params;
      return { operationId: params.operationId, toolCallId: params.toolCallId, accepted: true } as TResult;
    }
    throw new Error(`Unexpected method: ${method}`);
  }

  async waitForSubmission(): Promise<void> {
    const deadline = Date.now() + 3_000;
    while (!this.submission) {
      if (Date.now() >= deadline) throw new Error('Timed out waiting for selected document result.');
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
  }
}
