const MAX_VISIBLE_TEXT = 16_000;
const MAX_ARTICLE_TEXT = 32_000;
const MAX_ELEMENTS = 240;
const MAX_LABEL = 240;
const HIGH_RISK_PATTERN = /支付|付款|购买|下单|提交订单|发布|发送|删除|注销|改密|密码|授权|转账|pay|purchase|place order|publish|send|delete account/i;

export interface PageElementSnapshot {
  readonly ref: string;
  readonly tag: string;
  readonly kind?: 'search-result';
  readonly role?: string;
  readonly text: string;
  readonly ariaLabel?: string;
  readonly title?: string;
  readonly alt?: string;
  readonly rel?: string;
  readonly href?: string;
  readonly placeholder?: string;
  readonly value?: string;
  readonly inputType?: string;
  readonly checked?: boolean;
}

export interface PageSnapshot {
  readonly title: string;
  readonly url: string;
  readonly readyState: DocumentReadyState;
  readonly visibleText: string;
  readonly scrollTop: number;
  readonly scrollHeight: number;
  readonly viewportHeight: number;
  readonly elements: readonly PageElementSnapshot[];
  readonly media: readonly {
    readonly index: number;
    readonly paused: boolean;
    readonly ended: boolean;
    readonly currentTime: number;
    readonly duration: number | null;
    readonly readyState: number;
  }[];
}

export interface ArticleSnapshot {
  readonly title: string;
  readonly url: string;
  readonly text: string;
  readonly paragraphCount: number;
  readonly source: 'article' | 'main' | 'semantic' | 'body';
}

export interface RefRegistry {
  readonly url: string;
  readonly elements: ReadonlyMap<string, Element>;
}

const ELEMENT_REF_ATTRIBUTE = 'data-velia-ref';

export function capturePage(documentValue: Document = document): { snapshot: PageSnapshot; registry: RefRegistry } {
  const scroller = documentValue.scrollingElement as HTMLElement | null;
  const selector = [
    'a[href]', 'button', 'input', 'textarea', 'select', 'summary',
    '[role="button"]', '[role="link"]', '[role="textbox"]', '[role="checkbox"]',
    '[role="radio"]', '[role="tab"]', '[contenteditable="true"]',
    '[tabindex]:not([tabindex="-1"])',
  ].join(',');
  const candidates = [...documentValue.querySelectorAll(selector)].filter((element) =>
    isVisible(element) || isRenderedSearchResultTitleLink(element));
  const mediaLinks = candidates.filter((element) =>
    element instanceof HTMLAnchorElement &&
    !isSearchResultTitleLink(element) &&
    /(?:\/video\/|[?&](?:video|v)=)/i.test(element.href));
  const mediaSet = new Set(mediaLinks);
  const ordered = [...mediaLinks, ...candidates.filter((element) => !mediaSet.has(element))].slice(0, MAX_ELEMENTS);
  const registry = new Map<string, Element>();
  const usedRefs = new Set<string>();
  const elements = ordered.map((element, index) => {
    const ref = stableElementRef(element, index, usedRefs);
    registry.set(ref, element);
    return describeElement(ref, element);
  });
  const media = [...documentValue.querySelectorAll('video,audio')].slice(0, 16).map((element, index) => {
    const item = element as HTMLMediaElement;
    return {
      index,
      paused: item.paused,
      ended: item.ended,
      currentTime: finite(item.currentTime),
      duration: Number.isFinite(item.duration) ? item.duration : null,
      readyState: item.readyState,
    };
  });

  return {
    snapshot: {
      title: documentValue.title.slice(0, 500),
      url: documentValue.location.href,
      readyState: documentValue.readyState,
      visibleText: sanitizeText(documentValue.body?.innerText ?? '').slice(0, MAX_VISIBLE_TEXT),
      scrollTop: finite(scroller?.scrollTop ?? documentValue.defaultView?.scrollY ?? 0),
      scrollHeight: finite(scroller?.scrollHeight ?? documentValue.documentElement.scrollHeight),
      viewportHeight: finite(documentValue.defaultView?.innerHeight ?? 0),
      elements,
      media,
    },
    registry: { url: documentValue.location.href, elements: registry },
  };
}

export function captureArticle(documentValue: Document = document): ArticleSnapshot {
  const semanticCandidates = uniqueElements([
    ...documentValue.querySelectorAll('article,main,[role="main"],.article-content,.article-body,.post-content,.entry-content,.content-body,#article-content,#article-body'),
  ]);
  const selected = semanticCandidates
    .map((element) => ({ element, score: articleScore(element) }))
    .filter((candidate) => candidate.score > 0)
    .sort((left, right) => right.score - left.score)[0]?.element ?? documentValue.body;
  const source = selected?.tagName.toLowerCase() === 'article'
    ? 'article'
    : selected?.tagName.toLowerCase() === 'main' || selected?.getAttribute('role') === 'main'
      ? 'main'
      : selected === documentValue.body
        ? 'body'
        : 'semantic';
  const title = sanitizeBlockText(
    selected?.querySelector('h1')?.textContent ?? documentValue.querySelector('h1')?.textContent ?? documentValue.title,
  ).slice(0, 500);
  const paragraphs: string[] = [];
  if (selected) {
    for (const element of selected.querySelectorAll('h1,h2,h3,p,li,blockquote')) {
      if (isArticleNoiseContainer(element, selected)) continue;
      const text = sanitizeBlockText(element.textContent ?? '');
      if (!text || text === title || isArticleNoiseLine(text)) continue;
      if (paragraphs.at(-1) === text) continue;
      paragraphs.push(text);
    }
  }
  const bounded: string[] = [];
  let characters = 0;
  for (const paragraph of paragraphs) {
    const nextLength = characters + paragraph.length + (bounded.length > 0 ? 2 : 0);
    if (nextLength > MAX_ARTICLE_TEXT) break;
    bounded.push(paragraph);
    characters = nextLength;
  }
  return {
    title,
    url: documentValue.location.href.slice(0, 2_048),
    text: bounded.join('\n\n'),
    paragraphCount: bounded.length,
    source,
  };
}

export function rebuildRegistry(documentValue: Document = document): RefRegistry {
  const elements = new Map<string, Element>();
  for (const element of documentValue.querySelectorAll(`[${ELEMENT_REF_ATTRIBUTE}]`)) {
    const ref = element.getAttribute(ELEMENT_REF_ATTRIBUTE);
    if (ref && /^v[a-f0-9]{12}$/u.test(ref) && !elements.has(ref)) elements.set(ref, element);
  }
  return { url: documentValue.location.href, elements };
}

export function isHighRiskElement(element: Element): boolean {
  return HIGH_RISK_PATTERN.test(elementLabel(element));
}

export function setEditableValue(element: Element, value: string): void {
  if (value.length > 32_768) throw new Error('Input text exceeds the safety limit.');
  if (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement) {
    const prototype = element instanceof HTMLInputElement ? HTMLInputElement.prototype : HTMLTextAreaElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(prototype, 'value')?.set;
    if (!setter) throw new Error('Editable value setter is unavailable.');
    setter.call(element, value);
  } else if (element instanceof HTMLElement && element.isContentEditable) {
    element.textContent = value;
  } else {
    throw new Error('Referenced element is not editable.');
  }
  element.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: value }));
  element.dispatchEvent(new Event('change', { bubbles: true }));
}

function describeElement(ref: string, element: Element): PageElementSnapshot {
  const text = elementLabel(element).slice(0, MAX_LABEL);
  const kind = isSearchResultTitleLink(element) ? 'search-result' as const : undefined;
  const role = element.getAttribute('role')?.slice(0, 80);
  const ariaLabel = sanitizeText(element.getAttribute('aria-label') ?? '').slice(0, MAX_LABEL);
  const title = sanitizeText(element.getAttribute('title') ?? '').slice(0, MAX_LABEL);
  const alt = sanitizeText(element.getAttribute('alt') ?? '').slice(0, MAX_LABEL);
  const rel = element.getAttribute('rel')?.slice(0, 80);
  const placeholder = element.getAttribute('placeholder')?.slice(0, 240);
  const href = element instanceof HTMLAnchorElement ? element.href.slice(0, 2_048) : undefined;
  const inputType = element instanceof HTMLInputElement ? element.type.slice(0, 40) : undefined;
  const value = element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement
    ? (element.type === 'password' ? '' : element.value.slice(0, 1_000))
    : undefined;
  const checked = element instanceof HTMLInputElement && ['checkbox', 'radio'].includes(element.type)
    ? element.checked
    : undefined;
  return {
    ref,
    tag: element.tagName.toLowerCase(),
    ...(kind ? { kind } : {}),
    text,
    ...(role ? { role } : {}),
    ...(ariaLabel ? { ariaLabel } : {}),
    ...(title ? { title } : {}),
    ...(alt ? { alt } : {}),
    ...(rel ? { rel } : {}),
    ...(href ? { href } : {}),
    ...(placeholder ? { placeholder } : {}),
    ...(value !== undefined ? { value } : {}),
    ...(inputType ? { inputType } : {}),
    ...(checked !== undefined ? { checked } : {}),
  };
}

function isSearchResultTitleLink(element: Element): boolean {
  if (!(element instanceof HTMLAnchorElement) || !/^https?:\/\//iu.test(element.href)) return false;
  if (!(element.querySelector('h1,h2,h3') || element.closest('h1,h2,h3'))) return false;
  return !hasSponsoredMarker(element) && !hasPaidClickTracking(element.href);
}

function isRenderedSearchResultTitleLink(element: Element): boolean {
  if (!isSearchResultTitleLink(element) || !(element instanceof HTMLElement)) return false;
  const style = getComputedStyle(element);
  if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) return false;
  const rect = element.getBoundingClientRect();
  return rect.width >= 2 && rect.height >= 2;
}

function hasSponsoredMarker(element: Element): boolean {
  let container = element.parentElement;
  for (let depth = 0; container && depth < 5; depth += 1, container = container.parentElement) {
    if (container.querySelectorAll('a[href] h1, a[href] h2, a[href] h3, h1 a[href], h2 a[href], h3 a[href]').length > 1)
      break;
    if (container.hasAttribute('data-text-ad')) return true;
    const markers = [...container.querySelectorAll('[data-text-ad], [aria-label], span, small')];
    if (markers.some((marker) => {
      const label = sanitizeText([
        marker.getAttribute('aria-label'),
        marker.getAttribute('data-text-ad'),
        marker.textContent,
      ].filter(Boolean).join(' '));
      return label.length <= 40 && /^(?:广告|赞助|赞助商链接|sponsored|ad|ads)(?:[\s·:：|-].*)?$/iu.test(label);
    })) return true;
  }
  return false;
}

function hasPaidClickTracking(href: string): boolean {
  try {
    const url = new URL(href);
    if (/(?:^|\.)(?:googleadservices|doubleclick)\.com$/iu.test(url.hostname)) return true;
    return ['gclid', 'dclid', 'gbraid', 'wbraid'].some((name) => url.searchParams.has(name));
  } catch {
    return false;
  }
}

function stableElementRef(element: Element, index: number, usedRefs: Set<string>): string {
  const existing = element.getAttribute(ELEMENT_REF_ATTRIBUTE);
  if (existing && /^v[a-f0-9]{12}$/u.test(existing) && !usedRefs.has(existing)) {
    usedRefs.add(existing);
    return existing;
  }
  const identity = [
    element.tagName.toLowerCase(),
    element.getAttribute('id') ?? '',
    element.getAttribute('name') ?? '',
    element.getAttribute('role') ?? '',
    element.getAttribute('href') ?? '',
    element.getAttribute('type') ?? '',
    element.getAttribute('aria-label') ?? '',
    element.getAttribute('placeholder') ?? '',
    elementLabel(element),
    String(index),
  ].join('\u001f');
  let ref = `v${fnv1a64(identity).slice(0, 12)}`;
  let salt = 0;
  while (usedRefs.has(ref)) {
    salt += 1;
    ref = `v${fnv1a64(`${identity}\u001f${salt}`).slice(0, 12)}`;
  }
  usedRefs.add(ref);
  element.setAttribute(ELEMENT_REF_ATTRIBUTE, ref);
  return ref;
}

function fnv1a64(value: string): string {
  let hash = 0xcbf29ce484222325n;
  for (const character of value) {
    hash ^= BigInt(character.codePointAt(0) ?? 0);
    hash = BigInt.asUintN(64, hash * 0x100000001b3n);
  }
  return hash.toString(16).padStart(16, '0');
}

function elementLabel(element: Element): string {
  return sanitizeText([
    element.getAttribute('aria-label'),
    element.getAttribute('title'),
    element.getAttribute('alt'),
    element.getAttribute('placeholder'),
    element.textContent,
  ].filter(Boolean).join(' '));
}

function sanitizeText(value: string): string {
  return value.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/gu, ' ').replace(/\s+/gu, ' ').trim();
}

function sanitizeBlockText(value: string): string {
  return value
    .replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/gu, ' ')
    .replace(/[\t\r\n ]+/gu, ' ')
    .trim();
}

function uniqueElements(values: readonly Element[]): Element[] {
  return [...new Set(values)];
}

function articleScore(element: Element): number {
  let characters = 0;
  let paragraphs = 0;
  let linkedCharacters = 0;
  const blocks: string[] = [];
  for (const block of element.querySelectorAll('p,li,blockquote')) {
    if (isArticleNoiseContainer(block, element)) continue;
    const text = sanitizeBlockText(block.textContent ?? '');
    if (text.length < 12 || isArticleNoiseLine(text)) continue;
    characters += text.length;
    paragraphs += 1;
    blocks.push(text);
    if (block.closest('a[href]')) linkedCharacters += text.length;
  }
  const repeatedCards = countRepeatedListingCards(blocks);
  const semanticBonus = element.tagName.toLowerCase() === 'article' ? 2_000 : 0;
  const listingPenalty = repeatedCards * 1_500 + linkedCharacters * 2;
  return characters + paragraphs * 80 + semanticBonus - listingPenalty;
}

function countRepeatedListingCards(blocks: readonly string[]): number {
  let count = 0;
  for (let index = 0; index + 2 < blocks.length; index += 1) {
    const combined = normalizeForContainment(blocks[index]!);
    const heading = normalizeForContainment(blocks[index + 1]!);
    const summary = normalizeForContainment(blocks[index + 2]!);
    if (heading.length < 2 || heading.length > 120 || summary.length < 12) continue;
    if (combined.length >= heading.length + summary.length - 4 &&
        combined.includes(heading) && combined.includes(summary)) {
      count += 1;
      index += 2;
    }
  }
  return count;
}

function normalizeForContainment(value: string): string {
  return value.normalize('NFKC').replace(/[\s\p{P}\p{S}]/gu, '');
}

function isArticleNoiseContainer(element: Element, boundary: Element): boolean {
  for (let current: Element | null = element; current && current !== boundary; current = current.parentElement) {
    const tag = current.tagName.toLowerCase();
    if (['nav', 'aside', 'footer', 'form', 'script', 'style', 'noscript'].includes(tag)) return true;
    const role = current.getAttribute('role')?.toLowerCase();
    if (role && ['navigation', 'complementary', 'contentinfo'].includes(role)) return true;
    const identity = `${current.id} ${current.className}`.toLowerCase();
    if (/(?:^|[\s_-])(?:ad|ads|advert|advertisement|promo|promotion|recommend|related|share|social|byline|author|copyright|qrcode)(?:[\s_-]|$)/iu.test(identity)) return true;
  }
  return false;
}

function isArticleNoiseLine(value: string): boolean {
  if (value.length <= 100 && /^(?:广告|推广|赞助|相关推荐|相关阅读|推荐阅读|热门推荐|猜你喜欢|点击关注|扫码关注|关注我们|返回顶部|上一篇|下一篇)(?:[：:].*)?$/iu.test(value)) return true;
  if (value.length <= 160 && /^(?:作者|编辑|来源|记者|文|图|摄影|责任编辑)\s*[:：]/u.test(value)) return true;
  if (value.length <= 200 && /^(?:版权声明|免责声明|转载声明|本文来源|文章来源)\s*[:：]?/u.test(value)) return true;
  return value.length <= 160 && /(?:微信号|公众号)\s*[:：]/u.test(value);
}

function isVisible(element: Element): boolean {
  if (!(element instanceof HTMLElement)) return false;
  const style = getComputedStyle(element);
  if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) return false;
  const rect = element.getBoundingClientRect();
  return rect.width >= 2 && rect.height >= 2 && rect.bottom >= 0 && rect.right >= 0 &&
    rect.top <= innerHeight && rect.left <= innerWidth;
}

function finite(value: number): number {
  return Number.isFinite(value) ? Math.round(value * 100) / 100 : 0;
}
