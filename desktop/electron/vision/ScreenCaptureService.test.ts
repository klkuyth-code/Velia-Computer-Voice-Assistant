import { describe, expect, it, vi } from 'vitest';
import {
  captureWindowState,
  restoreWindowAfterCapture,
  shouldTemporarilyHide,
  type CaptureWindowState,
} from './ScreenCaptureService.js';

function windowStub(state: CaptureWindowState & { destroyed?: boolean }) {
  let visible = state.visible;
  let minimized = state.minimized;
  return {
    isDestroyed: vi.fn(() => state.destroyed ?? false),
    isVisible: vi.fn(() => visible),
    isMinimized: vi.fn(() => minimized),
    isFocused: vi.fn(() => state.focused),
    isAlwaysOnTop: vi.fn(() => state.alwaysOnTop),
    hide: vi.fn(() => { visible = false; }),
    show: vi.fn(() => { visible = true; minimized = false; }),
    showInactive: vi.fn(() => { visible = true; minimized = false; }),
    focus: vi.fn(),
    minimize: vi.fn(() => { minimized = true; }),
  };
}

describe('screen capture window state policy', () => {
  it('hides only a visible always-on-top surface during background automation', () => {
    expect(shouldTemporarilyHide({ visible: true, minimized: false, focused: false, alwaysOnTop: true }, false)).toBe(true);
    expect(shouldTemporarilyHide({ visible: true, minimized: false, focused: false, alwaysOnTop: false }, false)).toBe(false);
    expect(shouldTemporarilyHide({ visible: false, minimized: false, focused: false, alwaysOnTop: true }, false)).toBe(false);
  });

  it('allows an explicitly requested capture to hide a normal visible window', () => {
    expect(shouldTemporarilyHide({ visible: true, minimized: false, focused: true, alwaysOnTop: false }, true)).toBe(true);
  });

  it('restores a previously unfocused window without stealing focus', () => {
    const state = { visible: true, minimized: false, focused: false, alwaysOnTop: true };
    const window = windowStub(state);
    window.hide();
    restoreWindowAfterCapture(window, state);
    expect(window.showInactive).toHaveBeenCalledOnce();
    expect(window.focus).not.toHaveBeenCalled();
  });

  it('restores focus only when the same window owned focus before capture', () => {
    const state = { visible: true, minimized: false, focused: true, alwaysOnTop: false };
    const window = windowStub(state);
    window.hide();
    restoreWindowAfterCapture(window, state);
    expect(window.show).toHaveBeenCalledOnce();
    expect(window.focus).toHaveBeenCalledOnce();
  });

  it('never resurrects a hidden, minimized, or destroyed window', () => {
    const hidden = windowStub({ visible: false, minimized: false, focused: false, alwaysOnTop: true });
    restoreWindowAfterCapture(hidden, captureWindowState(hidden));
    expect(hidden.show).not.toHaveBeenCalled();
    expect(hidden.showInactive).not.toHaveBeenCalled();

    const minimizedState = { visible: true, minimized: true, focused: false, alwaysOnTop: false };
    const minimized = windowStub(minimizedState);
    restoreWindowAfterCapture(minimized, minimizedState);
    expect(minimized.show).not.toHaveBeenCalled();
    expect(minimized.showInactive).not.toHaveBeenCalled();

    const destroyed = windowStub({ visible: true, minimized: false, focused: false, alwaysOnTop: true, destroyed: true });
    restoreWindowAfterCapture(destroyed, { visible: true, minimized: false, focused: false, alwaysOnTop: true });
    expect(destroyed.showInactive).not.toHaveBeenCalled();
  });
});
