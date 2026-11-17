import type {
  CoreEvent,
  DesktopContextResolveSelectedDocumentResult,
  RpcError,
  SubmitToolResultResult,
} from '@velia/protocol';
import type { CoreProcessClient } from '../core/CoreProcessClient.js';
import { extractDocument } from './DocumentExtractor.js';

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu;
const maximumResultCharacters = 262_144;

export class SelectedDocumentToolOrchestrator {
  readonly #running = new Set<string>();

  constructor(private readonly core: Pick<CoreProcessClient, 'request'>) {}

  handle(event: CoreEvent): void {
    if (event.type !== 'tool' || event.toolCall?.name !== 'document_context' || !event.toolCall.id) return;
    const key = `${event.operationId}:${event.toolCall.id}`;
    if (this.#running.has(key)) return;
    this.#running.add(key);
    void this.#execute(event, key);
  }

  async #execute(event: CoreEvent, key: string): Promise<void> {
    const call = event.toolCall!;
    try {
      const contextId = parseContextId(call.argumentsFragment);
      const resolved = await this.core.request<DesktopContextResolveSelectedDocumentResult>(
        'desktopContext.resolveSelectedDocument',
        { operationId: event.operationId, contextId },
        10_000,
      );
      const extracted = await extractDocument(resolved.internalPath, {
        sizeBytes: resolved.sizeBytes,
        lastWriteTimeUnixMs: resolved.lastWriteTimeUnixMs,
        creationTimeUnixMs: resolved.creationTimeUnixMs,
      });
      if (extracted.name.localeCompare(resolved.name, undefined, { sensitivity: 'accent' }) !== 0 ||
          extracted.originalBytes !== resolved.sizeBytes) {
        throw toolError('document_binding_changed', '选中文档在读取期间发生变化，请重新选择。');
      }
      const result = {
        name: extracted.name,
        kind: extracted.kind,
        text: extracted.text,
        originalBytes: extracted.originalBytes,
        ...(extracted.pageCount === undefined ? {} : { pageCount: extracted.pageCount }),
        truncated: extracted.truncated,
        evidence: 'selectedDocumentLocalExtraction',
      };
      if (JSON.stringify(result).length > maximumResultCharacters)
        throw toolError('document_result_too_large', '文档提取结果超过安全限制。');
      await this.#submit(event.operationId, call.id!, true, result);
    } catch (error: unknown) {
      await this.#submit(event.operationId, call.id!, false, undefined, asRpcError(error)).catch(() => undefined);
    } finally {
      this.#running.delete(key);
    }
  }

  async #submit(operationId: string, toolCallId: string, ok: boolean, result?: unknown, error?: RpcError): Promise<void> {
    await this.core.request<SubmitToolResultResult>('conversation.submitToolResult', {
      operationId,
      toolCallId,
      ok,
      ...(ok ? { result } : { error }),
    });
  }
}

function parseContextId(value: string | undefined): string {
  if (typeof value !== 'string' || value.length < 2 || value.length > 256) throw invalidArguments();
  let parsed: unknown;
  try { parsed = JSON.parse(value) as unknown; }
  catch { throw invalidArguments(); }
  if (!isRecord(parsed) || Object.keys(parsed).length !== 1 ||
      typeof parsed.contextId !== 'string' || !uuidPattern.test(parsed.contextId)) throw invalidArguments();
  return parsed.contextId;
}

function invalidArguments(): Error & { code: string } {
  return toolError('invalid_document_context', '选中文档读取参数无效。');
}

function asRpcError(error: unknown): RpcError {
  const value = error as { code?: unknown; message?: unknown };
  const knownToolCode = typeof value?.code === 'string' && /^[a-z][a-z0-9_]{1,63}$/u.test(value.code)
    ? value.code
    : undefined;
  const rawMessage = typeof value?.message === 'string' ? value.message : '';
  const coreFailure = /^([a-z][a-z0-9_]{1,63}):\s*(.+)$/u.exec(rawMessage);
  if (!knownToolCode && !coreFailure) {
    return {
      code: 'document_context_failed',
      message: '选中文档读取失败，文件可能已变化、无权访问或格式无效。',
      retryable: false,
    };
  }
  return {
    code: knownToolCode ?? coreFailure![1]!,
    message: (knownToolCode ? rawMessage : coreFailure![2]!).slice(0, 500),
    retryable: false,
  };
}

function toolError(code: string, message: string): Error & { code: string } {
  return Object.assign(new Error(message), { code });
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
