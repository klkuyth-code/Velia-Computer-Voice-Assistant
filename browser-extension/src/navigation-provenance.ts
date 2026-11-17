export type NavigationReturnPoint =
  | { readonly kind: 'same-tab'; readonly previousUrl: string }
  | { readonly kind: 'new-tab'; readonly sourceTabId: number };

const MAX_RETURN_POINTS_PER_TAB = 20;

export function rememberReturnPoint(
  pointsByTab: Map<number, NavigationReturnPoint[]>,
  tabId: number,
  point: NavigationReturnPoint,
): void {
  const existing = pointsByTab.get(tabId) ?? [];
  const last = existing.at(-1);
  if (sameReturnPoint(last, point)) return;
  const next = [...existing, point].slice(-MAX_RETURN_POINTS_PER_TAB);
  pointsByTab.set(tabId, next);
}

export function peekReturnPoint(
  pointsByTab: ReadonlyMap<number, readonly NavigationReturnPoint[]>,
  tabId: number,
): NavigationReturnPoint | undefined {
  return pointsByTab.get(tabId)?.at(-1);
}

export function consumeReturnPoint(
  pointsByTab: Map<number, NavigationReturnPoint[]>,
  tabId: number,
): NavigationReturnPoint | undefined {
  const existing = pointsByTab.get(tabId);
  if (!existing?.length) return undefined;
  const point = existing.at(-1);
  const remaining = existing.slice(0, -1);
  if (remaining.length) pointsByTab.set(tabId, remaining);
  else pointsByTab.delete(tabId);
  return point;
}

export function forgetTabReturnPoints(
  pointsByTab: Map<number, NavigationReturnPoint[]>,
  tabId: number,
): void {
  pointsByTab.delete(tabId);
}

function sameReturnPoint(
  left: NavigationReturnPoint | undefined,
  right: NavigationReturnPoint,
): boolean {
  if (!left || left.kind !== right.kind) return false;
  return left.kind === 'same-tab'
    ? left.previousUrl === (right as Extract<NavigationReturnPoint, { kind: 'same-tab' }>).previousUrl
    : left.sourceTabId === (right as Extract<NavigationReturnPoint, { kind: 'new-tab' }>).sourceTabId;
}
