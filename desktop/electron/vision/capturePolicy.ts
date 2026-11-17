export interface PixelSize { readonly width: number; readonly height: number }

export function boundedCaptureSize(width: number, height: number, longestEdge = 2048): PixelSize {
  if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) {
    throw new TypeError('Display dimensions are invalid.');
  }
  const scale = Math.min(1, longestEdge / Math.max(width, height));
  return {
    width: Math.max(1, Math.round(width * scale)),
    height: Math.max(1, Math.round(height * scale)),
  };
}

export function selectScreenSource<T extends { readonly display_id: string }>(
  sources: readonly T[],
  displayId: string,
): T {
  const exact = sources.find((source) => source.display_id === displayId);
  if (exact) return exact;
  if (sources.length === 1 && sources[0]?.display_id === '') return sources[0];
  throw new Error('无法安全匹配所选显示器，请刷新显示器列表后重试。');
}

export function selectLiveSource<T extends { readonly id: string; readonly display_id: string }>(
  sources: readonly T[],
  excludedWindowIds: ReadonlySet<string>,
  displayId: string,
): T {
  const externalWindow = sources.find((source) =>
    source.id.startsWith('window:', 0) && !excludedWindowIds.has(source.id));
  if (externalWindow) return externalWindow;
  const exactScreen = sources.find((source) =>
    source.id.startsWith('screen:', 0) && source.display_id === displayId);
  if (exactScreen) return exactScreen;
  const screens = sources.filter((source) => source.id.startsWith('screen:', 0));
  const onlyScreen = screens[0];
  if (screens.length === 1 && onlyScreen) return onlyScreen;
  throw new Error('无法安全匹配当前窗口或鼠标所在屏幕。');
}
