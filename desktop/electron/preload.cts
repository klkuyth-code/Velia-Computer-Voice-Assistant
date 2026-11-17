import { contextBridge, ipcRenderer } from 'electron';
import type {
  CancelConversationResult,
  ConfigStatusResult,
  CoreEvent,
  HealthResult,
  ProviderId,
  ProviderProfileId,
  ProviderProfilesResult,
  ProviderTestResult,
  SubmitTextResult,
  AsrCredentials,
  AsrTestResult,
  VoiceTranscriptionResult,
  EdgeVoiceId,
  TtsSynthesisResult,
  WakeWordDetectionResult,
  WakeWordPrivacyEvent,
  WakeWordStatus,
  VoiceShortcutId,
  VoiceShortcutSetting,
  VisionAnalyzeResult,
  VisionDisplay,
  VisionModel,
  VisionTestResult,
  DocumentAnalyzeResult,
  DocumentCloseResult,
  DocumentOpenResult,
  AssistantOverlayCommand,
  AssistantOverlaySnapshot,
  InputEvent,
  LiveEvent,
  LiveStartResult,
  LiveSendAudioResult,
  LiveSubmitLocalTranscriptResult,
  LiveStopResult,
  LiveStatusResult,
  LiveVoiceName,
  LiveVisionStatus,
  MissionOrigin,
  MissionRelationship,
  WebTestResult,
  ConversationHistoryRetentionDays,
  ConversationHistorySettings,
  ConversationHistoryListResult,
  ConversationHistoryDetail,
  ConversationHistoryDeleteResult,
  ConversationHistoryClearResult,
  ConversationHistoryStartNewResult,
} from '@velia/protocol';
import type { BrowserGatewayStatus } from './browser/BrowserGateway.js';

const api = Object.freeze({
  system: Object.freeze({
    health: (): Promise<HealthResult> => ipcRenderer.invoke('velia:system:health') as Promise<HealthResult>,
    openExternalResource: (resourceId: 'deepseekApiKeys' | 'volcengineSpeechApiKeys' | 'geminiApiKeys' | 'firecrawlApiKeys'): Promise<void> =>
      ipcRenderer.invoke('velia:system:open-external-resource', resourceId) as Promise<void>,
  }),
  app: Object.freeze({
    bootstrap: (): Promise<unknown> => ipcRenderer.invoke('velia:app:bootstrap') as Promise<unknown>,
  }),
  assistant: Object.freeze({
    getState: (): Promise<{ readonly snapshot: AssistantOverlaySnapshot; readonly compact: boolean }> =>
      ipcRenderer.invoke('velia:assistant:get-state') as Promise<{ readonly snapshot: AssistantOverlaySnapshot; readonly compact: boolean }>,
    publish: (snapshot: AssistantOverlaySnapshot): Promise<void> =>
      ipcRenderer.invoke('velia:assistant:publish', snapshot) as Promise<void>,
    command: (command: AssistantOverlayCommand): Promise<void> =>
      ipcRenderer.invoke('velia:assistant:command', command) as Promise<void>,
    copyAnswer: (answer: string): Promise<void> =>
      ipcRenderer.invoke('velia:assistant:copy-answer', answer) as Promise<void>,
    openMain: (): Promise<void> => ipcRenderer.invoke('velia:assistant:open-main') as Promise<void>,
    quit: (): Promise<void> => ipcRenderer.invoke('velia:assistant:quit') as Promise<void>,
    onSnapshot: (listener: (state: { readonly snapshot: AssistantOverlaySnapshot; readonly compact: boolean }) => void): (() => void) => {
      const handler = (_event: Electron.IpcRendererEvent, state: { readonly snapshot: AssistantOverlaySnapshot; readonly compact: boolean }) => listener(state);
      ipcRenderer.on('velia:assistant:snapshot', handler);
      return () => ipcRenderer.removeListener('velia:assistant:snapshot', handler);
    },
    onCommand: (listener: (command: AssistantOverlayCommand) => void): (() => void) => {
      const handler = (_event: Electron.IpcRendererEvent, command: AssistantOverlayCommand) => listener(command);
      ipcRenderer.on('velia:assistant:command', handler);
      return () => ipcRenderer.removeListener('velia:assistant:command', handler);
    },
  }),
  browser: Object.freeze({
    status: (): Promise<BrowserGatewayStatus> =>
      ipcRenderer.invoke('velia:browser:status') as Promise<BrowserGatewayStatus>,
    pairing: (): Promise<{ readonly token: string; readonly port: number }> =>
      ipcRenderer.invoke('velia:browser:pairing') as Promise<{ readonly token: string; readonly port: number }>,
    copyPairingToken: (): Promise<void> =>
      ipcRenderer.invoke('velia:browser:copy-pairing-token') as Promise<void>,
    openExtensionFolder: (): Promise<void> =>
      ipcRenderer.invoke('velia:browser:open-extension-folder') as Promise<void>,
    onStatusChanged: (listener: (status: BrowserGatewayStatus) => void): (() => void) => {
      const handler = (_event: Electron.IpcRendererEvent, status: BrowserGatewayStatus) => listener(status);
      ipcRenderer.on('velia:browser:status-changed', handler);
      return () => ipcRenderer.removeListener('velia:browser:status-changed', handler);
    },
  }),
  web: Object.freeze({
    test: (): Promise<WebTestResult> =>
      ipcRenderer.invoke('velia:web:test') as Promise<WebTestResult>,
  }),
  config: Object.freeze({
    status: (): Promise<ConfigStatusResult> => ipcRenderer.invoke('velia:config:status') as Promise<ConfigStatusResult>,
    setSecret: (provider: ProviderId, secret: string): Promise<void> =>
      ipcRenderer.invoke('velia:config:set-secret', provider, secret) as Promise<void>,
    removeSecret: (provider: ProviderId): Promise<void> =>
      ipcRenderer.invoke('velia:config:remove-secret', provider) as Promise<void>,
    profiles: (): Promise<ProviderProfilesResult> =>
      ipcRenderer.invoke('velia:config:profiles') as Promise<ProviderProfilesResult>,
    selectProfile: (profileId: ProviderProfileId): Promise<ProviderProfilesResult> =>
      ipcRenderer.invoke('velia:config:select-profile', profileId) as Promise<ProviderProfilesResult>,
    testProvider: (profileId: ProviderProfileId): Promise<ProviderTestResult> =>
      ipcRenderer.invoke('velia:config:test-provider', profileId) as Promise<ProviderTestResult>,
  }),
  conversation: Object.freeze({
    submitText: (text: string, origin: MissionOrigin = 'text', relationship: MissionRelationship | 'auto' = 'auto'): Promise<SubmitTextResult> =>
      ipcRenderer.invoke('velia:conversation:submit-text', text, origin, relationship) as Promise<SubmitTextResult>,
    cancel: (operationId: string): Promise<CancelConversationResult> =>
      ipcRenderer.invoke('velia:conversation:cancel', operationId) as Promise<CancelConversationResult>,
    historySettings: (): Promise<ConversationHistorySettings> =>
      ipcRenderer.invoke('velia:conversation:history-settings') as Promise<ConversationHistorySettings>,
    updateHistorySettings: (enabled: boolean, retentionDays: ConversationHistoryRetentionDays): Promise<ConversationHistorySettings> =>
      ipcRenderer.invoke('velia:conversation:update-history-settings', enabled, retentionDays) as Promise<ConversationHistorySettings>,
    listHistory: (): Promise<ConversationHistoryListResult> =>
      ipcRenderer.invoke('velia:conversation:list-history') as Promise<ConversationHistoryListResult>,
    getHistory: (conversationId: string): Promise<ConversationHistoryDetail> =>
      ipcRenderer.invoke('velia:conversation:get-history', conversationId) as Promise<ConversationHistoryDetail>,
    openHistory: (conversationId: string): Promise<ConversationHistoryDetail> =>
      ipcRenderer.invoke('velia:conversation:open-history', conversationId) as Promise<ConversationHistoryDetail>,
    startNewHistory: (): Promise<ConversationHistoryStartNewResult> =>
      ipcRenderer.invoke('velia:conversation:start-new-history') as Promise<ConversationHistoryStartNewResult>,
    deleteHistory: (conversationId: string): Promise<ConversationHistoryDeleteResult> =>
      ipcRenderer.invoke('velia:conversation:delete-history', conversationId) as Promise<ConversationHistoryDeleteResult>,
    clearHistory: (): Promise<ConversationHistoryClearResult> =>
      ipcRenderer.invoke('velia:conversation:clear-history') as Promise<ConversationHistoryClearResult>,
    onEvent: (listener: (event: CoreEvent) => void): (() => void) => {
      const handler = (_event: Electron.IpcRendererEvent, coreEvent: CoreEvent) => listener(coreEvent);
      ipcRenderer.on('velia:conversation:event', handler);
      return () => ipcRenderer.removeListener('velia:conversation:event', handler);
    },
  }),
  voice: Object.freeze({
    inputReady: (ready: boolean): void => ipcRenderer.send('velia:voice:input-ready', ready),
    saveAsr: (credentials: AsrCredentials): Promise<void> =>
      ipcRenderer.invoke('velia:voice:save-asr', credentials) as Promise<void>,
    removeAsr: (): Promise<void> =>
      ipcRenderer.invoke('velia:config:remove-secret', 'asr') as Promise<void>,
    testAsr: (): Promise<AsrTestResult> =>
      ipcRenderer.invoke('velia:voice:test-asr') as Promise<AsrTestResult>,
    transcribe: (wavBase64: string, options?: { readonly maxLatencyMs?: number }): Promise<VoiceTranscriptionResult> =>
      ipcRenderer.invoke('velia:voice:transcribe', wavBase64, options) as Promise<VoiceTranscriptionResult>,
    synthesize: (text: string, voiceId: EdgeVoiceId, rate: number): Promise<TtsSynthesisResult> =>
      ipcRenderer.invoke('velia:voice:synthesize', text, voiceId, rate) as Promise<TtsSynthesisResult>,
    getShortcut: (): Promise<VoiceShortcutSetting> =>
      ipcRenderer.invoke('velia:voice:get-shortcut') as Promise<VoiceShortcutSetting>,
    setShortcut: (shortcutId: VoiceShortcutId): Promise<VoiceShortcutSetting> =>
      ipcRenderer.invoke('velia:voice:set-shortcut', shortcutId) as Promise<VoiceShortcutSetting>,
    onShortcut: (listener: () => void): (() => void) => {
      const handler = () => listener();
      ipcRenderer.on('velia:voice:shortcut', handler);
      return () => ipcRenderer.removeListener('velia:voice:shortcut', handler);
    },
    onPushToTalk: (listener: (event: InputEvent) => void): (() => void) => {
      const handler = (_event: Electron.IpcRendererEvent, inputEvent: InputEvent) => listener(inputEvent);
      ipcRenderer.on('velia:voice:push-to-talk', handler);
      return () => ipcRenderer.removeListener('velia:voice:push-to-talk', handler);
    },
    onLongConversation: (listener: (event: InputEvent) => void): (() => void) => {
      const handler = (_event: Electron.IpcRendererEvent, inputEvent: InputEvent) => listener(inputEvent);
      ipcRenderer.on('velia:voice:long-conversation', handler);
      return () => ipcRenderer.removeListener('velia:voice:long-conversation', handler);
    },
  }),
  wake: Object.freeze({
    status: (): Promise<WakeWordStatus> =>
      ipcRenderer.invoke('velia:wake:status') as Promise<WakeWordStatus>,
    setEnabled: (enabled: boolean): Promise<WakeWordStatus> =>
      ipcRenderer.invoke('velia:wake:set-enabled', enabled) as Promise<WakeWordStatus>,
    detect: (wavBase64: string): Promise<WakeWordDetectionResult> =>
      ipcRenderer.invoke('velia:wake:detect', wavBase64) as Promise<WakeWordDetectionResult>,
    onPrivacy: (listener: (event: WakeWordPrivacyEvent) => void): (() => void) => {
      const handler = (_event: Electron.IpcRendererEvent, privacyEvent: WakeWordPrivacyEvent) => listener(privacyEvent);
      ipcRenderer.on('velia:wake:privacy', handler);
      return () => ipcRenderer.removeListener('velia:wake:privacy', handler);
    },
  }),
  live: Object.freeze({
    start: (voiceName: LiveVoiceName): Promise<LiveStartResult> =>
      ipcRenderer.invoke('velia:live:start', voiceName) as Promise<LiveStartResult>,
    sendAudio: (sessionId: string, audioBase64: string): Promise<LiveSendAudioResult> =>
      ipcRenderer.invoke('velia:live:send-audio', sessionId, audioBase64) as Promise<LiveSendAudioResult>,
    submitLocalTranscript: (sessionId: string, transcript: string, utteranceId: string, capturedAtUtc: string): Promise<LiveSubmitLocalTranscriptResult> =>
      ipcRenderer.invoke('velia:live:submit-local-transcript', sessionId, transcript, utteranceId, capturedAtUtc) as Promise<LiveSubmitLocalTranscriptResult>,
    stop: (sessionId: string): Promise<LiveStopResult> => ipcRenderer.invoke('velia:live:stop', sessionId) as Promise<LiveStopResult>,
    status: (): Promise<LiveStatusResult> => ipcRenderer.invoke('velia:live:status') as Promise<LiveStatusResult>,
    onEvent: (listener: (event: LiveEvent) => void): (() => void) => {
      const handler = (_event: Electron.IpcRendererEvent, liveEvent: LiveEvent) => listener(liveEvent);
      ipcRenderer.on('velia:live:event', handler);
      return () => ipcRenderer.removeListener('velia:live:event', handler);
    },
    onVisionStatus: (listener: (status: LiveVisionStatus) => void): (() => void) => {
      const handler = (_event: Electron.IpcRendererEvent, status: LiveVisionStatus) => listener(status);
      ipcRenderer.on('velia:live:vision-status', handler);
      return () => ipcRenderer.removeListener('velia:live:vision-status', handler);
    },
  }),
  vision: Object.freeze({
    listDisplays: (): Promise<readonly VisionDisplay[]> =>
      ipcRenderer.invoke('velia:vision:list-displays') as Promise<readonly VisionDisplay[]>,
    test: (): Promise<VisionTestResult> =>
      ipcRenderer.invoke('velia:vision:test') as Promise<VisionTestResult>,
    captureAndAnalyze: (displayId: string, prompt: string, model: VisionModel): Promise<VisionAnalyzeResult> =>
      ipcRenderer.invoke('velia:vision:capture-and-analyze', displayId, prompt, model) as Promise<VisionAnalyzeResult>,
  }),
  document: Object.freeze({
    chooseAndOpen: (): Promise<DocumentOpenResult | null> =>
      ipcRenderer.invoke('velia:document:choose-and-open') as Promise<DocumentOpenResult | null>,
    analyze: (documentId: string, prompt: string): Promise<DocumentAnalyzeResult> =>
      ipcRenderer.invoke('velia:document:analyze', documentId, prompt) as Promise<DocumentAnalyzeResult>,
    close: (documentId: string): Promise<DocumentCloseResult> =>
      ipcRenderer.invoke('velia:document:close', documentId) as Promise<DocumentCloseResult>,
  }),
});

contextBridge.exposeInMainWorld('velia', api);
