import { captureArticle, capturePage, isHighRiskElement, rebuildRegistry, setEditableValue, type RefRegistry } from './dom';

const CONTENT_VERSION = '0.1.7';
let registry: RefRegistry | undefined;

chrome.runtime.onMessage.addListener((message: unknown, _sender, sendResponse) => {
  void handleMessage(message).then(sendResponse, (error: unknown) => sendResponse({
    ok: false,
    error: error instanceof Error ? error.message : 'Content action failed.',
  }));
  return true;
});

async function handleMessage(message: unknown): Promise<unknown> {
  if (!isRecord(message) || typeof message.type !== 'string') throw new Error('Invalid content message.');
  if (message.type === 'velia.getPage') return pageResult();
  if (message.type === 'velia.getArticle') return articleResult();
  if (message.type !== 'velia.performAction' || !isRecord(message.action)) throw new Error('Unsupported content message.');
  const action = message.action;
  const type = String(action.type ?? '');
  if (type === 'scroll') {
    const position = String(action.position ?? '');
    const amount = clamp(Number(action.amount ?? 600), -4_000, 4_000);
    const evidence = await performVerifiedScroll(position, amount);
    return {
      ok: true,
      contentVersion: CONTENT_VERSION,
      page: lightweightScrollPage(evidence),
      action: { type: 'scroll', position, amount, ...evidence },
    };
  } else if (type === 'play_media' || type === 'pause_media') {
    const index = clamp(Math.trunc(Number(action.index ?? 0)), 0, 15);
    const media = [...document.querySelectorAll('video,audio')][index] as HTMLMediaElement | undefined;
    if (!media) throw new Error('Requested media element was not found.');
    if (type === 'play_media') await media.play(); else media.pause();
  } else {
    const element = resolveRef(String(action.ref ?? ''));
    if (type === 'click') {
      if (isHighRiskElement(element)) {
        return { ok: false, code: 'confirmation_required', error: '该元素可能执行发送、发布、购买、删除或账号操作，当前版本已阻止。' };
      }
      const beforeUrl = location.href;
      const expectedUrl = element instanceof HTMLAnchorElement ? element.href : undefined;
      (element as HTMLElement).focus({ preventScroll: false });
      (element as HTMLElement).click();
      // Respond before a normal link navigation tears down this document and its
      // message channel. The service worker waits for and snapshots the target page.
      return {
        ok: true,
        contentVersion: CONTENT_VERSION,
        action: {
          type: 'click',
          beforeUrl,
          ...(expectedUrl ? { expectedUrl } : {}),
        },
      };
    } else if (type === 'input') {
      (element as HTMLElement).focus({ preventScroll: false });
      setEditableValue(element, String(action.text ?? ''));
    } else {
      throw new Error('Unsupported page action.');
    }
  }
  await new Promise((resolve) => setTimeout(resolve, 80));
  return pageResult();
}

function lightweightScrollPage(state: {
  readonly afterScrollTop: number;
  readonly scrollHeight: number;
  readonly viewportHeight: number;
}): unknown {
  return {
    title: document.title.slice(0, 500),
    url: location.href,
    readyState: document.readyState,
    visibleText: '',
    scrollTop: state.afterScrollTop,
    scrollHeight: state.scrollHeight,
    viewportHeight: state.viewportHeight,
    elements: [],
    media: [],
  };
}

async function performVerifiedScroll(position: string, amount: number): Promise<{
  readonly beforeScrollTop: number;
  readonly afterScrollTop: number;
  readonly scrollHeight: number;
  readonly viewportHeight: number;
  readonly moved: boolean;
  readonly targetReached: boolean;
}> {
  const before = readScrollState();
  let after = before;
  let stableHeight = -1;
  for (let attempt = 0; attempt < 5; attempt += 1) {
    const scroller = document.scrollingElement as HTMLElement | null;
    if (position === 'bottom') {
      if (scroller) scroller.scrollTo({ top: scroller.scrollHeight, behavior: 'instant' });
      else scrollTo({ top: document.documentElement.scrollHeight, behavior: 'instant' });
    } else if (position === 'top') {
      if (scroller) scroller.scrollTo({ top: 0, behavior: 'instant' });
      else scrollTo({ top: 0, behavior: 'instant' });
    } else {
      if (scroller) scroller.scrollBy({ top: amount, behavior: 'instant' });
      else scrollBy({ top: amount, behavior: 'instant' });
    }
    await delay(35);
    after = readScrollState();
    if (position !== 'bottom') break;
    const reached = after.scrollTop + after.viewportHeight >= after.scrollHeight - 3;
    if (reached && stableHeight === after.scrollHeight) break;
    stableHeight = reached ? after.scrollHeight : -1;
  }
  const moved = Math.abs(after.scrollTop - before.scrollTop) >= 1;
  const targetReached = position === 'bottom'
    ? after.scrollTop + after.viewportHeight >= after.scrollHeight - 3
    : position === 'top'
      ? after.scrollTop <= 3
      : amount > 0
        ? after.scrollTop > before.scrollTop || after.scrollTop + after.viewportHeight >= after.scrollHeight - 3
        : after.scrollTop < before.scrollTop || after.scrollTop <= 3;
  return {
    beforeScrollTop: before.scrollTop,
    afterScrollTop: after.scrollTop,
    scrollHeight: after.scrollHeight,
    viewportHeight: after.viewportHeight,
    moved,
    targetReached,
  };
}

function readScrollState(): { readonly scrollTop: number; readonly scrollHeight: number; readonly viewportHeight: number } {
  const scroller = document.scrollingElement as HTMLElement | null;
  return {
    scrollTop: Number(scroller?.scrollTop ?? scrollY ?? 0),
    scrollHeight: Number(scroller?.scrollHeight ?? document.documentElement.scrollHeight),
    viewportHeight: Number(innerHeight),
  };
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

function pageResult(): unknown {
  const captured = capturePage();
  registry = captured.registry;
  return { ok: true, contentVersion: CONTENT_VERSION, page: captured.snapshot };
}

function articleResult(): unknown {
  return { ok: true, contentVersion: CONTENT_VERSION, article: captureArticle() };
}

function resolveRef(ref: string): Element {
  if (!/^v[a-f0-9]{12}$/u.test(ref)) throw new Error('Element reference format is invalid.');
  if (!registry || registry.url !== location.href || !registry.elements.has(ref)) registry = rebuildRegistry();
  const element = registry.elements.get(ref);
  if (!element || !element.isConnected) throw new Error('Referenced element is no longer available.');
  return element;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function clamp(value: number, minimum: number, maximum: number): number {
  if (!Number.isFinite(value)) return minimum;
  return Math.max(minimum, Math.min(maximum, value));
}
