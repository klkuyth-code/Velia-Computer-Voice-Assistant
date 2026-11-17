export interface TabHistoryCandidate {
  readonly id?: number | undefined;
  readonly index: number;
}

export function selectPreviousVisibleTab<T extends TabHistoryCandidate>(
  tabs: readonly T[],
  currentTabId: number,
): T | undefined {
  const ordered = tabs
    .filter((tab): tab is T & { id: number } => Number.isInteger(tab.id) && Number(tab.id) > 0)
    .toSorted((left, right) => left.index - right.index);
  const currentIndex = ordered.findIndex((tab) => tab.id === currentTabId);
  if (currentIndex < 0 || ordered.length < 2) return undefined;
  return ordered[currentIndex - 1] ?? ordered[currentIndex + 1];
}
