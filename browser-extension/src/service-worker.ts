import {
  BRIDGE_SCHEMA,
  isBridgeCancel,
  isBridgeRequest,
  isRecord,
  type BridgeHello,
  type BridgeRequest,
  type BridgeResponse,
} from './protocol';
import { matchesNavigationTarget } from './navigation';
import {
  consumeReturnPoint,
  forgetTabReturnPoints,
  peekReturnPoint,
  rememberReturnPoint,
  type NavigationReturnPoint,
} from './navigation-provenance';
import { selectPreviousVisibleTab } from './tab-history';

const MAX_MESSAGE_CHARACTERS = 1_048_576;
const RECONNECT_MIN_MS = 300;
const RECONNECT_MAX_MS = 5_000;
let socket: WebSocket | undefined;
let reconnectTimer: ReturnType<typeof setTimeout> | undefined;
let reconnectDelay = RECONNECT_MIN_MS;
let keepAliveTimer: ReturnType<typeof setInterval> | undefined;
let connected = false;
const cancelledRequests = new Set<string>();
const activeRequests = new Set<string>();
const navigationReturnPoints = new Map<number, NavigationReturnPoint[]>();
const lastKnownTabUrls = new Map<number, string>();
const expectedBackwardNavigations = new Set<number>();

void connect();
void seedKnownTabUrls();
chrome.runtime.onStartup.addListener(() => void connect());
chrome.runtime.onInstalled.addListener(() => void connect());
chrome.tabs.onCreated.addListener((tab) => {
  if (tab.id && tab.url) lastKnownTabUrls.set(tab.id, tab.url);
  if (tab.id && tab.openerTabId) {
    rememberReturnPoint(navigationReturnPoints, tab.id, {
      kind: 'new-tab',
      sourceTabId: tab.openerTabId,
    });
  }
});
chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (!changeInfo.url) return;
  const nextUrl = changeInfo.url;
  const previousUrl = lastKnownTabUrls.get(tabId);
  lastKnownTabUrls.set(tabId, nextUrl);
  if (expectedBackwardNavigations.delete(tabId)) return;
  if (!isTrackableWebUrl(previousUrl) || !isTrackableWebUrl(nextUrl) || previousUrl === nextUrl) return;
  rememberReturnPoint(navigationReturnPoints, tabId, { kind: 'same-tab', previousUrl });
});
chrome.tabs.onRemoved.addListener((tabId) => {
  lastKnownTabUrls.delete(tabId);
  expectedBackwardNavigations.delete(tabId);
  forgetTabReturnPoints(navigationReturnPoints, tabId);
});
chrome.storage.onChanged.addListener((_changes, area) => {
  if (area === 'local') reconnect();
});
chrome.runtime.onMessage.addListener((message: unknown, _sender, sendResponse) => {
  if (!isRecord(message) || typeof message.type !== 'string') return false;
  if (message.type === 'velia.status') {
    sendResponse({ connected });
    return false;
  }
  if (message.type === 'velia.reconnect') {
    reconnect();
    sendResponse({ ok: true });
    return false;
  }
  return false;
});

async function connect(): Promise<void> {
  clearReconnect();
  const settings = await chrome.storage.local.get(['pairingToken', 'gatewayPort']);
  const token = typeof settings.pairingToken === 'string' ? settings.pairingToken.trim() : '';
  const port = validPort(settings.gatewayPort);
  if (token.length < 32 || token.length > 256 || !port) {
    setConnected(false);
    return;
  }

  const next = new WebSocket(`ws://127.0.0.1:${port}`);
  socket = next;
  next.addEventListener('open', () => {
    if (socket !== next) return;
    const hello: BridgeHello = {
      schema: BRIDGE_SCHEMA,
      type: 'hello',
      token,
      extensionVersion: chrome.runtime.getManifest().version,
    };
    next.send(JSON.stringify(hello));
  });
  next.addEventListener('message', (event) => void handleSocketMessage(next, event.data));
  next.addEventListener('close', () => handleSocketClosed(next));
  next.addEventListener('error', () => handleSocketClosed(next));
}

async function handleSocketMessage(current: WebSocket, raw: unknown): Promise<void> {
  if (current !== socket || typeof raw !== 'string' || raw.length > MAX_MESSAGE_CHARACTERS) {
    current.close(1009, 'Invalid bridge message');
    return;
  }
  let message: unknown;
  try {
    message = JSON.parse(raw) as unknown;
  } catch {
    current.close(1007, 'Invalid bridge JSON');
    return;
  }
  if (isRecord(message) && message.schema === BRIDGE_SCHEMA && message.type === 'helloAck') {
    reconnectDelay = RECONNECT_MIN_MS;
    setConnected(true);
    startKeepAlive();
    return;
  }
  if (isBridgeCancel(message)) {
    if (activeRequests.has(message.id)) cancelledRequests.add(message.id);
    return;
  }
  if (!isBridgeRequest(message)) return;
  activeRequests.add(message.id);
  const response = await executeSafely(message);
  if (current.readyState === WebSocket.OPEN) current.send(JSON.stringify(response));
}

async function executeSafely(request: BridgeRequest): Promise<BridgeResponse> {
  try {
    assertNotCancelled(request.id);
    const result = await executeRequest(request);
    assertNotCancelled(request.id);
    return { schema: BRIDGE_SCHEMA, type: 'response', id: request.id, ok: true, result };
  } catch (error: unknown) {
    return {
      schema: BRIDGE_SCHEMA,
      type: 'response',
      id: request.id,
      ok: false,
      error: {
        code: error instanceof BridgeActionError ? error.code : 'browser_action_failed',
        message: error instanceof Error ? error.message.slice(0, 500) : 'Browser action failed.',
      },
    };
  } finally {
    activeRequests.delete(request.id);
    cancelledRequests.delete(request.id);
  }
}

async function executeRequest(request: BridgeRequest): Promise<unknown> {
  switch (request.method) {
    case 'ping':
      return { pong: true, now: new Date().toISOString() };
    case 'listTabs':
      return { tabs: (await chrome.tabs.query({})).map(plainTab) };
    case 'getPage': {
      const tabId = optionalTabId(request.params.tabId) ?? await activeTabId();
      return getPage(tabId);
    }
    case 'getArticle': {
      const tabId = optionalTabId(request.params.tabId) ?? await activeTabId();
      return getArticle(tabId);
    }
    case 'open':
      return openPage(request.params);
    case 'act':
      return executeBatch(request.id, request.params);
  }
}

async function openPage(params: Readonly<Record<string, unknown>>): Promise<unknown> {
  const url = trustedUrl(params.url);
  const newTab = params.newTab === true;
  let tab: chrome.tabs.Tab;
  if (newTab) {
    const source = await activeTab();
    tab = await chrome.tabs.create({ url, active: true });
    if (tab.id && source?.id) {
      rememberReturnPoint(navigationReturnPoints, tab.id, {
        kind: 'new-tab',
        sourceTabId: source.id,
      });
    }
  } else {
    const current = await activeTab();
    if (current?.id) {
      const updated = await chrome.tabs.update(current.id, { url, active: true });
      if (!updated) throw new BridgeActionError('tab_unavailable', 'Browser did not return the updated tab.');
      rememberSameTabReturnPoint(current, url);
      tab = updated;
    } else {
      tab = await chrome.tabs.create({ url, active: true });
    }
  }
  if (!tab.id) throw new BridgeActionError('tab_unavailable', 'Browser did not return a target tab.');
  await focusBrowserWindow(tab.windowId);
  tab = await waitForNavigation(tab.id, url);
  return navigationSnapshot(tab);
}

async function executeBatch(requestId: string, params: Readonly<Record<string, unknown>>): Promise<unknown> {
  const actions = Array.isArray(params.actions) ? params.actions : [];
  if (actions.length < 1 || actions.length > 20) throw new BridgeActionError('invalid_actions', 'Action count is invalid.');
  let tabId = optionalTabId(params.tabId) ?? await activeTabId();
  let navigationOnly = true;
  let clickNavigationCommitted = false;
  let latestPageResult: unknown;
  const steps: unknown[] = [];
  for (const rawAction of actions) {
    assertNotCancelled(requestId);
    if (!isRecord(rawAction) || typeof rawAction.type !== 'string') {
      throw new BridgeActionError('invalid_action', 'Browser action is invalid.');
    }
    const action = rawAction;
    switch (action.type) {
      case 'navigate':
        {
          const targetUrl = trustedUrl(action.url);
          const before = await chrome.tabs.get(tabId);
          const updated = await chrome.tabs.update(tabId, { url: targetUrl, active: true });
          if (!updated) throw new BridgeActionError('tab_unavailable', 'Browser did not return the navigated tab.');
          rememberSameTabReturnPoint(before, targetUrl);
          await focusBrowserWindow(updated.windowId);
          const after = await waitForNavigation(tabId, targetUrl);
          steps.push({
            type: action.type,
            ok: true,
            fromUrl: String(before.url ?? '').slice(0, 2_048),
            toUrl: String(after.url ?? '').slice(0, 2_048),
          });
        }
        break;
      case 'go_back':
        {
          const before = await chrome.tabs.get(tabId);
          let after: chrome.tabs.Tab | undefined;
          expectBackwardNavigation(tabId);
          try {
            await chrome.tabs.goBack(tabId);
            await focusBrowserWindow(before.windowId);
            after = await waitForHistoryNavigation(tabId, String(before.url ?? ''));
            clearExpectedBackwardNavigation(tabId);
            consumeReturnPoint(navigationReturnPoints, tabId);
            steps.push({
              type: action.type,
              ok: true,
              fromUrl: String(before.url ?? '').slice(0, 2_048),
              toUrl: String(after.url ?? '').slice(0, 2_048),
            });
          } catch {
            clearExpectedBackwardNavigation(tabId);
            const fallback = await returnFromRecordedNavigation(before);
            steps.push({ type: action.type, ok: true, ...fallback.step });
            if (fallback.closedTabId) {
              return {
                ok: true,
                steps,
                closedTabId: fallback.closedTabId,
                finalPage: navigationSnapshot(fallback.tab),
              };
            }
            after = fallback.tab;
          }
        }
        break;
      case 'switch_tab':
        tabId = requiredTabId(action.tabId);
        {
          const updated = await chrome.tabs.update(tabId, { active: true });
          if (!updated) throw new BridgeActionError('tab_unavailable', 'Browser did not return the selected tab.');
          await focusBrowserWindow(updated.windowId);
        }
        steps.push({ type: action.type, ok: true, tabId });
        break;
      case 'close_tab':
        {
          const closing = await resolveTabToClose(action, tabId);
          await chrome.tabs.remove(requiredTabId(closing.id));
          forgetTabReturnPoints(navigationReturnPoints, requiredTabId(closing.id));
          steps.push({ type: action.type, ok: true, tabId: closing.id });
          return {
            ok: true,
            steps,
            closedTabId: closing.id,
            closedTabTitle: String(closing.title ?? '').slice(0, 500),
            closedTabUrl: String(closing.url ?? '').slice(0, 2_048),
          };
        }
      case 'wait':
        navigationOnly = false;
        await delay(clamp(Number(action.ms ?? 250), 0, 2_000));
        steps.push({ type: action.type, ok: true });
        break;
      case 'click':
      case 'input':
      case 'scroll':
      case 'play_media':
      case 'pause_media': {
        navigationOnly = false;
        const tabBeforeAction = action.type === 'click' ? await chrome.tabs.get(tabId) : undefined;
        const result = await sendContentAction(tabId, action);
        if (isRecord(result) && result.ok === false) {
          throw new BridgeActionError(
            typeof result.code === 'string' ? result.code : 'page_action_failed',
            typeof result.error === 'string' ? result.error : 'Page action failed.',
          );
        }
        if (isRecord(result) && isRecord(result.page)) latestPageResult = result;
        steps.push({ type: action.type, ok: true, result });
        if (action.type === 'click') {
          const expectedUrl = isRecord(result) && isRecord(result.action) &&
            typeof result.action.expectedUrl === 'string'
            ? result.action.expectedUrl
            : undefined;
          await delay(expectedUrl ? 40 : 120);
          let active = await activeTab();
          if (tabBeforeAction && (expectedUrl ||
              (active && (active.id !== tabBeforeAction.id ||
                String(active.url ?? '') !== String(tabBeforeAction.url ?? ''))))) {
            active = await waitForClickNavigationCommit(tabBeforeAction);
          }
          if (active?.id) tabId = active.id;
          if (active && tabBeforeAction &&
              (active.id !== tabBeforeAction.id || String(active.url ?? '') !== String(tabBeforeAction.url ?? ''))) {
            clickNavigationCommitted = true;
            if (active.id !== tabBeforeAction.id && active.id && tabBeforeAction.id) {
              rememberReturnPoint(navigationReturnPoints, active.id, {
                kind: 'new-tab',
                sourceTabId: tabBeforeAction.id,
              });
            } else if (clickNavigationCommitted) {
              rememberSameTabReturnPoint(tabBeforeAction, String(active.url ?? ''));
            }
          }
        }
        break;
      }
      default:
        throw new BridgeActionError('unsupported_action', `Unsupported browser action: ${action.type}`);
    }
  }
  return {
    ok: true,
    steps,
    finalPage: navigationOnly || clickNavigationCommitted
      ? navigationSnapshot(await chrome.tabs.get(tabId))
      : latestPageResult ?? await getPage(tabId),
  };
}

function assertNotCancelled(requestId: string): void {
  if (cancelledRequests.has(requestId)) {
    throw new BridgeActionError('request_cancelled', 'Browser request was cancelled before its next action.');
  }
}

async function getPage(tabId: number): Promise<unknown> {
  return sendToContent(tabId, { type: 'velia.getPage' });
}

async function getArticle(tabId: number): Promise<unknown> {
  return sendToContent(tabId, { type: 'velia.getArticle' });
}

async function sendContentAction(tabId: number, action: Readonly<Record<string, unknown>>): Promise<unknown> {
  return sendToContent(tabId, { type: 'velia.performAction', action });
}

async function sendToContent(tabId: number, message: unknown): Promise<unknown> {
  try {
    return await chrome.tabs.sendMessage(tabId, message);
  } catch {
    await chrome.scripting.executeScript({ target: { tabId }, files: ['content.js'] });
    return chrome.tabs.sendMessage(tabId, message);
  }
}

async function waitForTab(tabId: number): Promise<void> {
  const deadline = Date.now() + 12_000;
  while (Date.now() < deadline) {
    const tab = await chrome.tabs.get(tabId);
    if (tab.status === 'complete') return;
    await delay(100);
  }
  throw new BridgeActionError('page_load_timeout', 'Page did not finish loading in time.');
}

async function waitForNavigation(tabId: number, targetUrl: string): Promise<chrome.tabs.Tab> {
  const deadline = Date.now() + 800;
  let tab = await chrome.tabs.get(tabId);
  while (Date.now() < deadline && !matchesNavigationTarget(tab.url, targetUrl)) {
    await delay(50);
    tab = await chrome.tabs.get(tabId);
  }
  if (matchesNavigationTarget(tab.url, targetUrl)) {
    await delay(120);
    tab = await chrome.tabs.get(tabId);
  }
  return tab;
}

async function waitForHistoryNavigation(tabId: number, previousUrl: string): Promise<chrome.tabs.Tab> {
  const deadline = Date.now() + 900;
  let tab = await chrome.tabs.get(tabId);
  while (Date.now() < deadline) {
    if (String(tab.url ?? '') !== previousUrl) {
      await delay(80);
      return chrome.tabs.get(tabId);
    }
    await delay(50);
    tab = await chrome.tabs.get(tabId);
  }
  if (String(tab.url ?? '') !== previousUrl) return tab;
  throw new BridgeActionError('browser_history_unverified', 'The browser did not confirm a previous page.');
}

async function waitForClickNavigationCommit(previous: chrome.tabs.Tab): Promise<chrome.tabs.Tab> {
  const previousUrl = String(previous.url ?? '');
  const previousId = requiredTabId(previous.id);
  const deadline = Date.now() + 1_400;
  let tab = previous;
  while (Date.now() < deadline) {
    const active = await activeTab();
    if (active?.id && active.id !== previousId && isTrackableWebUrl(active.url)) {
      await delay(60);
      return chrome.tabs.get(active.id);
    }
    try { tab = await chrome.tabs.get(previousId); }
    catch { return active ?? previous; }
    const currentUrl = String(tab.url ?? '');
    if (currentUrl.length > 0 && currentUrl !== 'about:blank' && currentUrl !== previousUrl) {
      await delay(60);
      return chrome.tabs.get(previousId);
    }
    await delay(50);
  }
  return tab;
}

async function closeTaskTabAndReturn(current: chrome.tabs.Tab): Promise<chrome.tabs.Tab> {
  const currentId = requiredTabId(current.id);
  if (!isUserClosableTab(current)) {
    throw new BridgeActionError('browser_history_empty', 'The current tab has no previous page.');
  }
  const windowTabs = await chrome.tabs.query({ windowId: current.windowId });
  const target = selectPreviousVisibleTab(windowTabs, currentId);
  if (!target?.id) {
    throw new BridgeActionError('browser_history_empty', 'The current tab has no previous page or previous tab.');
  }
  const activated = await chrome.tabs.update(target.id, { active: true });
  if (!activated) throw new BridgeActionError('tab_unavailable', 'The previous browser tab is unavailable.');
  await focusBrowserWindow(activated.windowId);
  await chrome.tabs.remove(currentId);
  forgetTabReturnPoints(navigationReturnPoints, currentId);
  return chrome.tabs.get(target.id);
}

async function returnFromRecordedNavigation(current: chrome.tabs.Tab): Promise<{
  readonly tab: chrome.tabs.Tab;
  readonly closedTabId?: number;
  readonly step: Readonly<Record<string, unknown>>;
}> {
  const currentId = requiredTabId(current.id);
  const point = peekReturnPoint(navigationReturnPoints, currentId);
  if (point?.kind === 'same-tab') {
    const targetUrl = trustedUrl(point.previousUrl);
    expectBackwardNavigation(currentId);
    let restored: chrome.tabs.Tab;
    try {
      const updated = await chrome.tabs.update(currentId, { url: targetUrl, active: true });
      if (!updated) throw new BridgeActionError('tab_unavailable', 'Browser did not restore the previous page.');
      await focusBrowserWindow(updated.windowId);
      restored = await waitForNavigation(currentId, targetUrl);
      if (!matchesNavigationTarget(restored.url, targetUrl)) {
        throw new BridgeActionError('browser_history_unverified', 'The browser did not restore the recorded previous page.');
      }
    } finally {
      clearExpectedBackwardNavigation(currentId);
    }
    consumeReturnPoint(navigationReturnPoints, currentId);
    return {
      tab: restored,
      step: {
        fallback: 'restoredRecordedUrl',
        fromUrl: String(current.url ?? '').slice(0, 2_048),
        toUrl: String(restored.url ?? '').slice(0, 2_048),
      },
    };
  }
  if (point?.kind === 'new-tab') {
    try {
      const source = await chrome.tabs.get(point.sourceTabId);
      const activated = await chrome.tabs.update(point.sourceTabId, { active: true });
      if (!activated) throw new Error('Source tab is unavailable.');
      await focusBrowserWindow(source.windowId);
      await chrome.tabs.remove(currentId);
      consumeReturnPoint(navigationReturnPoints, currentId);
      return {
        tab: await chrome.tabs.get(point.sourceTabId),
        closedTabId: currentId,
        step: {
          fallback: 'closedTrackedNewTab',
          closedTabId: currentId,
          toTabId: point.sourceTabId,
          toUrl: String(source.url ?? '').slice(0, 2_048),
        },
      };
    } catch {
      const fallback = await closeTaskTabAndReturn(current);
      return {
        tab: fallback,
        closedTabId: currentId,
        step: {
          fallback: 'closedTrackedNewTabViaNeighbor',
          closedTabId: currentId,
          toTabId: fallback.id,
          toUrl: String(fallback.url ?? '').slice(0, 2_048),
        },
      };
    }
  }
  throw new BridgeActionError(
    'browser_history_empty',
    'The current tab has no verified previous page; it was left open to avoid closing the wrong tab.');
}

function rememberSameTabReturnPoint(before: chrome.tabs.Tab, targetUrl: string): void {
  const tabId = before.id;
  const previousUrl = String(before.url ?? '');
  if (!tabId || !previousUrl || previousUrl === targetUrl) return;
  if (!/^https?:\/\//iu.test(previousUrl)) return;
  rememberReturnPoint(navigationReturnPoints, tabId, { kind: 'same-tab', previousUrl });
}

async function seedKnownTabUrls(): Promise<void> {
  const tabs = await chrome.tabs.query({});
  for (const tab of tabs) {
    if (tab.id && tab.url) lastKnownTabUrls.set(tab.id, tab.url);
  }
}

function expectBackwardNavigation(tabId: number): void {
  expectedBackwardNavigations.add(tabId);
  setTimeout(() => expectedBackwardNavigations.delete(tabId), 3_000);
}

function clearExpectedBackwardNavigation(tabId: number): void {
  expectedBackwardNavigations.delete(tabId);
}

function isTrackableWebUrl(value: string | undefined): value is string {
  return typeof value === 'string' && /^https?:\/\//iu.test(value);
}

async function resolveTabToClose(
  action: Readonly<Record<string, unknown>>,
  currentTabId: number,
): Promise<chrome.tabs.Tab> {
  const titleQuery = typeof action.titleContains === 'string'
    ? normalizeTabTitle(action.titleContains)
    : '';
  let tab: chrome.tabs.Tab;
  if (!titleQuery) {
    tab = await chrome.tabs.get(requiredTabId(action.tabId ?? currentTabId));
  } else {
    const tabs = (await chrome.tabs.query({})).filter((candidate) => candidate.id && isUserClosableTab(candidate));
    const exact = tabs.filter((candidate) => normalizeTabTitle(candidate.title ?? '') === titleQuery);
    const contains = exact.length > 0 ? exact : tabs.filter((candidate) => {
      const title = normalizeTabTitle(candidate.title ?? '');
      const url = normalizeTabTitle(candidate.url ?? '');
      return title.includes(titleQuery) || url.includes(titleQuery);
    });
    if (contains.length === 0) throw new BridgeActionError('browser_tab_not_found', 'No matching browser tab was found.');
    tab = contains.find((candidate) => candidate.active) ??
      (contains.length === 1 ? contains[0]! : (() => {
        throw new BridgeActionError('browser_tab_ambiguous', 'More than one browser tab matches that title.');
      })());
  }
  if (!isUserClosableTab(tab)) {
    throw new BridgeActionError('browser_tab_not_closable', 'That browser tab cannot be closed by Velia.');
  }
  return tab;
}

function isUserClosableTab(tab: chrome.tabs.Tab): boolean {
  const url = String(tab.url ?? '').toLowerCase();
  return Boolean(tab.id) && !url.startsWith('chrome-extension://');
}

function normalizeTabTitle(value: string): string {
  return value.normalize('NFKC').toLowerCase().replace(/[\s\p{P}\p{S}]+/gu, '');
}

function navigationSnapshot(tab: chrome.tabs.Tab): unknown {
  const url = String(tab.url ?? '').slice(0, 2_048);
  return {
    ok: true,
    verification: 'navigationCommitted',
    tab: plainTab(tab),
    page: {
      title: String(tab.title ?? '').slice(0, 500),
      url,
      readyState: tab.status === 'complete' ? 'complete' : 'loading',
      visibleText: '',
      elements: [],
      media: [],
    },
  };
}

async function focusBrowserWindow(windowId: number): Promise<void> {
  const browserWindow = await chrome.windows.get(windowId);
  await chrome.windows.update(windowId, browserWindow.state === 'minimized'
    ? { focused: true, state: 'normal' }
    : { focused: true });
}

async function activeTab(): Promise<chrome.tabs.Tab | undefined> {
  return (await chrome.tabs.query({ active: true, lastFocusedWindow: true }))[0];
}

async function activeTabId(): Promise<number> {
  const id = (await activeTab())?.id;
  if (!id) throw new BridgeActionError('active_tab_unavailable', 'No active browser tab is available.');
  return id;
}

function plainTab(tab: chrome.tabs.Tab): unknown {
  return {
    id: tab.id,
    windowId: tab.windowId,
    active: tab.active,
    title: String(tab.title ?? '').slice(0, 500),
    url: String(tab.url ?? '').slice(0, 2_048),
    status: tab.status,
  };
}

function trustedUrl(value: unknown): string {
  if (typeof value !== 'string' || value.length > 2_048) throw new BridgeActionError('invalid_url', 'URL is invalid.');
  let url: URL;
  try { url = new URL(value); } catch { throw new BridgeActionError('invalid_url', 'URL is invalid.'); }
  if (!['http:', 'https:'].includes(url.protocol)) throw new BridgeActionError('invalid_url_scheme', 'Only HTTP and HTTPS pages are supported.');
  return url.toString();
}

function optionalTabId(value: unknown): number | undefined {
  const id = Number(value);
  return Number.isInteger(id) && id > 0 ? id : undefined;
}

function requiredTabId(value: unknown): number {
  const id = optionalTabId(value);
  if (!id) throw new BridgeActionError('invalid_tab', 'Tab identifier is invalid.');
  return id;
}

function reconnect(): void {
  socket?.close(1000, 'Reconfigure');
  socket = undefined;
  setConnected(false);
  scheduleReconnect(0);
}

function handleSocketClosed(current: WebSocket): void {
  if (socket !== current) return;
  socket = undefined;
  setConnected(false);
  scheduleReconnect(reconnectDelay);
  reconnectDelay = Math.min(RECONNECT_MAX_MS, reconnectDelay * 2);
}

function scheduleReconnect(milliseconds: number): void {
  clearReconnect();
  reconnectTimer = setTimeout(() => void connect(), milliseconds);
}

function clearReconnect(): void {
  if (reconnectTimer) clearTimeout(reconnectTimer);
  reconnectTimer = undefined;
}

function startKeepAlive(): void {
  if (keepAliveTimer) clearInterval(keepAliveTimer);
  keepAliveTimer = setInterval(() => {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ schema: BRIDGE_SCHEMA, type: 'keepAlive' }));
  }, 20_000);
}

function setConnected(value: boolean): void {
  connected = value;
  void chrome.action.setBadgeText({ text: value ? '✓' : '' });
  void chrome.action.setBadgeBackgroundColor({ color: '#36c9aa' });
}

function validPort(value: unknown): number | undefined {
  const port = Number(value);
  return Number.isInteger(port) && port >= 1_024 && port <= 65_535 ? port : undefined;
}

function clamp(value: number, minimum: number, maximum: number): number {
  if (!Number.isFinite(value)) return minimum;
  return Math.max(minimum, Math.min(maximum, value));
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

class BridgeActionError extends Error {
  constructor(readonly code: string, message: string) { super(message); }
}
