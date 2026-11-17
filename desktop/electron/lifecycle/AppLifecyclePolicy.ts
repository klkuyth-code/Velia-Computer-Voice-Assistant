export interface RevealableWindow {
  isDestroyed(): boolean;
  isMinimized(): boolean;
  restore(): void;
  show(): void;
  focus(): void;
}

export class AppLifecyclePolicy {
  #quitting = false;
  #cleanupStarted = false;

  shouldHideMainWindow(): boolean { return !this.#quitting; }

  beginQuitCleanup(): boolean {
    this.#quitting = true;
    if (this.#cleanupStarted) return false;
    this.#cleanupStarted = true;
    return true;
  }
}

export function revealWindow(window: RevealableWindow | undefined): void {
  if (!window || window.isDestroyed()) return;
  if (window.isMinimized()) window.restore();
  window.show();
  window.focus();
}

export async function settleWithin(tasks: readonly Promise<unknown>[], timeoutMs: number): Promise<void> {
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) throw new RangeError('Cleanup timeout is invalid.');
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    await Promise.race([
      Promise.allSettled(tasks).then(() => undefined),
      new Promise<void>((resolvePromise) => { timer = setTimeout(resolvePromise, timeoutMs); }),
    ]);
  } finally {
    if (timer) clearTimeout(timer);
  }
}
