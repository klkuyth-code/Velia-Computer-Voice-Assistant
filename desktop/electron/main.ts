import { app, BrowserWindow, ipcMain, Menu, nativeImage, session, Tray } from 'electron';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { BrowserGateway } from './browser/BrowserGateway.js';
import { CoreProcessClient } from './core/CoreProcessClient.js';
import { registerIpc, type DesktopIpcLifecycle } from './ipc.js';
import { AssistantWindowController } from './assistant/AssistantWindowController.js';
import { AppLifecyclePolicy, revealWindow, settleWithin } from './lifecycle/AppLifecyclePolicy.js';
import { FileShortcutDiagnostics } from './voice/ShortcutDiagnostics.js';
import { mainWindowTitleBar } from './windowAppearance.js';

const currentDirectory = dirname(fileURLToPath(import.meta.url));
const core = new CoreProcessClient();
let mainWindow: BrowserWindow | undefined;
let assistant: AssistantWindowController | undefined;
let tray: Tray | undefined;
let ipcLifecycle: DesktopIpcLifecycle | undefined;
let startupComplete = false;
let pendingSecondInstance = false;
const lifecycle = new AppLifecyclePolicy();

app.setName('Velia');
app.setPath('userData', join(app.getPath('appData'), 'Velia'));
const browser = new BrowserGateway(join(app.getPath('userData'), 'browser', 'pairing-token.bin'));
const shortcutDiagnostics = new FileShortcutDiagnostics(join(app.getPath('userData'), 'logs', 'input-shortcuts.jsonl'));

function browserExtensionPath(): string {
  return app.isPackaged
    ? join(process.resourcesPath, 'browser-extension')
    : resolve(currentDirectory, '..', '..', 'browser-extension', 'dist');
}

function createMainWindow(): BrowserWindow {
  assistant?.setMainInputReady(false);
  const window = new BrowserWindow({
    width: 1160,
    height: 760,
    minWidth: 960,
    minHeight: 640,
    show: false,
    backgroundColor: '#062126',
    icon: join(app.getAppPath(), 'build', 'icon.ico'),
    title: 'Velia',
    autoHideMenuBar: true,
    ...mainWindowTitleBar,
    webPreferences: {
      preload: join(currentDirectory, 'preload.cjs'),
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false,
      webSecurity: true,
      allowRunningInsecureContent: false,
      spellcheck: false,
      backgroundThrottling: false,
    },
  });

  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', (event, url) => {
    const current = window.webContents.getURL();
    if (url !== current) {
      event.preventDefault();
    }
  });
  window.once('ready-to-show', () => window.show());
  window.webContents.on('did-start-loading', () => assistant?.setMainInputReady(false));
  window.webContents.on('render-process-gone', () => assistant?.setMainInputReady(false));
  window.on('close', (event) => {
    if (!lifecycle.shouldHideMainWindow()) return;
    event.preventDefault();
    void ipcLifecycle?.stopLiveForPrivacy('main-window-hidden');
    window.hide();
  });
  window.on('closed', () => {
    if (mainWindow === window) mainWindow = undefined;
  });

  const developmentUrl = process.env.VELIA_DEV_SERVER_URL;
  if (developmentUrl) {
    void window.loadURL(developmentUrl);
  } else {
    void window.loadFile(join(currentDirectory, '..', 'dist-renderer', 'index.html'));
  }

  return window;
}

function ensureMainWindow(): BrowserWindow {
  if (mainWindow && !mainWindow.isDestroyed()) return mainWindow;
  mainWindow = createMainWindow();
  return mainWindow;
}

function showMainWindow(): void { revealWindow(ensureMainWindow()); }

function createTrayIcon(): Electron.NativeImage {
  return nativeImage.createFromPath(join(app.getAppPath(), 'build', 'tray-icon.png')).resize({ width: 20, height: 20 });
}

function createTray(): Tray {
  const nextTray = new Tray(createTrayIcon());
  nextTray.setToolTip('Velia');
  nextTray.setContextMenu(Menu.buildFromTemplate([
    { label: '打开 Velia', click: showMainWindow },
    { type: 'separator' },
    { label: '退出 Velia', click: () => app.quit() },
  ]));
  nextTray.on('click', showMainWindow);
  return nextTray;
}

const hasSingleInstanceLock = app.requestSingleInstanceLock();

if (!hasSingleInstanceLock) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (startupComplete) showMainWindow();
    else pendingSecondInstance = true;
  });
  app.whenReady().then(async () => {
    Menu.setApplicationMenu(null);
    const trustedRenderer = (url: string): boolean => url.startsWith('file:', 0) ||
      Boolean(process.env.VELIA_DEV_SERVER_URL && url.startsWith(process.env.VELIA_DEV_SERVER_URL, 0));
    session.defaultSession.setPermissionCheckHandler((webContents, permission, requestingOrigin, details) =>
      permission === 'media' &&
      Boolean(webContents && trustedRenderer(webContents.getURL())) &&
      trustedRenderer(requestingOrigin) &&
      details.mediaType === 'audio');
    session.defaultSession.setPermissionRequestHandler((webContents, permission, callback, details) => {
      const audioOnly = permission === 'media' &&
        'mediaTypes' in details &&
        Array.isArray(details.mediaTypes) &&
        details.mediaTypes.length > 0 &&
        details.mediaTypes.every((type) => type === 'audio');
      callback(audioOnly && trustedRenderer(webContents.getURL()));
    });
    await browser.start();
    assistant = new AssistantWindowController(currentDirectory, ensureMainWindow, shortcutDiagnostics);
    ipcLifecycle = registerIpc(core, browser, browserExtensionPath(), assistant);
    ipcMain.on('velia:voice:input-ready', (event, ready: unknown) => {
      if (typeof ready !== 'boolean' || event.sender !== mainWindow?.webContents) return;
      assistant?.setMainInputReady(ready);
    });
    await core.start();
    mainWindow = createMainWindow();
    assistant.create();
    tray = createTray();
    startupComplete = true;
    if (pendingSecondInstance) {
      pendingSecondInstance = false;
      showMainWindow();
    }

    app.on('activate', showMainWindow);
  }).catch((error: unknown) => {
    console.error(`Velia startup failed: ${error instanceof Error ? error.message : 'unknown error'}`);
    app.quit();
  });

  app.on('before-quit', (event) => {
    if (!lifecycle.beginQuitCleanup()) return;
    event.preventDefault();
    void settleWithin([
      ipcLifecycle?.stopLiveForPrivacy('app-quitting') ?? Promise.resolve(),
      browser.stop(),
    ], 2_500).finally(() => {
      core.stop();
      tray?.destroy();
      tray = undefined;
      app.quit();
    });
  });
}
