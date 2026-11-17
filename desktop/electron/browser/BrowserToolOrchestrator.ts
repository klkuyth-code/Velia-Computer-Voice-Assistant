import type { CoreEvent, RpcError, SubmitToolResultResult } from '@velia/protocol';
import { appendFile, mkdir } from 'node:fs/promises';
import { dirname } from 'node:path';
import type { CoreProcessClient } from '../core/CoreProcessClient.js';
import { BrowserGateway, type BrowserMethod } from './BrowserGateway.js';
import { BrowserTaskCoordinator } from './BrowserTaskCoordinator.js';

const allowedMethods: readonly BrowserMethod[] = ['listTabs', 'getPage', 'getArticle', 'open', 'act', 'ping'];
const MAX_ARGUMENTS_CHARACTERS = 65_536;
const MAX_RESULT_CHARACTERS = 262_144;

export class BrowserToolOrchestrator {
  readonly #coordinator: BrowserTaskCoordinator;
  readonly #running = new Map<string, AbortController>();
  #evidenceWrite: Promise<void> = Promise.resolve();

  constructor(
    readonly core: CoreProcessClient,
    readonly browser: BrowserGateway,
    readonly evidenceLogPath?: string,
  ) {
    this.#coordinator = new BrowserTaskCoordinator(browser);
  }

  handle(event: CoreEvent): void {
    if (event.type === 'tool' && event.toolCall?.name === 'browser') void this.#execute(event);
    if (event.type === 'cancelled' || event.type === 'failed' || event.type === 'completed') this.cancel(event.operationId);
  }

  cancel(operationId: string): void {
    for (const [key, controller] of this.#running) {
      if (!key.startsWith(`${operationId}:`, 0)) continue;
      this.#running.delete(key);
      controller.abort();
    }
    this.#coordinator.finish(operationId);
  }

  async #execute(event: CoreEvent): Promise<void> {
    const call = event.toolCall;
    if (!call?.id) return;
    const key = `${event.operationId}:${call.id}`;
    let submitted = false;
    let navigationParams: Readonly<Record<string, unknown>> | undefined;
    try {
      if (typeof call.argumentsFragment !== 'string' || call.argumentsFragment.length > MAX_ARGUMENTS_CHARACTERS) {
        throw toolError('invalid_tool_arguments', '浏览器工具参数超过安全限制。');
      }
      const parsed = JSON.parse(call.argumentsFragment) as unknown;
      if (!isRecord(parsed) || typeof parsed.method !== 'string' || !allowedMethods.includes(parsed.method as BrowserMethod) || !isRecord(parsed.params)) {
        throw toolError('invalid_tool_arguments', '浏览器工具参数格式无效。');
      }
      const method = parsed.method as BrowserMethod;
      validateMethodParams(method, parsed.params);
      if (method === 'act' && hasNavigateAction(parsed.params)) navigationParams = parsed.params;
      const controller = new AbortController();
      this.#running.set(key, controller);
      let result: unknown;
      try {
        const browserStatus = typeof (this.browser as BrowserGateway & { status?: () => { connected: boolean } }).status === 'function'
          ? (this.browser as BrowserGateway & { status: () => { connected: boolean } }).status()
          : undefined;
        if (method !== 'ping' && browserStatus && !browserStatus.connected) {
          throw toolError('browser_bridge_disconnected', 'Chrome 浏览器桥当前未连接。');
        }
        result = await this.#coordinator.execute(
          event.operationId,
          method,
          normalizedParams(method, parsed.params),
          controller.signal,
        );
      } finally {
        this.#running.delete(key);
      }
      ensureResultBound(result);
      if (navigationParams) {
        await this.#recordNavigationEvidence(event.operationId, call.id, navigationParams, true, result);
      }
      submitted = await this.#submit(event.operationId, call.id, true, result);
    } catch (error: unknown) {
      this.#running.delete(key);
      const rpcError = asRpcError(error);
      if (navigationParams) {
        await this.#recordNavigationEvidence(
          event.operationId,
          call.id,
          navigationParams,
          false,
          undefined,
          rpcError,
        );
      }
      submitted = await this.#submit(event.operationId, call.id, false, undefined, rpcError).catch(() => false);
    }
    if (!submitted) this.cancel(event.operationId);
  }

  async #submit(operationId: string, toolCallId: string, ok: boolean, result?: unknown, error?: RpcError): Promise<boolean> {
    const response = await this.core.request<SubmitToolResultResult>('conversation.submitToolResult', {
      operationId,
      toolCallId,
      ok,
      ...(ok ? { result } : { error }),
    });
    return response.accepted;
  }

  async #recordNavigationEvidence(
    operationId: string,
    toolCallId: string,
    params: Readonly<Record<string, unknown>>,
    outerOk: boolean,
    result?: unknown,
    error?: RpcError,
  ): Promise<void> {
    if (!this.evidenceLogPath) return;
    const record = createNavigationEvidenceRecord(operationId, toolCallId, params, outerOk, result, error);
    const write = this.#evidenceWrite.then(async () => {
      await mkdir(dirname(this.evidenceLogPath!), { recursive: true });
      await appendFile(this.evidenceLogPath!, `${JSON.stringify(record)}\n`, 'utf8');
    });
    this.#evidenceWrite = write.catch(() => undefined);
    await write.catch(() => undefined);
  }
}

const paginationKeys = ['page', 'p', 'pn', 'start', 'offset'] as const;
const diagnosticPathSegments = new Set([
  'search', 's', 'web', 'result', 'results', 'page', 'pages', 'index', 'home',
  'user', 'profile', 'video', 'creator', 'center', 'studio',
]);

function hasNavigateAction(params: Readonly<Record<string, unknown>>): boolean {
  return Array.isArray(params.actions) && params.actions.some(
    (value) => isRecord(value) && value.type === 'navigate',
  );
}

function createNavigationEvidenceRecord(
  operationId: string,
  toolCallId: string,
  params: Readonly<Record<string, unknown>>,
  outerOk: boolean,
  result?: unknown,
  error?: RpcError,
): Readonly<Record<string, unknown>> {
  const value = isRecord(result) ? result : undefined;
  const finalPage = value && isRecord(value.finalPage) ? value.finalPage : undefined;
  const page = finalPage && isRecord(finalPage.page) ? finalPage.page : undefined;
  const tab = finalPage && isRecord(finalPage.tab) ? finalPage.tab : undefined;
  const verifiedTab = value && isRecord(value.verifiedTab) ? value.verifiedTab : undefined;
  const actions = Array.isArray(params.actions) ? params.actions : [];
  const targets = actions
    .filter((action): action is Record<string, unknown> => isRecord(action) && action.type === 'navigate')
    .map((action) => sanitizeEvidenceUrl(action.url))
    .filter((url): url is Readonly<Record<string, unknown>> => url !== undefined);
  const steps = value && Array.isArray(value.steps) ? value.steps : [];
  const committed = steps
    .filter((step): step is Record<string, unknown> => isRecord(step) && step.type === 'navigate')
    .map((step) => ({
      from: sanitizeEvidenceUrl(step.fromUrl),
      to: sanitizeEvidenceUrl(step.toUrl),
    }));
  return {
    schema: 'velia-browser-evidence/v1',
    atUtc: new Date().toISOString(),
    operationId,
    toolCallId,
    action: 'navigate',
    targets,
    outerOk,
    resultOk: value?.ok === true,
    committed,
    finalPage: {
      page: sanitizeEvidenceUrl(page?.url),
      tab: sanitizeEvidenceUrl(tab?.url),
    },
    verifiedTab: sanitizeEvidenceUrl(verifiedTab?.url),
    reconciliationDeferred: value?.reconciliationDeferred === true,
    ...(error ? { errorCode: error.code } : {}),
  };
}

function sanitizeEvidenceUrl(value: unknown): Readonly<Record<string, unknown>> | undefined {
  if (typeof value !== 'string' || value.length === 0) return undefined;
  try {
    const url = new URL(value);
    if (!['http:', 'https:'].includes(url.protocol)) return undefined;
    const pagination: Record<string, string> = {};
    for (const key of paginationKeys) {
      const parameter = url.searchParams.get(key);
      if (parameter !== null && /^\d{1,10}$/u.test(parameter)) pagination[key] = parameter;
    }
    return {
      host: url.hostname.toLowerCase(),
      path: sanitizeEvidencePath(url.pathname),
      ...(Object.keys(pagination).length > 0 ? { pagination } : {}),
    };
  } catch {
    return undefined;
  }
}

function sanitizeEvidencePath(pathname: string): string {
  const segments = pathname.split('/').map((segment) => {
    if (!segment) return '';
    return diagnosticPathSegments.has(segment.toLowerCase()) ? segment.toLowerCase() : ':redacted';
  });
  return segments.join('/').slice(0, 1_024) || '/';
}

function normalizedParams(method: BrowserMethod, params: Readonly<Record<string, unknown>>): Readonly<Record<string, unknown>> {
  if (method !== 'open') return params;
  return { url: params.url };
}

function validateMethodParams(method: BrowserMethod, params: Readonly<Record<string, unknown>>): void {
  if (method === 'open') {
    if (typeof params.url !== 'string' || params.url.length > 2_048) throw toolError('invalid_url', '浏览器地址无效。');
    return;
  }
  if (method === 'getPage' || method === 'getArticle') {
    if (params.tabId !== undefined && (!Number.isInteger(params.tabId) || Number(params.tabId) <= 0)) throw toolError('invalid_tab', '标签页标识无效。');
    return;
  }
  if (method === 'act') {
    if (!Array.isArray(params.actions) || params.actions.length < 1 || params.actions.length > 20) {
      throw toolError('invalid_actions', 'act 必须使用 params.actions 数组（1 到 20 项），例如 {"method":"act","params":{"actions":[{"type":"click","ref":"getPage 返回的 ref"}]}}。');
    }
    for (const action of params.actions) validateAction(action);
    if (params.userExplicitTabClose !== undefined && params.userExplicitTabClose !== true) {
      throw toolError('invalid_tab_close_authorization', '标签页关闭授权参数无效。');
    }
    if (params.userExplicitTabClose === true &&
        (params.actions.length !== 1 || !isRecord(params.actions[0]) || params.actions[0].type !== 'close_tab')) {
      throw toolError('invalid_tab_close_authorization', '明确的标签页关闭授权只能用于单个关闭动作。');
    }
    return;
  }
  if (Object.keys(params).length > 0) throw toolError('unexpected_params', '该浏览器方法不接受参数。');
}

function validateAction(value: unknown): void {
  if (!isRecord(value) || typeof value.type !== 'string' ||
      !['click', 'input', 'scroll', 'navigate', 'go_back', 'switch_tab', 'close_tab', 'wait', 'play_media', 'pause_media'].includes(value.type)) {
    throw toolError('unsupported_action', '动作 type 只允许 click、input、scroll、navigate、go_back、switch_tab、close_tab、wait、play_media 或 pause_media。');
  }
  if (value.type === 'navigate' && (typeof value.url !== 'string' || value.url.length > 2_048)) throw toolError('invalid_url', '浏览器地址无效。');
  if (value.type === 'input' && (typeof value.text !== 'string' || value.text.length > 32_768)) throw toolError('invalid_input', '浏览器输入内容无效。');
  if (['click', 'input'].includes(value.type) && (typeof value.ref !== 'string' || !/^v[a-f0-9]{12}$/u.test(value.ref))) {
    throw toolError('invalid_ref', 'click/input 必须使用最近一次 getPage 的 elements.ref（格式 v 加 12 位小写十六进制），不能使用 selector、文字或 elementId。');
  }
  if (value.type === 'close_tab' && value.titleContains !== undefined &&
      (typeof value.titleContains !== 'string' || value.titleContains.trim().length < 1 || value.titleContains.length > 500)) {
    throw toolError('invalid_tab_title', '标签页标题关键词无效。');
  }
}

function ensureResultBound(result: unknown): void {
  const serialized = JSON.stringify(result);
  if (typeof serialized !== 'string' || serialized.length > MAX_RESULT_CHARACTERS) {
    throw toolError('browser_result_too_large', '浏览器返回内容超过安全限制。');
  }
}

function toolError(code: string, message: string): Error & { code: string } {
  return Object.assign(new Error(message), { code });
}

function asRpcError(error: unknown): RpcError {
  const value = error as { code?: unknown; message?: unknown };
  return {
    code: typeof value?.code === 'string' ? value.code : 'browser_action_failed',
    message: typeof value?.message === 'string' ? value.message.slice(0, 500) : '浏览器操作失败。',
    retryable: false,
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
