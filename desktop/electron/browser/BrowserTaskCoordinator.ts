import type { BrowserGateway, BrowserMethod } from './BrowserGateway.js';

interface BrowserTab {
  readonly id: number;
  readonly active?: boolean;
  readonly url?: string;
}

interface TaskState {
  readonly baseline: Set<number>;
  readonly created: Set<number>;
  readonly openedUrls: Set<string>;
}

interface BrowserClient {
  request<TResult>(method: BrowserMethod, params: Readonly<Record<string, unknown>>, signal?: AbortSignal): Promise<TResult>;
}

export class BrowserTaskCoordinator {
  readonly #tasks = new Map<string, TaskState>();

  constructor(readonly client: BrowserClient | BrowserGateway) {}

  finish(taskKey: string): void {
    this.#tasks.delete(taskKey.trim());
  }

  async execute(
    taskKey: string,
    method: BrowserMethod,
    params: Readonly<Record<string, unknown>>,
    signal?: AbortSignal,
  ): Promise<unknown> {
    if (method === 'open') return this.#ensureTarget(taskKey, params, signal);
    if (method === 'act') {
      const state = await this.#state(taskKey, undefined, signal);
      assertTaskOwnedClosures(state, params);
      let result: unknown;
      let failure: unknown;
      try { result = await this.client.request(method, params, signal); } catch (error: unknown) { failure = error; }
      let budget: Record<string, unknown> = { taskNewTabCount: state.created.size, tabBudgetCleanupCount: 0 };
      try {
        budget = await this.#reconcile(state, result, signal);
      } catch (error: unknown) {
        // Navigation, scrolling, history and input actions cannot create a
        // surprise tab. If the action itself returned committed evidence, a
        // transient follow-up listTabs failure must not erase that success.
        // Click remains strict because a site may open a new tab.
        if (failure || !isSuccessfulBrowserResult(result) || !canReturnWithoutReconcile(params)) failure ??= error;
        else budget = { ...budget, reconciliationDeferred: true };
      }
      if (failure) throw failure;
      return { ...(isRecord(result) ? result : {}), ...budget };
    }
    return this.client.request(method, params, signal);
  }

  async #ensureTarget(taskKey: string, params: Readonly<Record<string, unknown>>, signal?: AbortSignal): Promise<unknown> {
    const target = normalizeUrl(params.url);
    if (!target) throw new Error('Browser target URL is invalid.');
    const tabs = await this.#listTabs(signal);
    const state = await this.#state(taskKey, tabs, signal);
    const exact = tabs.find((tab) => normalizeUrl(tab.url) === target);
    if (exact) {
      const result = await this.client.request<Record<string, unknown>>(
        'act',
        { tabId: exact.id, actions: [{ type: 'switch_tab', tabId: exact.id }] },
        signal,
      );
      return { ...result, reusedExistingTab: true };
    }
    if (state.openedUrls.has(target)) return { duplicateOpenSuppressed: true, newTabsCreated: 0 };

    const targetSite = siteKey(target);
    const sameSite = tabs.find((tab) => targetSite && siteKey(tab.url) === targetSite);
    let result: unknown;
    if (sameSite) {
      result = await this.client.request('act', {
        tabId: sameSite.id,
        actions: [{ type: 'switch_tab', tabId: sameSite.id }, { type: 'navigate', url: target }],
      }, signal);
    } else {
      if (state.created.size >= 1) throw new Error('Browser task tab budget exceeded.');
      result = await this.client.request('open', { url: target, newTab: true }, signal);
    }
    state.openedUrls.add(target);
    const budget = await this.#reconcile(state, result, signal);
    return { ...(isRecord(result) ? result : {}), ...budget };
  }

  async #state(taskKey: string, listedTabs?: readonly BrowserTab[], signal?: AbortSignal): Promise<TaskState> {
    const key = taskKey.trim();
    if (!key) throw new Error('Browser task identifier is required.');
    let state = this.#tasks.get(key);
    if (state) return state;
    const tabs = listedTabs ?? await this.#listTabs(signal);
    state = { baseline: new Set(tabs.map((tab) => tab.id)), created: new Set(), openedUrls: new Set() };
    this.#tasks.set(key, state);
    while (this.#tasks.size > 64) this.#tasks.delete(this.#tasks.keys().next().value!);
    return state;
  }

  async #reconcile(state: TaskState, result: unknown, signal?: AbortSignal): Promise<Record<string, unknown>> {
    const tabs = await this.#listTabs(signal);
    const live = new Set(tabs.map((tab) => tab.id));
    for (const tab of tabs) if (!state.baseline.has(tab.id)) state.created.add(tab.id);
    for (const id of [...state.created]) if (!live.has(id)) state.created.delete(id);
    let cleaned = 0;
    const preferred = preferredTabId(result) ?? tabs.find((tab) => tab.active)?.id ?? [...state.created].at(-1);
    if (state.created.size > 1) {
      for (const id of [...state.created]) {
        if (id === preferred) continue;
        await this.client.request('act', { tabId: id, actions: [{ type: 'close_tab', tabId: id }] }, signal);
        state.created.delete(id);
        cleaned += 1;
      }
    }
    if (state.created.size > 1) throw new Error('Browser task tab budget remains exceeded after cleanup.');
    // listTabs is already required for the task-tab budget. Preserve the
    // selected tab as cheap, independent navigation evidence instead of
    // discarding it. This prevents a committed navigation from being marked
    // failed when a site redirects and the earlier finalPage snapshot is stale.
    const verifiedTab = tabs.find((tab) => tab.id === preferred) ?? tabs.find((tab) => tab.active);
    return {
      taskNewTabCount: state.created.size,
      tabBudgetCleanupCount: cleaned,
      ...(verifiedTab ? { verifiedTab } : {}),
    };
  }

  async #listTabs(signal?: AbortSignal): Promise<readonly BrowserTab[]> {
    const result = await this.client.request<unknown>('listTabs', {}, signal);
    if (!isRecord(result) || !Array.isArray(result.tabs)) throw new Error('Browser returned an invalid tab list.');
    return result.tabs.filter(isBrowserTab);
  }
}

function normalizeUrl(value: unknown): string {
  if (typeof value !== 'string') return '';
  try {
    const url = new URL(/^https?:\/\//iu.test(value) ? value : `https://${value}`);
    if (!['http:', 'https:'].includes(url.protocol)) return '';
    url.hash = '';
    return url.toString().replace(/\/$/u, '');
  } catch { return ''; }
}

function siteKey(value: unknown): string {
  const normalized = normalizeUrl(value);
  if (!normalized) return '';
  const host = new URL(normalized).hostname.toLowerCase().replace(/^www\./u, '');
  const labels = host.split('.').filter(Boolean);
  return labels.length <= 2 ? host : labels.slice(-2).join('.');
}

function preferredTabId(result: unknown): number | undefined {
  if (!isRecord(result)) return undefined;
  const finalPage = isRecord(result.finalPage) ? result.finalPage : undefined;
  const tab = finalPage && isRecord(finalPage.tab) ? finalPage.tab : isRecord(result.tab) ? result.tab : undefined;
  return tab && Number.isInteger(Number(tab.id)) ? Number(tab.id) : undefined;
}

function assertTaskOwnedClosures(state: TaskState, params: Readonly<Record<string, unknown>>): void {
  if (!Array.isArray(params.actions)) return;
  const userExplicitTabClose = params.userExplicitTabClose === true;
  for (const value of params.actions) {
    if (!isRecord(value) || value.type !== 'close_tab') continue;
    if (userExplicitTabClose) continue;
    const id = Number(value.tabId ?? params.tabId);
    if (!Number.isInteger(id) || !state.created.has(id)) {
      throw new Error('Velia may close only a tab created by the current browser task.');
    }
  }
}

function canReturnWithoutReconcile(params: Readonly<Record<string, unknown>>): boolean {
  if (!Array.isArray(params.actions) || params.actions.length < 1) return false;
  const noNewTabActions = new Set([
    'navigate', 'go_back', 'scroll', 'input', 'wait', 'play_media', 'pause_media', 'switch_tab', 'close_tab',
  ]);
  return params.actions.every((value) => isRecord(value) &&
    typeof value.type === 'string' && noNewTabActions.has(value.type));
}

function isSuccessfulBrowserResult(value: unknown): boolean {
  return isRecord(value) && value.ok === true;
}

function isBrowserTab(value: unknown): value is BrowserTab {
  return isRecord(value) && Number.isInteger(Number(value.id)) && Number(value.id) > 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
