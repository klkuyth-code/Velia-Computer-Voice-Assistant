import { app, BrowserWindow, clipboard, ipcMain, session } from 'electron';
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { WebSocket, type RawData } from 'ws';
import { CoreProcessClient } from './core/CoreProcessClient.js';
import { BrowserGateway } from './browser/BrowserGateway.js';
import { registerIpc } from './ipc.js';
import { mainWindowTitleBar } from './windowAppearance.js';

const currentDirectory = dirname(fileURLToPath(import.meta.url));
const desktopRoot = resolve(currentDirectory, '..');
const artifactsDirectory = join(desktopRoot, 'artifacts');
const outputPath = join(artifactsDirectory, 'ui-smoke.png');
const overviewOutputPath = join(artifactsDirectory, 'ui-overview-smoke.png');
const modelsOutputPath = join(artifactsDirectory, 'ui-models-smoke.png');
const historyOutputPath = join(artifactsDirectory, 'ui-history-smoke.png');
const browserOutputPath = join(artifactsDirectory, 'ui-browser-smoke.png');
const visionOutputPath = join(artifactsDirectory, 'ui-vision-smoke.png');
const liveOutputPath = join(artifactsDirectory, 'ui-live-smoke.png');
const documentsOutputPath = join(artifactsDirectory, 'ui-documents-smoke.png');
const voiceOutputPath = join(artifactsDirectory, 'ui-voice-smoke.png');
const voiceMenuOutputPath = join(artifactsDirectory, 'ui-voice-menu-smoke.png');
const asrOutputPath = join(artifactsDirectory, 'ui-asr-smoke.png');
const browserConfigOutputPath = join(artifactsDirectory, 'ui-browser-config-smoke.png');
const assistantOutputPath = join(artifactsDirectory, 'ui-assistant-smoke.png');
const assistantAnsweringOutputPath = join(artifactsDirectory, 'ui-assistant-answering-smoke.png');
process.env.VELIA_SMOKE_TEST = '1';
process.env.VELIA_TEST_DATA_DIR = join(artifactsDirectory, 'smoke-core-data', String(process.pid));
const core = new CoreProcessClient();
const browser = new BrowserGateway(join(artifactsDirectory, `browser-smoke-token-${process.pid}.bin`), 47_840);
app.setName('Velia UI Smoke');
app.setPath('userData', join(artifactsDirectory, `smoke-user-data-${process.pid}`));
const startupWatchdog = setTimeout(() => {
  console.error('SMOKE_STARTUP_TIMEOUT');
  core.stop();
  app.exit(3);
}, 20_000);

async function runSmoke(): Promise<void> {
  const previousClipboardText = clipboard.readText();
  try {
  clearTimeout(startupWatchdog);
  console.log('SMOKE_APP_READY');
  session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false));
  await browser.start();
  await verifyBrowserGatewayRoundTrip();
  registerIpc(core, browser, resolve(desktopRoot, '..', 'browser-extension', 'dist'));
  console.log('SMOKE_STARTING_CORE');
  await core.start();
  console.log('SMOKE_CORE_READY');

  const window = new BrowserWindow({
    width: 1160,
    height: 760,
    show: false,
    backgroundColor: '#062126',
    ...mainWindowTitleBar,
    webPreferences: {
      preload: join(currentDirectory, 'preload.cjs'),
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false,
      webSecurity: true,
      backgroundThrottling: false,
    },
  });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  console.log('SMOKE_LOADING_RENDERER');
  await window.loadFile(join(desktopRoot, 'dist-renderer', 'index.html'));
  console.log('SMOKE_RENDERER_LOADED');
  await window.webContents.executeJavaScript('document.fonts.ready');
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 500));
  window.showInactive();
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 200));

  const bodyText = await window.webContents.executeJavaScript('document.body.innerText');
  console.log('SMOKE_RENDERER_TEXT_READY');
  const rendererHealth = await window.webContents.executeJavaScript('window.velia.system.health()');
  if (!isRecord(rendererHealth)) {
    throw new Error('Renderer did not establish the Velia Core connection.');
  }
  if (typeof bodyText !== 'string' || !bodyText.includes('意识中心') || !bodyText.includes('服务状态')) {
    throw new Error('Renderer did not load the Velia control center.');
  }
  const gateOrbReady = await window.webContents.executeJavaScript(`(() => {
    const node = document.querySelector('.velia-life-orb__surface img');
    return node instanceof HTMLImageElement && node.complete && node.naturalWidth > 0;
  })()`);
  if (gateOrbReady !== true) {
    throw new Error('Renderer did not load the approved Velia life-orb asset.');
  }
  const assistantWindow = new BrowserWindow({
    width: 118,
    height: 112,
    show: false,
    frame: false,
    transparent: true,
    webPreferences: {
      preload: join(currentDirectory, 'preload.cjs'),
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false,
      webSecurity: true,
      backgroundThrottling: false,
    },
  });
  await assistantWindow.loadFile(join(desktopRoot, 'dist-renderer', 'index.html'), { query: { view: 'assistant' } });
  await assistantWindow.webContents.executeJavaScript('document.fonts.ready');
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 150));
  const assistantCompact = await assistantWindow.webContents.executeJavaScript(`(() => ({
    orb: document.querySelector('.assistant-orb-button') instanceof HTMLButtonElement,
    image: (() => { const node = document.querySelector('.assistant-orb-image'); return node instanceof HTMLImageElement && node.complete && node.naturalWidth > 0; })(),
    caption: document.querySelector('.assistant-orb-caption') !== null,
  }))()`);
  if (!isRecord(assistantCompact) || assistantCompact.orb !== true || assistantCompact.image !== true || assistantCompact.caption !== false) {
    throw new Error('Assistant overlay did not render the approved idle orb asset without persistent caption text.');
  }
  const assistantImage = await assistantWindow.webContents.capturePage();
  await mkdir(artifactsDirectory, { recursive: true });
  await writeFile(assistantOutputPath, assistantImage.toPNG());
  assistantWindow.setSize(118, 112);
  assistantWindow.webContents.send('velia:assistant:snapshot', {
    compact: false,
    snapshot: {
      phase: 'responding',
      status: '正在回答…',
      transcript: '请用一句话说明今天的测试结果。',
      answer: '语音入口、物理按键边沿和悬浮回答面板均已通过自动化检查。',
      canStop: true,
      shortcutLabel: 'F8',
      metrics: { recordingMs: 1260, asrMs: 680, firstTokenMs: 1210, totalMs: 2380 },
      updatedAtUtc: new Date().toISOString(),
    },
  });
  await assistantWindow.webContents.executeJavaScript(
    'new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))',
  );
  assistantWindow.webContents.invalidate();
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 250));
  const assistantAnswering = await assistantWindow.webContents.executeJavaScript(`(() => ({
    answering: document.querySelector('.assistant-orb-shell.assistant-state--answering') !== null,
    text: document.body.innerText.trim(),
    captionPanel: document.querySelector('.assistant-caption-panel') !== null,
    stopControl: document.querySelector('button[aria-label=停止]') !== null,
    width: document.documentElement.scrollWidth - document.documentElement.clientWidth,
    height: document.documentElement.scrollHeight - document.documentElement.clientHeight,
  }))()`);
  if (!isRecord(assistantAnswering) || assistantAnswering.answering !== true || assistantAnswering.text !== '' ||
      assistantAnswering.captionPanel !== false || assistantAnswering.stopControl !== false ||
      Number(assistantAnswering.width) > 1 || Number(assistantAnswering.height) > 1) {
    throw new Error(`Assistant overlay did not remain a text-free compact answering orb: ${JSON.stringify(assistantAnswering)}`);
  }
  const assistantAnsweringImage = await assistantWindow.webContents.capturePage();
  await writeFile(assistantAnsweringOutputPath, assistantAnsweringImage.toPNG());
  assistantWindow.destroy();
  const image = await window.webContents.capturePage();
  await mkdir(artifactsDirectory, { recursive: true });
  await writeFile(outputPath, image.toPNG());

  const visualShellReady = await window.webContents.executeJavaScript(
    `document.body.innerText.includes('意识中心') && document.body.innerText.includes('服务状态')`,
  );
  if (visualShellReady !== true) throw new Error('Visual shell did not load.');
  await assertVisualShellBounds(window);
  const overviewImage = await window.webContents.capturePage();
  await writeFile(overviewOutputPath, overviewImage.toPNG());

  const visualPages = [
    { label: '模型与 API', output: modelsOutputPath },
    { label: '语音与唤醒', output: voiceOutputPath },
    { label: '浏览器与网页', output: browserOutputPath },
  ] as const;
  for (const visualPage of visualPages) {
    const visualOpened = await window.webContents.executeJavaScript(`(() => {
      const label = ${JSON.stringify(visualPage.label)};
      const button = [...document.querySelectorAll('.velia-navigation button')]
        .find(item => item.textContent?.trim().includes(label));
      if (!(button instanceof HTMLButtonElement)) return false;
      button.click();
      return true;
    })()`);
    if (visualOpened !== true) throw new Error(`Could not open ${visualPage.label} for visual smoke.`);
    await window.webContents.executeJavaScript('new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))');
    await new Promise((resolvePromise) => setTimeout(resolvePromise, 180));
    await assertVisualShellBounds(window);
    const visualPageImage = await window.webContents.capturePage();
    await writeFile(visualPage.output, visualPageImage.toPNG());
  }

  await window.webContents.executeJavaScript(`(() => {
    const button = [...document.querySelectorAll('.velia-navigation button')]
      .find(item => item.textContent?.includes('语音与唤醒'));
    if (button instanceof HTMLButtonElement) button.click();
  })()`);
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 120));
  const visualVoiceMenuOpened = await window.webContents.executeJavaScript(`(() => {
    const trigger = document.querySelector('.voice-broadcast-card .voice-soft-select__trigger');
    if (!(trigger instanceof HTMLButtonElement)) return false;
    trigger.click();
    return true;
  })()`);
  if (visualVoiceMenuOpened !== true) throw new Error('Could not open the approved voice menu.');
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 260));
  const visualVoiceMenuExpanded = await window.webContents.executeJavaScript(`(() => {
    const trigger = document.querySelector('.voice-broadcast-card .voice-soft-select__trigger');
    const menu = document.querySelector('.voice-broadcast-card .voice-soft-select__menu');
    if (!(trigger instanceof HTMLElement) || !(menu instanceof HTMLElement)) return null;
    const style = getComputedStyle(menu);
    const rect = menu.getBoundingClientRect();
    return {
      expanded: trigger.getAttribute('aria-expanded') === 'true',
      hidden: menu.hidden,
      display: style.display,
      visibility: style.visibility,
      opacity: style.opacity,
      top: rect.top,
      bottom: rect.bottom,
      width: rect.width,
      height: rect.height,
    };
  })()`);
  if (!isRecord(visualVoiceMenuExpanded) || visualVoiceMenuExpanded.expanded !== true ||
      visualVoiceMenuExpanded.hidden !== false || visualVoiceMenuExpanded.display === 'none' ||
      Number(visualVoiceMenuExpanded.width) <= 0 || Number(visualVoiceMenuExpanded.height) <= 0) {
    throw new Error(`Approved voice menu did not remain visible: ${JSON.stringify(visualVoiceMenuExpanded)}`);
  }
  const voiceMenuImage = await window.webContents.capturePage();
  await writeFile(voiceMenuOutputPath, voiceMenuImage.toPNG());

  await window.webContents.executeJavaScript(`(() => {
    const button = [...document.querySelectorAll('.velia-navigation button')]
      .find(item => item.textContent?.includes('模型与 API'));
    if (button instanceof HTMLButtonElement) button.click();
  })()`);
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 120));
  const visualAsrOpened = await window.webContents.executeJavaScript(`(() => {
    const card = [...document.querySelectorAll('.velia-api-card')]
      .find(item => item.textContent?.includes('火山 ASR'));
    const button = card?.querySelector('.velia-api-button');
    if (!(button instanceof HTMLButtonElement)) return false;
    button.click();
    return true;
  })()`);
  if (visualAsrOpened !== true) throw new Error('Could not open the approved ASR modal.');
  const visualAsrVisible = await window.webContents.executeJavaScript(`(async () => {
    for (let attempt = 0; attempt < 30; attempt += 1) {
      const layer = document.querySelector('.voice-asr-layer.is-open');
      if (layer instanceof HTMLElement && getComputedStyle(layer).visibility === 'visible' &&
          Number(getComputedStyle(layer).opacity) > .9) return true;
      await new Promise(resolve => setTimeout(resolve, 20));
    }
    return false;
  })()`);
  if (visualAsrVisible !== true) throw new Error('Approved ASR modal did not become visible.');
  await window.webContents.executeJavaScript('new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))');
  window.webContents.invalidate();
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 180));
  const asrImage = await window.webContents.capturePage();
  await writeFile(asrOutputPath, asrImage.toPNG());
  await window.webContents.executeJavaScript(`document.querySelector('[aria-label="关闭火山 ASR 配置"]')?.click()`);
  const visualAsrClosed = await window.webContents.executeJavaScript(`(async () => {
    for (let attempt = 0; attempt < 30; attempt += 1) {
      if (!document.querySelector('.voice-asr-layer.is-open')) return true;
      await new Promise(resolve => setTimeout(resolve, 20));
    }
    return false;
  })()`);
  if (visualAsrClosed !== true) throw new Error('Approved ASR modal did not close.');

  await window.webContents.executeJavaScript(`(() => {
    const button = [...document.querySelectorAll('.velia-navigation button')]
      .find(item => item.textContent?.includes('浏览器与网页'));
    if (button instanceof HTMLButtonElement) button.click();
  })()`);
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 120));
  const visualBrowserConfigOpened = await window.webContents.executeJavaScript(`(() => {
    const button = [...document.querySelectorAll('button')]
      .find(item => item.textContent?.trim() === '配置服务');
    if (!(button instanceof HTMLButtonElement)) return false;
    button.click();
    return true;
  })()`);
  if (visualBrowserConfigOpened !== true) throw new Error('Could not open the approved Firecrawl modal.');
  const visualBrowserConfigVisible = await window.webContents.executeJavaScript(`(async () => {
    for (let attempt = 0; attempt < 30; attempt += 1) {
      const layer = document.querySelector('.browser-config-layer.is-open');
      if (layer instanceof HTMLElement && getComputedStyle(layer).visibility === 'visible' &&
          Number(getComputedStyle(layer).opacity) > .9) return true;
      await new Promise(resolve => setTimeout(resolve, 20));
    }
    return false;
  })()`);
  if (visualBrowserConfigVisible !== true) throw new Error('Approved Firecrawl modal did not become visible.');
  await window.webContents.executeJavaScript('new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))');
  window.webContents.invalidate();
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 180));
  const browserConfigImage = await window.webContents.capturePage();
  await writeFile(browserConfigOutputPath, browserConfigImage.toPNG());
  await window.webContents.executeJavaScript(`document.querySelector('[aria-label="关闭 Firecrawl 配置"]')?.click()`);
  window.setSize(960, 640);
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 240));
  for (const compactLabel of ['意识中心', '模型与 API', '语音与唤醒', '浏览器与网页']) {
    await window.webContents.executeJavaScript(`(() => {
      const label = ${JSON.stringify(compactLabel)};
      const button = [...document.querySelectorAll('.velia-navigation button')]
        .find(item => item.textContent?.trim().includes(label));
      if (button instanceof HTMLButtonElement) button.click();
    })()`);
    await window.webContents.executeJavaScript('new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))');
    await new Promise((resolvePromise) => setTimeout(resolvePromise, 90));
    await assertVisualShellBounds(window);
  }

  console.log(JSON.stringify({
    ok: true,
    mode: 'approved-v12-visual-shell',
    screenshots: [
      outputPath,
      overviewOutputPath,
      modelsOutputPath,
      voiceOutputPath,
      voiceMenuOutputPath,
      asrOutputPath,
      browserOutputPath,
      browserConfigOutputPath,
      assistantOutputPath,
      assistantAnsweringOutputPath,
    ],
  }));
  window.destroy();
  return;
  } finally {
    clipboard.writeText(previousClipboardText);
    core.stop();
    await browser.stop();
    app.quit();
  }
}

async function verifyBrowserGatewayRoundTrip(): Promise<void> {
  const pairing = browser.pairingInfo();
  const client = new WebSocket(`ws://127.0.0.1:${pairing.port}`, {
    origin: `chrome-extension://${'a'.repeat(32)}`,
  });
  await new Promise<void>((resolvePromise, rejectPromise) => {
    const timer = setTimeout(() => rejectPromise(new Error('Browser gateway connection timed out.')), 5_000);
    client.once('open', () => {
      clearTimeout(timer);
      resolvePromise();
    });
    client.once('error', rejectPromise);
  });
  client.send(JSON.stringify({
    schema: 'velia-browser-message/v1',
    type: 'hello',
    token: pairing.token,
    extensionVersion: 'smoke-test',
  }));
  const acknowledgement = parseSocketMessage(await nextSocketMessage(client));
  if (!isRecord(acknowledgement) || acknowledgement.type !== 'helloAck' || !browser.status().connected) {
    throw new Error('Browser gateway did not authenticate the smoke bridge.');
  }
  const requestMessage = nextSocketMessage(client);
  const resultPromise = browser.request<{ readonly pong: boolean }>('ping', {});
  const request = parseSocketMessage(await requestMessage);
  if (!isRecord(request) || request.type !== 'request' || typeof request.id !== 'string' || request.method !== 'ping') {
    throw new Error('Browser gateway did not emit the expected allowlisted request.');
  }
  client.send(JSON.stringify({
    schema: 'velia-browser-message/v1',
    type: 'response',
    id: request.id,
    ok: true,
    result: { pong: true },
  }));
  const result = await resultPromise;
  if (!result.pong) throw new Error('Browser gateway did not complete its request round trip.');
  const cancelledMessage = nextSocketMessage(client);
  const cancellable = browser.requestCancellable('ping', {});
  const cancelledRequest = parseSocketMessage(await cancelledMessage);
  if (!isRecord(cancelledRequest) || cancelledRequest.type !== 'request' || cancelledRequest.method !== 'ping') {
    throw new Error('Browser gateway did not emit its cancellable request.');
  }
  const cancellationMessage = nextSocketMessage(client);
  cancellable.cancel();
  await cancellable.promise.then(
    () => { throw new Error('Cancelled browser request resolved unexpectedly.'); },
    () => undefined,
  );
  const cancellation = parseSocketMessage(await cancellationMessage);
  if (!isRecord(cancellation) || cancellation.type !== 'cancel' || cancellation.id !== cancelledRequest.id) {
    throw new Error('Browser gateway did not forward cancellation to the bridge.');
  }
  await new Promise<void>((resolvePromise) => {
    client.once('close', () => resolvePromise());
    client.close(1000, 'Smoke complete');
  });
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 50));
  if (browser.status().connected) throw new Error('Browser gateway retained a closed smoke bridge.');
  console.log('SMOKE_BROWSER_GATEWAY_OK');
}

async function assertVisualShellBounds(window: BrowserWindow): Promise<void> {
  const bounds = await window.webContents.executeJavaScript(`(() => {
    const shell = document.querySelector('.velia-control');
    const sidebar = document.querySelector('.velia-sidebar');
    const header = document.querySelector('.velia-main-header');
    const page = document.querySelector('.velia-page');
    const dock = document.querySelector('.velia-bottom-dock');
    if (![shell, sidebar, header, page, dock].every(item => item instanceof HTMLElement)) return null;
    const shellRect = shell.getBoundingClientRect();
    const sidebarRect = sidebar.getBoundingClientRect();
    const headerRect = header.getBoundingClientRect();
    const dockRect = dock.getBoundingClientRect();
    const bodyRect = document.body.getBoundingClientRect();
    const rootRect = document.querySelector('#root')?.getBoundingClientRect();
    const sidebarStyle = getComputedStyle(sidebar);
    return {
      viewportWidth: innerWidth,
      viewportHeight: innerHeight,
      scrollY,
      visualViewportOffsetTop: visualViewport?.offsetTop,
      visualViewportHeight: visualViewport?.height,
      visualViewportScale: visualViewport?.scale,
      documentOverflowX: document.documentElement.scrollWidth - document.documentElement.clientWidth,
      documentOverflowY: document.documentElement.scrollHeight - document.documentElement.clientHeight,
      bodyTop: bodyRect.top,
      bodyBottom: bodyRect.bottom,
      rootTop: rootRect?.top,
      rootBottom: rootRect?.bottom,
      shellTop: shellRect.top,
      shellBottom: shellRect.bottom,
      sidebarTop: sidebarRect.top,
      sidebarBottom: sidebarRect.bottom,
      sidebarOffsetHeight: sidebar.offsetHeight,
      sidebarScrollHeight: sidebar.scrollHeight,
      sidebarComputedHeight: sidebarStyle.height,
      sidebarTransform: sidebarStyle.transform,
      headerTop: headerRect.top,
      dockBottom: dockRect.bottom,
      pageScrollTop: page.scrollTop,
      pageOverflowX: page.scrollWidth - page.clientWidth,
    };
  })()`);
  if (!isRecord(bounds)) throw new Error('Visual shell bounds were unavailable.');
  const number = (key: string): number => Number(bounds[key]);
  if (number('documentOverflowX') > 1 || number('documentOverflowY') > 1 || number('pageOverflowX') > 1 ||
      number('pageScrollTop') > 1) {
    throw new Error(`Approved visual shell overflowed its viewport: ${JSON.stringify(bounds)}`);
  }
  if (number('shellTop') < -1 || number('sidebarTop') < -1 || number('headerTop') < -1 ||
      number('shellBottom') > number('viewportHeight') + 1 ||
      number('sidebarBottom') > number('viewportHeight') + 1 ||
      number('dockBottom') > number('viewportHeight') + 1) {
    throw new Error(`Approved visual shell escaped its viewport: ${JSON.stringify(bounds)}`);
  }
}

function nextSocketMessage(client: WebSocket): Promise<RawData> {
  return new Promise((resolvePromise, rejectPromise) => {
    const timer = setTimeout(() => rejectPromise(new Error('Browser gateway message timed out.')), 5_000);
    client.once('message', (data) => {
      clearTimeout(timer);
      resolvePromise(data);
    });
  });
}

function parseSocketMessage(data: RawData): unknown {
  try { return JSON.parse(data.toString()) as unknown; } catch { return undefined; }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

console.log('SMOKE_WAITING_FOR_APP');
app.whenReady().then(runSmoke).catch((error: unknown) => {
  console.error(`SMOKE_FAILED: ${error instanceof Error ? error.message : 'unknown error'}`);
  core.stop();
  app.exit(1);
});
