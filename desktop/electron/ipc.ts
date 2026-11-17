import { app, BrowserWindow, Notification, clipboard, dialog, ipcMain, powerMonitor, shell, type IpcMainInvokeEvent } from 'electron';
import { join, resolve } from 'node:path';
import {
  providerProfileIds,
  type CancelConversationResult,
  type ConfigStatusResult,
  type HealthResult,
  type ProviderId,
  type ProviderProfileId,
  type ProviderProfilesResult,
  type ProviderTestResult,
  type SubmitTextResult,
  type AsrCredentials,
  type AsrTestResult,
  type VoiceTranscriptionResult,
  edgeVoiceIds,
  type EdgeVoiceId,
  type TtsSynthesisResult,
  type WakeWordDetectionResult,
  type WakeWordPrivacyEvent,
  type WakeWordStatus,
  voiceShortcutIds,
  type VoiceShortcutId,
  type VoiceShortcutSetting,
  visionModels,
  type VisionAnalyzeResult,
  type VisionDisplay,
  type VisionModel,
  type VisionTestResult,
  type DocumentAnalyzeResult,
  type DocumentCloseResult,
  type DocumentOpenResult,
  voiceOverlayPhases,
  type AssistantOverlayCommand,
  type AssistantOverlaySnapshot,
  type LiveStartResult,
  type LiveSendAudioResult,
  type LiveSubmitLocalTranscriptResult,
  type LiveStopResult,
  type LiveStatusResult,
  type LiveVisionStatus,
  liveVoiceNames,
  type LiveVoiceName,
  missionOrigins,
  missionRelationships,
  type MissionOrigin,
  type MissionRelationship,
  type WebTestResult,
  conversationHistoryRetentionDays,
  type ConversationHistoryRetentionDays,
  type ConversationHistorySettings,
  type ConversationHistoryListResult,
  type ConversationHistoryDetail,
  type ConversationHistoryDeleteResult,
  type ConversationHistoryClearResult,
  type ConversationHistoryStartNewResult,
} from '@velia/protocol';
import { CoreProcessClient } from './core/CoreProcessClient.js';
import { BrowserGateway, type BrowserGatewayStatus } from './browser/BrowserGateway.js';
import { BrowserToolOrchestrator } from './browser/BrowserToolOrchestrator.js';
import { resolveTrustedExternalResource } from './desktopSecurity.js';
import { ScreenCaptureService } from './vision/ScreenCaptureService.js';
import { LiveVisionStreamer } from './vision/LiveVisionStreamer.js';
import { LiveVisualToolCoordinator } from './vision/LiveVisualToolCoordinator.js';
import { ScreenVisionToolOrchestrator } from './vision/ScreenVisionToolOrchestrator.js';
import { ScreenDocumentWriteToolOrchestrator } from './vision/ScreenDocumentWriteToolOrchestrator.js';
import { extractDocument } from './documents/DocumentExtractor.js';
import { SelectedDocumentToolOrchestrator } from './documents/SelectedDocumentToolOrchestrator.js';
import { AssistantWindowController } from './assistant/AssistantWindowController.js';
import { EdgeTtsService } from './voice/EdgeTtsService.js';
import { WakeWordService } from './voice/WakeWordService.js';
import { DesktopContextToolOrchestrator } from './desktopContext/DesktopContextToolOrchestrator.js';
import { DesktopVisualToolOrchestrator } from './desktopVisual/DesktopVisualToolOrchestrator.js';
import { shouldForwardConversationEvent } from './core/CoreEventRouting.js';

const providerIds: readonly ProviderId[] = ['llm', 'asr', 'vision', 'web'];

export interface DesktopIpcLifecycle {
  readonly stopLiveForPrivacy: (reason: string) => Promise<void>;
}

export function registerIpc(
  core: CoreProcessClient,
  browser: BrowserGateway,
  extensionPath: string,
  assistant?: AssistantWindowController,
): DesktopIpcLifecycle {
  const screenCapture = new ScreenCaptureService();
  const publishLiveVision = (status: LiveVisionStatus): void => {
    for (const window of BrowserWindow.getAllWindows()) {
      if (!window.isDestroyed() && !window.webContents.isDestroyed()) {
        window.webContents.send('velia:live:vision-status', status);
      }
    }
  };
  const liveVision = new LiveVisionStreamer(core, screenCapture, publishLiveVision);
  const liveVisualTools = new LiveVisualToolCoordinator(core, screenCapture);
  const edgeTts = new EdgeTtsService();
  const wakeWord = new WakeWordService({
    runtimeDirectory: app.isPackaged
      ? join(process.resourcesPath, 'wake-word')
      : resolve(app.getAppPath(), '..', '.tools', 'wake-word'),
    settingsPath: join(app.getPath('userData'), 'settings', 'wake-word.json'),
  });
  const browserTools = new BrowserToolOrchestrator(
    core,
    browser,
    join(app.getPath('userData'), 'logs', 'browser-evidence.jsonl'),
  );
  const desktopContextTools = new DesktopContextToolOrchestrator(core);
  const desktopVisualTools = new DesktopVisualToolOrchestrator(core, screenCapture);
  const screenVisionTools = new ScreenVisionToolOrchestrator(core, screenCapture);
  const screenDocumentWriteTools = new ScreenDocumentWriteToolOrchestrator(core, screenCapture);
  const selectedDocumentTools = new SelectedDocumentToolOrchestrator(core);
  core.subscribe((coreEvent) => {
    if (coreEvent.type === 'notification' && coreEvent.notification?.kind === 'timerElapsed') {
      assistant?.notifyTimer(coreEvent.notification.message);
      try { shell.beep(); }
      catch (error: unknown) { console.error(`Timer system beep failed: ${error instanceof Error ? error.message : 'unknown error'}`); }
      try {
        if (Notification.isSupported()) {
          new Notification({
            title: coreEvent.notification.title,
            body: coreEvent.notification.message,
            silent: false,
          }).show();
        }
      } catch (error: unknown) {
        console.error(`Timer notification failed: ${error instanceof Error ? error.message : 'unknown error'}`);
      }
      return;
    }
    browserTools.handle(coreEvent);
    desktopContextTools.handle(coreEvent);
    desktopVisualTools.handle(coreEvent);
    screenVisionTools.handle(coreEvent);
    screenDocumentWriteTools.handle(coreEvent);
    selectedDocumentTools.handle(coreEvent);
    if (!shouldForwardConversationEvent(coreEvent)) return;
    for (const window of BrowserWindow.getAllWindows()) {
      if (!window.isDestroyed() && !window.webContents.isDestroyed()) {
        window.webContents.send('velia:conversation:event', coreEvent);
      }
    }
  });
  core.subscribeInput((inputEvent) => assistant?.forwardInput(inputEvent));
  core.subscribeLive((liveEvent) => {
    void liveVisualTools.handle(liveEvent);
    if (liveEvent.type === 'closed' || liveEvent.type === 'failed') liveVision.stop(liveEvent.type);
    for (const window of BrowserWindow.getAllWindows()) {
      if (!window.isDestroyed() && !window.webContents.isDestroyed()) window.webContents.send('velia:live:event', liveEvent);
    }
  });
  const stopLiveForPrivacy = async (reason: string): Promise<void> => {
    const activeSession = liveVision.activeSessionId;
    if (!activeSession) return;
    liveVision.stop(reason);
    await core.request<LiveStopResult>('live.stop', { sessionId: activeSession }, 10_000).catch(() => undefined);
  };
  const publishWakePrivacy = (paused: boolean, reason: string): void => {
    const privacyEvent: WakeWordPrivacyEvent = { paused, reason };
    for (const window of BrowserWindow.getAllWindows()) {
      if (!window.isDestroyed() && !window.webContents.isDestroyed()) window.webContents.send('velia:wake:privacy', privacyEvent);
    }
  };
  const stopAudioForPrivacy = (reason: string): void => {
    publishWakePrivacy(true, reason);
    assistant?.command('stop');
    void stopLiveForPrivacy(reason);
  };
  powerMonitor.on('lock-screen', () => stopAudioForPrivacy('screen-locked'));
  powerMonitor.on('suspend', () => stopAudioForPrivacy('system-suspended'));
  powerMonitor.on('unlock-screen', () => publishWakePrivacy(false, 'screen-unlocked'));
  powerMonitor.on('resume', () => publishWakePrivacy(false, 'system-resumed'));

  ipcMain.handle('velia:assistant:get-state', (event) => {
    assertTrustedRenderer(event);
    return assistant?.state() ?? { snapshot: emptyOverlaySnapshot(), compact: true };
  });

  ipcMain.handle('velia:assistant:publish', (event, snapshot: unknown): void => {
    assertTrustedRenderer(event);
    assertOverlaySnapshot(snapshot);
    assistant?.publish(snapshot);
  });

  ipcMain.handle('velia:assistant:command', (event, command: unknown): void => {
    assertTrustedRenderer(event);
    if (!['toggleRecording', 'stop', 'collapse', 'expand'].includes(String(command))) {
      throw new TypeError('Assistant command is invalid.');
    }
    assistant?.command(command as AssistantOverlayCommand);
  });

  ipcMain.handle('velia:assistant:copy-answer', (event, answer: unknown): void => {
    assertTrustedRenderer(event);
    if (typeof answer !== 'string' || answer.length === 0 || answer.length > 32_768) {
      throw new TypeError('Assistant answer is invalid.');
    }
    assistant?.copyAnswer(answer);
  });

  ipcMain.handle('velia:assistant:open-main', (event): void => {
    assertTrustedRenderer(event);
    assistant?.openMain();
  });

  ipcMain.handle('velia:assistant:quit', (event): void => {
    assertTrustedRenderer(event);
    assistant?.quit();
  });

  browser.subscribe((status) => {
    for (const window of BrowserWindow.getAllWindows()) {
      if (!window.isDestroyed() && !window.webContents.isDestroyed()) {
        window.webContents.send('velia:browser:status-changed', status);
      }
    }
  });

  ipcMain.handle('velia:browser:status', (event): BrowserGatewayStatus => {
    assertTrustedRenderer(event);
    return browser.status();
  });

  ipcMain.handle('velia:browser:pairing', (event): { readonly token: string; readonly port: number } => {
    assertTrustedRenderer(event);
    return browser.pairingInfo();
  });

  ipcMain.handle('velia:browser:copy-pairing-token', (event): void => {
    assertTrustedRenderer(event);
    clipboard.writeText(browser.pairingInfo().token);
  });

  ipcMain.handle('velia:browser:open-extension-folder', async (event): Promise<void> => {
    assertTrustedRenderer(event);
    const error = await shell.openPath(extensionPath);
    if (error) throw new Error(`无法打开浏览器扩展目录：${error}`);
  });

  ipcMain.handle('velia:web:test', async (event): Promise<WebTestResult> => {
    assertTrustedRenderer(event);
    return core.request<WebTestResult>('web.test', {}, 35_000);
  });

  ipcMain.handle('velia:system:health', async (event): Promise<HealthResult> => {
    assertTrustedRenderer(event);
    return core.request<HealthResult>('system.health', {});
  });

  ipcMain.handle('velia:system:open-external-resource', async (event, resourceId: unknown): Promise<void> => {
    assertTrustedRenderer(event);
    await shell.openExternal(resolveTrustedExternalResource(resourceId));
  });

  ipcMain.handle('velia:app:bootstrap', async (event): Promise<unknown> => {
    assertTrustedRenderer(event);
    return core.request('app.bootstrap', {});
  });

  ipcMain.handle('velia:config:status', async (event): Promise<ConfigStatusResult> => {
    assertTrustedRenderer(event);
    return core.request<ConfigStatusResult>('config.getStatus', {});
  });

  ipcMain.handle('velia:config:profiles', async (event): Promise<ProviderProfilesResult> => {
    assertTrustedRenderer(event);
    return core.request<ProviderProfilesResult>('config.getProfiles', {});
  });

  ipcMain.handle(
    'velia:config:select-profile',
    async (event, profileId: unknown): Promise<ProviderProfilesResult> => {
      assertTrustedRenderer(event);
      assertProviderProfile(profileId);
      return core.request<ProviderProfilesResult>('config.selectProfile', { profileId });
    },
  );

  ipcMain.handle('velia:config:test-provider', async (event, profileId: unknown): Promise<ProviderTestResult> => {
    assertTrustedRenderer(event);
    assertProviderProfile(profileId);
    return core.request<ProviderTestResult>('config.testProvider', { profileId }, 35_000);
  });

  ipcMain.handle('velia:config:set-secret', async (event, provider: unknown, secret: unknown): Promise<void> => {
    assertTrustedRenderer(event);
    assertProvider(provider);
    if (typeof secret !== 'string' || secret.trim().length === 0 || secret.length > 16_384) {
      throw new TypeError('Provider secret is invalid.');
    }
    await core.request('config.setSecret', { provider, secret });
  });

  ipcMain.handle('velia:voice:save-asr', async (event, credentials: unknown): Promise<void> => {
    assertTrustedRenderer(event);
    assertAsrCredentials(credentials);
    await core.request('config.setSecret', { provider: 'asr', secret: JSON.stringify(credentials) });
  });

  ipcMain.handle('velia:voice:test-asr', async (event): Promise<AsrTestResult> => {
    assertTrustedRenderer(event);
    return core.request<AsrTestResult>('voice.testAsr', {}, 35_000);
  });

  ipcMain.handle('velia:voice:get-shortcut', async (event): Promise<VoiceShortcutSetting> => {
    assertTrustedRenderer(event);
    return core.request<VoiceShortcutSetting>('voice.getShortcut', {});
  });

  ipcMain.handle('velia:voice:set-shortcut', async (event, shortcutId: unknown): Promise<VoiceShortcutSetting> => {
    assertTrustedRenderer(event);
    if (typeof shortcutId !== 'string' || !voiceShortcutIds.includes(shortcutId as VoiceShortcutId)) {
      throw new TypeError('Voice shortcut is invalid.');
    }
    return core.request<VoiceShortcutSetting>('voice.setShortcut', { shortcutId });
  });

  ipcMain.handle('velia:voice:transcribe', async (event, wavBase64: unknown, options: unknown): Promise<VoiceTranscriptionResult> => {
    assertTrustedRenderer(event);
    if (typeof wavBase64 !== 'string' || wavBase64.length === 0 || wavBase64.length > 940_000) {
      throw new TypeError('Voice audio is invalid.');
    }
    const maxLatencyMs = isRecord(options) && typeof options.maxLatencyMs === 'number'
      ? Math.trunc(options.maxLatencyMs)
      : undefined;
    if (maxLatencyMs !== undefined && (maxLatencyMs < 1_000 || maxLatencyMs > 20_000)) {
      throw new TypeError('Voice transcription deadline is invalid.');
    }
    return core.request<VoiceTranscriptionResult>(
      'voice.transcribe',
      { wavBase64, ...(maxLatencyMs === undefined ? {} : { maxLatencyMs }) },
      maxLatencyMs === undefined ? 35_000 : maxLatencyMs + 2_000,
    );
  });

  ipcMain.handle('velia:wake:status', (event): WakeWordStatus => {
    assertTrustedRenderer(event);
    return wakeWord.status();
  });

  ipcMain.handle('velia:wake:set-enabled', async (event, enabled: unknown): Promise<WakeWordStatus> => {
    assertTrustedRenderer(event);
    if (typeof enabled !== 'boolean') throw new TypeError('Wake-word setting is invalid.');
    return wakeWord.setEnabled(enabled);
  });

  ipcMain.handle('velia:wake:detect', async (event, wavBase64: unknown): Promise<WakeWordDetectionResult> => {
    assertTrustedRenderer(event);
    if (typeof wavBase64 !== 'string' || wavBase64.length < 128 || wavBase64.length > 940_000 ||
        !/^[A-Za-z0-9+/]+={0,2}$/u.test(wavBase64)) {
      throw new TypeError('Wake-word audio is invalid.');
    }
    return wakeWord.detect(wavBase64);
  });

  ipcMain.handle('velia:live:start', async (event, voiceName: unknown): Promise<LiveStartResult> => {
    assertTrustedRenderer(event);
    if (typeof voiceName !== 'string' || !liveVoiceNames.includes(voiceName as LiveVoiceName)) {
      throw new TypeError('Live voice is invalid.');
    }
    const started = await core.request<LiveStartResult>('live.start', {
      model: 'gemini-3.1-flash-live-preview', voiceName,
    }, 35_000);
    liveVision.start(started.sessionId);
    return started;
  });

  ipcMain.handle('velia:live:send-audio', async (event, sessionId: unknown, audioBase64: unknown): Promise<LiveSendAudioResult> => {
    assertTrustedRenderer(event);
    if (typeof sessionId !== 'string' || !uuidPattern.test(sessionId) || typeof audioBase64 !== 'string' ||
        audioBase64.length < 428 || audioBase64.length > 3_416 || !/^[A-Za-z0-9+/]+={0,2}$/u.test(audioBase64)) {
      throw new TypeError('Live audio is invalid.');
    }
    return core.request<LiveSendAudioResult>('live.sendAudio', { sessionId, audioBase64, mimeType: 'audio/pcm;rate=16000' }, 3_000);
  });

  ipcMain.handle(
    'velia:live:submit-local-transcript',
    async (event, sessionId: unknown, transcript: unknown, utteranceId: unknown, capturedAtUtc: unknown): Promise<LiveSubmitLocalTranscriptResult> => {
      assertTrustedRenderer(event);
      if (typeof sessionId !== 'string' || !uuidPattern.test(sessionId) ||
          typeof transcript !== 'string' || transcript.trim().length === 0 || transcript.length > 32_768 ||
          typeof utteranceId !== 'string' || !uuidPattern.test(utteranceId) ||
          typeof capturedAtUtc !== 'string' || !Number.isFinite(Date.parse(capturedAtUtc)) ||
          Math.abs(Date.now() - Date.parse(capturedAtUtc)) > 300_000) {
        throw new TypeError('Live local transcript is invalid.');
      }
      return core.request<LiveSubmitLocalTranscriptResult>(
        'live.submitLocalTranscript',
        { sessionId, transcript: transcript.trim(), utteranceId, capturedAtUtc },
        12_000,
      );
    },
  );

  ipcMain.handle('velia:live:stop', async (event, sessionId: unknown): Promise<LiveStopResult> => {
    assertTrustedRenderer(event);
    if (typeof sessionId !== 'string' || !uuidPattern.test(sessionId)) throw new TypeError('Live session is invalid.');
    liveVision.stop('user-stopped');
    return core.request<LiveStopResult>('live.stop', { sessionId }, 10_000);
  });

  ipcMain.handle('velia:live:status', async (event): Promise<LiveStatusResult> => {
    assertTrustedRenderer(event);
    return core.request<LiveStatusResult>('live.getStatus', {});
  });

  ipcMain.handle(
    'velia:voice:synthesize',
    async (event, text: unknown, voiceId: unknown, rate: unknown): Promise<TtsSynthesisResult> => {
      assertTrustedRenderer(event);
      if (typeof text !== 'string' || text.trim().length === 0 || text.length > 4_000 ||
          typeof voiceId !== 'string' || !edgeVoiceIds.includes(voiceId as EdgeVoiceId) ||
          typeof rate !== 'number' || !Number.isInteger(rate) || rate < -50 || rate > 50 || rate % 5 !== 0) {
        throw new TypeError('TTS request is invalid.');
      }
      return edgeTts.synthesize(text, voiceId as EdgeVoiceId, rate);
    },
  );

  ipcMain.handle('velia:vision:list-displays', (event): readonly VisionDisplay[] => {
    assertTrustedRenderer(event);
    return screenCapture.listDisplays();
  });

  ipcMain.handle('velia:vision:test', async (event): Promise<VisionTestResult> => {
    assertTrustedRenderer(event);
    return core.request<VisionTestResult>('vision.test', {}, 35_000);
  });

  ipcMain.handle('velia:document:choose-and-open', async (event): Promise<DocumentOpenResult | null> => {
    assertTrustedRenderer(event);
    const window = BrowserWindow.fromWebContents(event.sender);
    if (!window || window.isDestroyed()) throw new Error('Velia 窗口不可用。');
    const selection = await dialog.showOpenDialog(window, {
      title: '选择要只读分析的文件',
      properties: ['openFile'],
      filters: [{ name: '支持的文件', extensions: ['txt', 'md', 'markdown', 'csv', 'log', 'pdf', 'docx'] }],
    });
    if (selection.canceled || selection.filePaths.length !== 1) return null;
    const extracted = await extractDocument(selection.filePaths[0]!);
    return core.request<DocumentOpenResult>('document.open', extracted, 15_000);
  });

  ipcMain.handle(
    'velia:document:analyze',
    async (event, documentId: unknown, prompt: unknown): Promise<DocumentAnalyzeResult> => {
      assertTrustedRenderer(event);
      if (typeof documentId !== 'string' || !uuidPattern.test(documentId)) {
        throw new TypeError('Document identifier is invalid.');
      }
      if (typeof prompt !== 'string' || prompt.trim().length === 0 || prompt.length > 4_000) {
        throw new TypeError('Document prompt is invalid.');
      }
      return core.request<DocumentAnalyzeResult>(
        'document.analyze',
        { documentId, prompt: prompt.trim() },
        90_000);
    },
  );

  ipcMain.handle('velia:document:close', async (event, documentId: unknown): Promise<DocumentCloseResult> => {
    assertTrustedRenderer(event);
    if (typeof documentId !== 'string' || !uuidPattern.test(documentId)) {
      throw new TypeError('Document identifier is invalid.');
    }
    return core.request<DocumentCloseResult>('document.close', { documentId });
  });

  ipcMain.handle(
    'velia:vision:capture-and-analyze',
    async (event, displayId: unknown, prompt: unknown, model: unknown): Promise<VisionAnalyzeResult> => {
      assertTrustedRenderer(event);
      if (typeof displayId !== 'string' || displayId.length === 0 || displayId.length > 128) {
        throw new TypeError('Display identifier is invalid.');
      }
      if (typeof prompt !== 'string' || prompt.trim().length === 0 || prompt.length > 4_000) {
        throw new TypeError('Vision prompt is invalid.');
      }
      if (typeof model !== 'string' || !visionModels.includes(model as VisionModel)) {
        throw new TypeError('Vision model is not allowlisted.');
      }
      const window = BrowserWindow.fromWebContents(event.sender);
      if (!window || window.isDestroyed()) throw new Error('Velia 窗口不可用，无法安全截图。');
      let capture = await screenCapture.captureOnce(window, displayId);
      try {
        return await core.request<VisionAnalyzeResult>('vision.analyze', {
          imageBase64: capture.imageBase64,
          mimeType: capture.mimeType,
          displayId: capture.displayId,
          capturedAtUtc: capture.capturedAtUtc,
          prompt: prompt.trim(),
          model,
        }, 75_000);
      } finally {
        capture = {
          imageBase64: '', mimeType: 'image/jpeg', displayId: '', capturedAtUtc: '',
          width: 0, height: 0, inputBytes: 0,
        };
      }
    },
  );

  ipcMain.handle('velia:config:remove-secret', async (event, provider: unknown): Promise<void> => {
    assertTrustedRenderer(event);
    assertProvider(provider);
    await core.request('config.removeSecret', { provider });
  });

  ipcMain.handle('velia:conversation:submit-text', async (
    event,
    input: unknown,
    origin: unknown = 'text',
    relationship: unknown = 'auto',
  ): Promise<SubmitTextResult> => {
    assertTrustedRenderer(event);
    if (typeof input !== 'string' || input.trim().length === 0 || input.length > 32_768) {
      throw new TypeError('Conversation input is invalid.');
    }
    if (typeof origin !== 'string' || !missionOrigins.includes(origin as MissionOrigin)) {
      throw new TypeError('Conversation origin is invalid.');
    }
    if (relationship !== 'auto' && (typeof relationship !== 'string' || !missionRelationships.includes(relationship as MissionRelationship))) {
      throw new TypeError('Mission relationship is invalid.');
    }
    return core.request<SubmitTextResult>('conversation.submitText', { text: input, origin, relationship });
  });

  ipcMain.handle(
    'velia:conversation:cancel',
    async (event, operationId: unknown): Promise<CancelConversationResult> => {
      assertTrustedRenderer(event);
      if (typeof operationId !== 'string' || !uuidPattern.test(operationId)) {
        throw new TypeError('Conversation operation identifier is invalid.');
      }
      browserTools.cancel(operationId);
      return core.request<CancelConversationResult>('conversation.cancel', { operationId });
    },
  );

  ipcMain.handle('velia:conversation:history-settings', async (event): Promise<ConversationHistorySettings> => {
    assertTrustedRenderer(event);
    return core.request<ConversationHistorySettings>('conversation.history.getSettings', {});
  });

  ipcMain.handle(
    'velia:conversation:update-history-settings',
    async (event, enabled: unknown, retentionDays: unknown): Promise<ConversationHistorySettings> => {
      assertTrustedRenderer(event);
      if (typeof enabled !== 'boolean' || typeof retentionDays !== 'number' ||
          !conversationHistoryRetentionDays.includes(retentionDays as ConversationHistoryRetentionDays)) {
        throw new TypeError('Conversation history settings are invalid.');
      }
      return core.request<ConversationHistorySettings>('conversation.history.updateSettings', { enabled, retentionDays });
    },
  );

  ipcMain.handle('velia:conversation:list-history', async (event): Promise<ConversationHistoryListResult> => {
    assertTrustedRenderer(event);
    return core.request<ConversationHistoryListResult>('conversation.history.list', {});
  });

  ipcMain.handle('velia:conversation:get-history', async (event, conversationId: unknown): Promise<ConversationHistoryDetail> => {
    assertTrustedRenderer(event);
    assertConversationId(conversationId);
    return core.request<ConversationHistoryDetail>('conversation.history.get', { conversationId });
  });

  ipcMain.handle('velia:conversation:open-history', async (event, conversationId: unknown): Promise<ConversationHistoryDetail> => {
    assertTrustedRenderer(event);
    assertConversationId(conversationId);
    return core.request<ConversationHistoryDetail>('conversation.history.open', { conversationId });
  });

  ipcMain.handle('velia:conversation:start-new-history', async (event): Promise<ConversationHistoryStartNewResult> => {
    assertTrustedRenderer(event);
    return core.request<ConversationHistoryStartNewResult>('conversation.history.startNew', {});
  });

  ipcMain.handle('velia:conversation:delete-history', async (event, conversationId: unknown): Promise<ConversationHistoryDeleteResult> => {
    assertTrustedRenderer(event);
    assertConversationId(conversationId);
    return core.request<ConversationHistoryDeleteResult>('conversation.history.delete', { conversationId });
  });

  ipcMain.handle('velia:conversation:clear-history', async (event): Promise<ConversationHistoryClearResult> => {
    assertTrustedRenderer(event);
    return core.request<ConversationHistoryClearResult>('conversation.history.clear', {});
  });
  return Object.freeze({ stopLiveForPrivacy });
}

function emptyOverlaySnapshot(): AssistantOverlaySnapshot {
  return { phase: 'idle', status: '按住 F8 说话', transcript: '', answer: '', canStop: false, shortcutLabel: 'F8', metrics: {}, updatedAtUtc: new Date(0).toISOString() };
}

function assertOverlaySnapshot(value: unknown): asserts value is AssistantOverlaySnapshot {
  if (!isRecord(value) || typeof value.phase !== 'string' || !voiceOverlayPhases.includes(value.phase as AssistantOverlaySnapshot['phase']) ||
      typeof value.status !== 'string' || value.status.length < 1 || value.status.length > 300 ||
      typeof value.transcript !== 'string' || value.transcript.length > 32_768 ||
      typeof value.answer !== 'string' || value.answer.length > 1_048_576 ||
      typeof value.canStop !== 'boolean' || typeof value.shortcutLabel !== 'string' || value.shortcutLabel.length < 1 || value.shortcutLabel.length > 40 || !isRecord(value.metrics) ||
      (Object.hasOwn(value, 'wakeWordActive') && typeof value.wakeWordActive !== 'boolean') ||
      typeof value.updatedAtUtc !== 'string' || Number.isNaN(Date.parse(value.updatedAtUtc))) {
    throw new TypeError('Assistant snapshot is invalid.');
  }
  const metrics = value.metrics;
  if (!['recordingMs', 'asrMs', 'firstTokenMs', 'totalMs'].every((key) =>
      !Object.hasOwn(metrics, key) || (typeof metrics[key] === 'number' && Number.isFinite(metrics[key]) && Number(metrics[key]) >= 0 && Number(metrics[key]) <= 3_600_000))) {
    throw new TypeError('Assistant metrics are invalid.');
  }
}

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu;

function assertConversationId(value: unknown): asserts value is string {
  if (typeof value !== 'string' || !uuidPattern.test(value)) {
    throw new TypeError('Conversation history identifier is invalid.');
  }
}

function assertProvider(value: unknown): asserts value is ProviderId {
  if (typeof value !== 'string' || !providerIds.includes(value as ProviderId)) {
    throw new TypeError('Provider is invalid.');
  }
}

function assertProviderProfile(value: unknown): asserts value is ProviderProfileId {
  if (typeof value !== 'string' || !providerProfileIds.includes(value as ProviderProfileId)) {
    throw new TypeError('Provider profile is invalid.');
  }
}

function assertAsrCredentials(value: unknown): asserts value is AsrCredentials {
  if (!isRecord(value) || !['apiKey', 'legacy'].includes(String(value.mode))) {
    throw new TypeError('ASR credentials are invalid.');
  }
  const valid = (item: unknown): item is string =>
    typeof item === 'string' && item.trim().length >= 4 && item.length <= 16_384;
  if (value.mode === 'apiKey' ? !valid(value.apiKey) : !valid(value.appId) || !valid(value.accessToken)) {
    throw new TypeError('ASR credentials are incomplete.');
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function assertTrustedRenderer(event: IpcMainInvokeEvent): void {
  const url = event.senderFrame?.url;
  if (!url) {
    throw new Error('IPC sender frame is unavailable.');
  }
  if (url.startsWith('file:', 0)) {
    return;
  }
  if (process.env.VELIA_DEV_SERVER_URL && url.startsWith(process.env.VELIA_DEV_SERVER_URL, 0)) {
    return;
  }
  throw new Error('IPC sender is not trusted.');
}
