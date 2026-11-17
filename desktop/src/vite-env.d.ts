/// <reference types="vite/client" />

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

interface BrowserGatewayStatus {
  readonly connected: boolean;
  readonly port: number;
  readonly extensionVersion?: string;
  readonly connectedAtUtc?: string;
}

declare global {
  interface Window {
    readonly velia: {
      readonly system: {
        readonly health: () => Promise<HealthResult>;
        readonly openExternalResource: (resourceId: 'deepseekApiKeys' | 'volcengineSpeechApiKeys' | 'geminiApiKeys' | 'firecrawlApiKeys') => Promise<void>;
      };
      readonly app: {
        readonly bootstrap: () => Promise<unknown>;
      };
      readonly assistant: {
        readonly getState: () => Promise<{ readonly snapshot: AssistantOverlaySnapshot; readonly compact: boolean }>;
        readonly publish: (snapshot: AssistantOverlaySnapshot) => Promise<void>;
        readonly command: (command: AssistantOverlayCommand) => Promise<void>;
        readonly copyAnswer: (answer: string) => Promise<void>;
        readonly openMain: () => Promise<void>;
        readonly quit: () => Promise<void>;
        readonly onSnapshot: (listener: (state: { readonly snapshot: AssistantOverlaySnapshot; readonly compact: boolean }) => void) => () => void;
        readonly onCommand: (listener: (command: AssistantOverlayCommand) => void) => () => void;
      };
      readonly browser: {
        readonly status: () => Promise<BrowserGatewayStatus>;
        readonly pairing: () => Promise<{ readonly token: string; readonly port: number }>;
        readonly copyPairingToken: () => Promise<void>;
        readonly openExtensionFolder: () => Promise<void>;
        readonly onStatusChanged: (listener: (status: BrowserGatewayStatus) => void) => () => void;
      };
      readonly web: {
        readonly test: () => Promise<WebTestResult>;
      };
      readonly config: {
        readonly status: () => Promise<ConfigStatusResult>;
        readonly setSecret: (provider: ProviderId, secret: string) => Promise<void>;
        readonly removeSecret: (provider: ProviderId) => Promise<void>;
        readonly profiles: () => Promise<ProviderProfilesResult>;
        readonly selectProfile: (profileId: ProviderProfileId) => Promise<ProviderProfilesResult>;
        readonly testProvider: (profileId: ProviderProfileId) => Promise<ProviderTestResult>;
      };
      readonly conversation: {
        readonly submitText: (text: string, origin?: MissionOrigin, relationship?: MissionRelationship | 'auto') => Promise<SubmitTextResult>;
        readonly cancel: (operationId: string) => Promise<CancelConversationResult>;
        readonly historySettings: () => Promise<ConversationHistorySettings>;
        readonly updateHistorySettings: (enabled: boolean, retentionDays: ConversationHistoryRetentionDays) => Promise<ConversationHistorySettings>;
        readonly listHistory: () => Promise<ConversationHistoryListResult>;
        readonly getHistory: (conversationId: string) => Promise<ConversationHistoryDetail>;
        readonly openHistory: (conversationId: string) => Promise<ConversationHistoryDetail>;
        readonly startNewHistory: () => Promise<ConversationHistoryStartNewResult>;
        readonly deleteHistory: (conversationId: string) => Promise<ConversationHistoryDeleteResult>;
        readonly clearHistory: () => Promise<ConversationHistoryClearResult>;
        readonly onEvent: (listener: (event: CoreEvent) => void) => () => void;
      };
      readonly voice: {
        readonly inputReady: (ready: boolean) => void;
        readonly saveAsr: (credentials: AsrCredentials) => Promise<void>;
        readonly removeAsr: () => Promise<void>;
        readonly testAsr: () => Promise<AsrTestResult>;
        readonly transcribe: (wavBase64: string, options?: { readonly maxLatencyMs?: number }) => Promise<VoiceTranscriptionResult>;
        readonly synthesize: (text: string, voiceId: EdgeVoiceId, rate: number) => Promise<TtsSynthesisResult>;
        readonly getShortcut: () => Promise<VoiceShortcutSetting>;
        readonly setShortcut: (shortcutId: VoiceShortcutId) => Promise<VoiceShortcutSetting>;
        readonly onShortcut: (listener: () => void) => () => void;
        readonly onPushToTalk: (listener: (event: InputEvent) => void) => () => void;
        readonly onLongConversation: (listener: (event: InputEvent) => void) => () => void;
      };
      readonly wake: {
        readonly status: () => Promise<WakeWordStatus>;
        readonly setEnabled: (enabled: boolean) => Promise<WakeWordStatus>;
        readonly detect: (wavBase64: string) => Promise<WakeWordDetectionResult>;
        readonly onPrivacy: (listener: (event: WakeWordPrivacyEvent) => void) => () => void;
      };
      readonly live: {
        readonly start: (voiceName: LiveVoiceName) => Promise<LiveStartResult>;
        readonly sendAudio: (sessionId: string, audioBase64: string) => Promise<LiveSendAudioResult>;
        readonly submitLocalTranscript: (sessionId: string, transcript: string, utteranceId: string, capturedAtUtc: string) => Promise<LiveSubmitLocalTranscriptResult>;
        readonly stop: (sessionId: string) => Promise<LiveStopResult>;
        readonly status: () => Promise<LiveStatusResult>;
        readonly onEvent: (listener: (event: LiveEvent) => void) => () => void;
        readonly onVisionStatus: (listener: (status: LiveVisionStatus) => void) => () => void;
      };
      readonly vision: {
        readonly listDisplays: () => Promise<readonly VisionDisplay[]>;
        readonly test: () => Promise<VisionTestResult>;
        readonly captureAndAnalyze: (displayId: string, prompt: string, model: VisionModel) => Promise<VisionAnalyzeResult>;
      };
      readonly document: {
        readonly chooseAndOpen: () => Promise<DocumentOpenResult | null>;
        readonly analyze: (documentId: string, prompt: string) => Promise<DocumentAnalyzeResult>;
        readonly close: (documentId: string) => Promise<DocumentCloseResult>;
      };
    };
  }
}

export {};
