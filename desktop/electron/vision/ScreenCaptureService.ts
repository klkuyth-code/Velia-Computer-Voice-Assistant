import { BrowserWindow, desktopCapturer, nativeImage, screen } from 'electron';
import { createHash } from 'node:crypto';
import type { DesktopVisualAction, VisionCapture, VisionDisplay } from '@velia/protocol';
import { boundedCaptureSize, selectLiveSource, selectScreenSource } from './capturePolicy.js';

const JPEG_QUALITY = 82;
const LIVE_LONGEST_EDGE = 1920;
const LIVE_MAXIMUM_BYTES = 4 * 1024 * 1024;

export interface LiveFrameCapture {
  readonly imageBase64: string;
  readonly mimeType: 'image/jpeg';
  readonly digest: string;
  readonly source: string;
  readonly displayId: string;
  readonly capturedAtUtc: string;
  readonly width: number;
  readonly height: number;
  readonly inputBytes: number;
}

export interface DesktopAutomationCapture extends VisionCapture {
  readonly digest: string;
  readonly displayBounds: { readonly x: number; readonly y: number; readonly width: number; readonly height: number };
  readonly scaleFactor: number;
}

export interface BoundDesktopAutomationCapture<T> {
  readonly capture: DesktopAutomationCapture;
  readonly target: T;
}

export interface CaptureWindowState {
  readonly visible: boolean;
  readonly minimized: boolean;
  readonly focused: boolean;
  readonly alwaysOnTop: boolean;
}

interface CaptureWindowLike {
  isDestroyed(): boolean;
  isVisible(): boolean;
  isMinimized(): boolean;
  isFocused(): boolean;
  isAlwaysOnTop(): boolean;
  hide(): void;
  show(): void;
  showInactive(): void;
  focus(): void;
  minimize(): void;
}

export class ScreenCaptureService {
  listDisplays(): readonly VisionDisplay[] {
    const primaryId = String(screen.getPrimaryDisplay().id);
    return screen.getAllDisplays().map((display, index) => ({
      id: String(display.id),
      name: display.label.trim() || `显示器 ${index + 1}`,
      width: display.size.width,
      height: display.size.height,
      scaleFactor: display.scaleFactor,
      primary: String(display.id) === primaryId,
    }));
  }

  async captureOnce(window: BrowserWindow, displayId: string): Promise<VisionCapture> {
    const display = screen.getAllDisplays().find((candidate) => String(candidate.id) === displayId);
    if (!display) throw new Error('所选显示器已断开，请刷新后重试。');
    const targetSize = boundedCaptureSize(display.size.width, display.size.height);
    const windowState = captureWindowState(window);
    const hideWindow = shouldTemporarilyHide(windowState, true);
    try {
      if (hideWindow) {
        window.hide();
        await delay(200);
      }
      const sources = await desktopCapturer.getSources({
        types: ['screen'],
        thumbnailSize: targetSize,
        fetchWindowIcons: false,
      });
      const source = selectScreenSource(sources, displayId);
      if (source.thumbnail.isEmpty()) throw new Error('Windows 没有返回屏幕画面。');
      const resized = source.thumbnail.resize({ ...targetSize, quality: 'best' });
      const jpeg = resized.toJPEG(JPEG_QUALITY);
      if (jpeg.byteLength === 0 || jpeg.byteLength > 6 * 1024 * 1024) {
        throw new Error('截图为空或超过 6 MB 安全限制。');
      }
      try {
        return {
          imageBase64: jpeg.toString('base64'),
          mimeType: 'image/jpeg',
          displayId,
          capturedAtUtc: new Date().toISOString(),
          width: resized.getSize().width,
          height: resized.getSize().height,
          inputBytes: jpeg.byteLength,
        };
      } finally {
        jpeg.fill(0);
      }
    } finally {
      if (hideWindow) restoreWindowAfterCapture(window, windowState);
    }
  }

  async captureLiveFrame(): Promise<LiveFrameCapture> {
    const targetDisplay = screen.getDisplayNearestPoint(screen.getCursorScreenPoint());
    const targetSize = boundedCaptureSize(targetDisplay.size.width, targetDisplay.size.height, LIVE_LONGEST_EDGE);
    const sources = await desktopCapturer.getSources({
      types: ['window', 'screen'],
      thumbnailSize: targetSize,
      fetchWindowIcons: false,
    });
    const ownWindowIds = new Set(BrowserWindow.getAllWindows()
      .filter((window) => !window.isDestroyed())
      .map((window) => window.getMediaSourceId()));
    const source = selectLiveSource(
      sources.filter((candidate) => !candidate.thumbnail.isEmpty()),
      ownWindowIds,
      String(targetDisplay.id),
    );

    const originalSize = source.thumbnail.getSize();
    const boundedSize = boundedCaptureSize(originalSize.width, originalSize.height, LIVE_LONGEST_EDGE);
    const thumbnail = boundedSize.width === originalSize.width && boundedSize.height === originalSize.height
      ? source.thumbnail
      : source.thumbnail.resize({ ...boundedSize, quality: 'best' });
    const jpeg = thumbnail.toJPEG(JPEG_QUALITY);
    if (jpeg.byteLength < 4 || jpeg.byteLength > LIVE_MAXIMUM_BYTES) {
      jpeg.fill(0);
      throw new Error('实时画面为空或超过 4 MB 安全限制。');
    }
    try {
      const size = thumbnail.getSize();
      return {
        imageBase64: jpeg.toString('base64'),
        mimeType: 'image/jpeg',
        digest: createHash('sha256').update(jpeg).digest('hex'),
        source: source.name.slice(0, 160),
        displayId: String(source.display_id || 'current-window').slice(0, 128),
        capturedAtUtc: new Date().toISOString(),
        width: size.width,
        height: size.height,
        inputBytes: jpeg.byteLength,
      };
    } finally {
      jpeg.fill(0);
    }
  }

  async captureDesktopAutomationFrame(): Promise<DesktopAutomationCapture> {
    return (await this.#captureDesktopAutomationFrame(async () => undefined)).capture;
  }

  hasTargetRegionChanged(
    before: DesktopAutomationCapture,
    after: DesktopAutomationCapture,
    screenX: number,
    screenY: number,
    action: DesktopVisualAction,
    target?: string,
  ): boolean {
    if (before.displayId !== after.displayId ||
        before.displayBounds.x !== after.displayBounds.x ||
        before.displayBounds.y !== after.displayBounds.y) return false;
    const first = nativeImage.createFromBuffer(Buffer.from(before.imageBase64, 'base64'));
    const second = nativeImage.createFromBuffer(Buffer.from(after.imageBase64, 'base64'));
    if (first.isEmpty() || second.isEmpty()) return false;
    const firstSize = first.getSize();
    const secondSize = second.getSize();
    if (firstSize.width !== secondSize.width || firstSize.height !== secondSize.height) return false;
    // Rich chat editors can be much wider than the point returned by the
    // locator: text may render near the left edge while the safe click point
    // is near the center. Sending can clear that editor or add a bubble away
    // from the button. Use the broad window region for those two mutations.
    const broad = action === 'scroll' || action === 'press' || action === 'type' ||
      (action === 'click' && typeof target === 'string' && /发送|发出/iu.test(target));
    const region = broad
      ? {
          x: Math.round(firstSize.width * 0.1),
          y: Math.round(firstSize.height * 0.1),
          width: Math.max(1, Math.round(firstSize.width * 0.8)),
          height: Math.max(1, Math.round(firstSize.height * 0.8)),
        }
      : targetRegion(before, firstSize, screenX, screenY);
    const beforeDigest = createHash('sha256').update(first.crop(region).toBitmap()).digest('hex');
    const afterDigest = createHash('sha256').update(second.crop(region).toBitmap()).digest('hex');
    return beforeDigest !== afterDigest;
  }

  async captureBoundDesktopAutomationFrame<T>(
    bindTarget: () => Promise<T>,
  ): Promise<BoundDesktopAutomationCapture<T>> {
    return this.#captureDesktopAutomationFrame(bindTarget);
  }

  async withOwnWindowsHidden<T>(operation: () => Promise<T>): Promise<T> {
    // Only the compact always-on-top assistant surface can obstruct the user's
    // actual target. Hiding every Velia window made the full settings window
    // disappear and reappear for every before/after verification frame, which
    // looked like the entire app was flashing and also disturbed z-order.
    const hiddenWindows = BrowserWindow.getAllWindows()
      .filter((window) => !window.isDestroyed())
      .map((window) => ({ window, state: captureWindowState(window) }))
      .filter(({ state }) => shouldTemporarilyHide(state, false));
    try {
      for (const { window } of hiddenWindows) window.hide();
      if (hiddenWindows.length > 0) await delay(140);
      return await operation();
    } finally {
      for (const { window, state } of hiddenWindows) {
        restoreWindowAfterCapture(window, state);
      }
    }
  }

  async #captureDesktopAutomationFrame<T>(
    bindTarget: () => Promise<T>,
  ): Promise<BoundDesktopAutomationCapture<T>> {
    return this.withOwnWindowsHidden(async () => {
      const target = await bindTarget();
      const targetDisplay = screen.getDisplayNearestPoint(capturePointForTarget(target));
      const targetSize = boundedCaptureSize(targetDisplay.size.width, targetDisplay.size.height);
      const sources = await desktopCapturer.getSources({
        types: ['screen'],
        thumbnailSize: targetSize,
        fetchWindowIcons: false,
      });
      const source = selectScreenSource(sources, String(targetDisplay.id));
      if (source.thumbnail.isEmpty()) throw new Error('Windows 没有返回可操作的桌面画面。');
      const resized = source.thumbnail.resize({ ...targetSize, quality: 'best' });
      const jpeg = resized.toJPEG(JPEG_QUALITY);
      if (jpeg.byteLength < 4 || jpeg.byteLength > 6 * 1024 * 1024) {
        jpeg.fill(0);
        throw new Error('桌面视觉截图为空或超过 6 MB 安全限制。');
      }
      try {
        const size = resized.getSize();
        return {
          target,
          capture: {
            imageBase64: jpeg.toString('base64'),
            mimeType: 'image/jpeg',
            digest: createHash('sha256').update(jpeg).digest('hex'),
            displayId: String(targetDisplay.id),
            capturedAtUtc: new Date().toISOString(),
            width: size.width,
            height: size.height,
            inputBytes: jpeg.byteLength,
            displayBounds: {
              x: targetDisplay.bounds.x,
              y: targetDisplay.bounds.y,
              width: targetDisplay.bounds.width,
              height: targetDisplay.bounds.height,
            },
            scaleFactor: targetDisplay.scaleFactor,
          },
        };
      } finally {
        jpeg.fill(0);
      }
    });
  }
}

export function captureWindowState(window: CaptureWindowLike): CaptureWindowState {
  return {
    visible: window.isVisible(),
    minimized: window.isMinimized(),
    focused: window.isFocused(),
    alwaysOnTop: window.isAlwaysOnTop(),
  };
}

export function shouldTemporarilyHide(state: CaptureWindowState, explicitlyRequested: boolean): boolean {
  return state.visible && !state.minimized && (explicitlyRequested || state.alwaysOnTop);
}

export function restoreWindowAfterCapture(window: CaptureWindowLike, state: CaptureWindowState): void {
  if (window.isDestroyed()) return;
  if (!state.visible) {
    if (window.isVisible()) window.hide();
    return;
  }
  if (state.minimized) {
    if (!window.isMinimized()) window.minimize();
    return;
  }
  if (state.focused) {
    window.show();
    window.focus();
    return;
  }
  window.showInactive();
}

function capturePointForTarget(target: unknown): Electron.Point {
  if (typeof target === 'object' && target !== null &&
      Number.isFinite((target as { screenX?: unknown }).screenX) &&
      Number.isFinite((target as { screenY?: unknown }).screenY)) {
    return {
      x: Number((target as { screenX: number }).screenX),
      y: Number((target as { screenY: number }).screenY),
    };
  }
  return screen.getCursorScreenPoint();
}

function targetRegion(
  capture: DesktopAutomationCapture,
  imageSize: { readonly width: number; readonly height: number },
  screenX: number,
  screenY: number,
): Electron.Rectangle {
  const scaleX = imageSize.width / capture.displayBounds.width;
  const scaleY = imageSize.height / capture.displayBounds.height;
  const centerX = Math.round((screenX - capture.displayBounds.x) * scaleX);
  const centerY = Math.round((screenY - capture.displayBounds.y) * scaleY);
  const width = Math.min(imageSize.width, Math.max(80, Math.round(320 * scaleX)));
  const height = Math.min(imageSize.height, Math.max(60, Math.round(220 * scaleY)));
  return {
    x: Math.max(0, Math.min(imageSize.width - width, centerX - Math.floor(width / 2))),
    y: Math.max(0, Math.min(imageSize.height - height, centerY - Math.floor(height / 2))),
    width,
    height,
  };
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
