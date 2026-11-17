import { describe, expect, it, vi } from 'vitest';
import { AppLifecyclePolicy, revealWindow, settleWithin } from './AppLifecyclePolicy';

describe('AppLifecyclePolicy', () => {
  it('hides normal closes and allows only one asynchronous quit cleanup', () => {
    const policy = new AppLifecyclePolicy();

    expect(policy.shouldHideMainWindow()).toBe(true);
    expect(policy.beginQuitCleanup()).toBe(true);
    expect(policy.shouldHideMainWindow()).toBe(false);
    expect(policy.beginQuitCleanup()).toBe(false);
  });

  it('restores, shows, and focuses an existing main window', () => {
    const window = {
      isDestroyed: vi.fn(() => false),
      isMinimized: vi.fn(() => true),
      restore: vi.fn(),
      show: vi.fn(),
      focus: vi.fn(),
    };

    revealWindow(window);

    expect(window.restore).toHaveBeenCalledOnce();
    expect(window.show).toHaveBeenCalledOnce();
    expect(window.focus).toHaveBeenCalledOnce();
  });

  it('does not touch a destroyed window', () => {
    const window = {
      isDestroyed: vi.fn(() => true),
      isMinimized: vi.fn(() => false),
      restore: vi.fn(),
      show: vi.fn(),
      focus: vi.fn(),
    };

    revealWindow(window);

    expect(window.show).not.toHaveBeenCalled();
  });

  it('bounds shutdown cleanup when a dependency does not settle', async () => {
    vi.useFakeTimers();
    try {
      const cleanup = settleWithin([new Promise<void>(() => undefined)], 250);
      await vi.advanceTimersByTimeAsync(250);
      await expect(cleanup).resolves.toBeUndefined();
    } finally {
      vi.useRealTimers();
    }
  });
});
