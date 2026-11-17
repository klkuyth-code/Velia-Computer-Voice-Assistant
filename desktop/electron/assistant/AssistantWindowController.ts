import { app, BrowserWindow, clipboard, screen } from 'electron';
import { join } from 'node:path';
import type { AssistantOverlayCommand, AssistantOverlaySnapshot, InputEvent } from '@velia/protocol';
import { createTimerAlertSnapshot } from './TimerAlertSnapshot.js';
import { noInputShortcutDiagnostics, type InputShortcutDiagnostics } from '../voice/ShortcutDiagnostics.js';
import { LongConversationInputBridge } from '../voice/LongConversationInputBridge.js';
import { compactAssistantSize } from './AssistantOverlayLayout.js';

export class AssistantWindowController {
  #window: BrowserWindow | undefined;
  #snapshot: AssistantOverlaySnapshot = {
    phase: 'idle', status: '按住 F8 说话', transcript: '', answer: '', canStop: false,
    shortcutLabel: 'F8', metrics: {}, updatedAtUtc: new Date(0).toISOString(),
  };
  #compact = true;
  readonly #longConversationInput: LongConversationInputBridge;

  constructor(
    private readonly rendererDirectory: string,
    private readonly getMainWindow: () => BrowserWindow | undefined,
    inputDiagnostics: InputShortcutDiagnostics = noInputShortcutDiagnostics,
  ) {
    this.#longConversationInput = new LongConversationInputBridge(
      (event) => this.#sendToMain('velia:voice:long-conversation', event),
      inputDiagnostics,
    );
  }

  create(): BrowserWindow {
    if (this.#window && !this.#window.isDestroyed()) return this.#window;
    const window = new BrowserWindow({
      ...compactAssistantSize,
      show: false,
      frame: false,
      transparent: true,
      resizable: false,
      maximizable: false,
      minimizable: false,
      fullscreenable: false,
      alwaysOnTop: true,
      skipTaskbar: true,
      hasShadow: false,
      // The voice overlay must never replace the user's active target window.
      // Mouse controls still work, but Windows cannot bind desktop automation to this overlay.
      focusable: false,
      title: 'Velia Assistant',
      backgroundColor: '#00000000',
      webPreferences: {
        preload: join(this.rendererDirectory, 'preload.cjs'),
        sandbox: true,
        contextIsolation: true,
        nodeIntegration: false,
        webSecurity: true,
        allowRunningInsecureContent: false,
        spellcheck: false,
        backgroundThrottling: false,
      },
    });
    this.#window = window;
    window.setIgnoreMouseEvents(false);
    window.setAlwaysOnTop(true, 'floating');
    window.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
    window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    window.webContents.on('page-title-updated', (event) => {
      event.preventDefault();
      window.setTitle('Velia Assistant');
    });
    window.webContents.on('will-navigate', (event, url) => {
      if (url !== window.webContents.getURL()) event.preventDefault();
    });
    window.once('ready-to-show', () => {
      this.#position(window, compactAssistantSize);
      window.showInactive();
    });
    window.on('closed', () => {
      if (this.#window !== window) return;
      this.#window = undefined;
    });
    const developmentUrl = process.env.VELIA_DEV_SERVER_URL;
    if (developmentUrl) {
      void window.loadURL(`${developmentUrl}?view=assistant`);
    } else {
      void window.loadFile(join(this.rendererDirectory, '..', 'dist-renderer', 'index.html'), {
        query: { view: 'assistant' },
      });
    }
    return window;
  }

  publish(snapshot: AssistantOverlaySnapshot): void {
    this.#snapshot = snapshot;
    this.#compact = true;
    this.#applyLayout();
    this.#send('velia:assistant:snapshot', this.state());
  }

  state(): { readonly snapshot: AssistantOverlaySnapshot; readonly compact: boolean } {
    return { snapshot: this.#snapshot, compact: this.#compact };
  }

  notifyTimer(message: string): void {
    this.publish(createTimerAlertSnapshot(this.#snapshot, message));
  }

  forwardInput(event: InputEvent): void {
    if (event.shortcut !== 'voiceLongConversation') {
      this.#sendToMain('velia:voice:push-to-talk', event);
      return;
    }
    this.#longConversationInput.forward(event);
  }

  setMainInputReady(ready: boolean): void {
    this.#longConversationInput.setRendererReady(ready);
  }

  command(command: AssistantOverlayCommand): void {
    if (command === 'collapse') {
      this.#compact = true;
      this.#applyLayout();
      this.#send('velia:assistant:snapshot', this.state());
      return;
    }
    if (command === 'expand') {
      this.#compact = true;
      this.#applyLayout();
      this.#send('velia:assistant:snapshot', this.state());
      return;
    }
    this.#sendToMain('velia:assistant:command', command);
  }

  openMain(): void {
    const window = this.getMainWindow();
    if (!window || window.isDestroyed()) return;
    if (window.isMinimized()) window.restore();
    window.show();
    window.focus();
  }

  copyAnswer(answer: string): void {
    clipboard.writeText(answer);
    if (clipboard.readText() !== answer) throw new Error('Windows clipboard verification failed.');
  }

  quit(): void {
    app.quit();
  }

  #applyLayout(): void {
    const window = this.#window;
    if (!window || window.isDestroyed()) return;
    const size = compactAssistantSize;
    window.setBounds({ ...this.#position(window, size), ...size }, false);
    if (!window.isVisible()) window.showInactive();
  }

  #position(window: BrowserWindow, size: { readonly width: number; readonly height: number }): { x: number; y: number } {
    const display = screen.getDisplayMatching(window.getBounds());
    return {
      x: display.workArea.x + Math.round((display.workArea.width - size.width) / 2),
      y: display.workArea.y + 6,
    };
  }

  #send(channel: string, value: unknown): void {
    const window = this.#window;
    if (!window || window.isDestroyed() || window.webContents.isDestroyed()) return;
    window.webContents.send(channel, value);
  }

  #sendToMain(channel: string, value: unknown): boolean {
    const window = this.getMainWindow();
    if (!window || window.isDestroyed() || window.webContents.isDestroyed()) return false;
    window.webContents.send(channel, value);
    return true;
  }
}
