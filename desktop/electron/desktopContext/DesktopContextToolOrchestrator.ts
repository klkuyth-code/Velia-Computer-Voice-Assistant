import type {
  CoreEvent,
  DesktopContextCaptureResult,
  DesktopContextFileOperation,
  DesktopContextFileOperationResult,
  DesktopContextMode,
  DesktopContextReplaceSelectionResult,
  DesktopContextReleaseResult,
  RpcError,
  SubmitToolResultResult,
} from '@velia/protocol';
import type { CoreProcessClient } from '../core/CoreProcessClient.js';

const allowedModes: readonly DesktopContextMode[] = ['auto', 'selection', 'clipboard'];
const MAX_ARGUMENTS_CHARACTERS = 65_536;
const MAX_RESULT_CHARACTERS = 262_144;

export class DesktopContextToolOrchestrator {
  readonly #running = new Set<string>();

  constructor(readonly core: CoreProcessClient) {}

  handle(event: CoreEvent): void {
    if (event.type === 'tool' && event.toolCall?.name === 'desktop_context') void this.#execute(event);
    if (event.type === 'cancelled' || event.type === 'failed' || event.type === 'completed') {
      void this.#release(event.operationId);
    }
  }

  async #execute(event: CoreEvent): Promise<void> {
    const call = event.toolCall;
    if (!call?.id) return;
    const key = `${event.operationId}:${call.id}`;
    if (this.#running.has(key)) return;
    this.#running.add(key);
    let submitted = false;
    try {
      if (typeof call.argumentsFragment !== 'string') {
        throw toolError('invalid_tool_arguments', '桌面上下文参数缺失。');
      }
      const request = parseDesktopContextArguments(call.argumentsFragment);
      const result = request.method === 'capture'
        ? await this.core.request<DesktopContextCaptureResult>(
            'desktopContext.capture',
            { operationId: event.operationId, mode: request.mode },
            10_000,
          )
        : request.method === 'replaceSelection'
          ? await this.core.request<DesktopContextReplaceSelectionResult>(
              'desktopContext.replaceSelection',
              { operationId: event.operationId, contextId: request.contextId, text: request.text },
              15_000,
            )
          : await this.core.request<DesktopContextFileOperationResult>(
              'desktopContext.operateSelectedFiles',
              {
                operationId: event.operationId,
                contextId: request.contextId,
                action: request.action,
                ...(request.newName === undefined ? {} : { newName: request.newName }),
              },
              30_000,
            );
      ensureResultBound(result);
      submitted = await this.#submit(event.operationId, call.id, true, result);
    } catch (error: unknown) {
      submitted = await this.#submit(
        event.operationId,
        call.id,
        false,
        undefined,
        asRpcError(error),
      ).catch(() => false);
    } finally {
      this.#running.delete(key);
    }
    if (!submitted) await this.#release(event.operationId);
  }

  async #submit(
    operationId: string,
    toolCallId: string,
    ok: boolean,
    result?: unknown,
    error?: RpcError,
  ): Promise<boolean> {
    const response = await this.core.request<SubmitToolResultResult>('conversation.submitToolResult', {
      operationId,
      toolCallId,
      ok,
      ...(ok ? { result } : { error }),
    });
    return response.accepted;
  }

  async #release(operationId: string): Promise<void> {
    await this.core.request<DesktopContextReleaseResult>(
      'desktopContext.release',
      { operationId },
    ).catch(() => undefined);
  }
}

type DesktopContextRequest =
  | { readonly method: 'capture'; readonly mode: DesktopContextMode }
  | { readonly method: 'replaceSelection'; readonly contextId: string; readonly text: string }
  | {
      readonly method: 'operateSelectedFiles';
      readonly contextId: string;
      readonly action: DesktopContextFileOperation;
      readonly newName?: string;
    };

function parseDesktopContextArguments(argumentsFragment: string): DesktopContextRequest {
  if (argumentsFragment.length > MAX_ARGUMENTS_CHARACTERS) {
    throw toolError('invalid_tool_arguments', '桌面上下文参数超过安全限制。');
  }
  let parsed: unknown;
  try { parsed = JSON.parse(argumentsFragment) as unknown; }
  catch { throw toolError('invalid_tool_arguments', '桌面上下文参数不是有效 JSON。'); }
  if (!isRecord(parsed) || typeof parsed.method !== 'string') {
    throw invalidDesktopContext();
  }
  if (parsed.method === 'capture' && Object.keys(parsed).length === 2 &&
      typeof parsed.mode === 'string' && allowedModes.includes(parsed.mode as DesktopContextMode)) {
    return { method: 'capture', mode: parsed.mode as DesktopContextMode };
  }
  if (parsed.method === 'replaceSelection' && Object.keys(parsed).length === 3 &&
      typeof parsed.contextId === 'string' && isUuid(parsed.contextId) &&
      typeof parsed.text === 'string' && parsed.text.length >= 1 && parsed.text.length <= 32_768) {
    return { method: 'replaceSelection', contextId: parsed.contextId, text: parsed.text };
  }
  if (parsed.method === 'operateSelectedFiles' && typeof parsed.contextId === 'string' &&
      isUuid(parsed.contextId) && typeof parsed.action === 'string') {
    if ((parsed.action === 'copyToSelectedFolder' || parsed.action === 'moveToSelectedFolder') &&
        Object.keys(parsed).length === 3) {
      return { method: 'operateSelectedFiles', contextId: parsed.contextId, action: parsed.action };
    }
    if (parsed.action === 'renameSelectedFile' && Object.keys(parsed).length === 4 &&
        typeof parsed.newName === 'string' && parsed.newName.length >= 1 && parsed.newName.length <= 240) {
      return {
        method: 'operateSelectedFiles',
        contextId: parsed.contextId,
        action: parsed.action,
        newName: parsed.newName,
      };
    }
  }
  throw invalidDesktopContext();
}

function invalidDesktopContext(): Error & { code: string } {
  return toolError(
    'invalid_desktop_context',
    'desktop_context 参数无效；文件操作不得包含路径、删除或覆盖参数。',
  );
}

function ensureResultBound(result: unknown): void {
  const serialized = JSON.stringify(result);
  if (typeof serialized !== 'string' || serialized.length > MAX_RESULT_CHARACTERS) {
    throw toolError('desktop_context_result_too_large', '桌面上下文结果超过安全限制。');
  }
}

function toolError(code: string, message: string): Error & { code: string } {
  return Object.assign(new Error(message), { code });
}

function asRpcError(error: unknown): RpcError {
  const value = error as { code?: unknown; message?: unknown };
  const rawMessage = typeof value?.message === 'string' ? value.message : '读取桌面上下文失败。';
  const coreFailure = /^([a-z][a-z0-9_]{1,63}):\s*(.+)$/u.exec(rawMessage);
  return {
    code: typeof value?.code === 'string' ? value.code : (coreFailure?.[1] ?? 'desktop_context_failed'),
    message: (coreFailure?.[2] ?? rawMessage).slice(0, 500),
    retryable: false,
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isUuid(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(value);
}
