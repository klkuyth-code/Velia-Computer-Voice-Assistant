export type AsrCredentialMode = 'apiKey' | 'legacy';

export interface AsrCredentials {
  readonly mode: AsrCredentialMode;
  readonly apiKey?: string;
  readonly appId?: string;
  readonly accessToken?: string;
}

export interface VoiceTranscribeParams extends Record<string, unknown> {
  readonly wavBase64: string;
  /** Optional short deadline for Live utterances. Ordinary voice keeps the default provider timeout. */
  readonly maxLatencyMs?: number;
}

export interface VoiceTranscriptionResult {
  readonly text: string;
  readonly audioDurationMs: number;
  readonly providerLatencyMs: number;
}

export interface AsrTestResult {
  readonly state: 'ready' | 'error';
  readonly testedAtUtc: string;
  readonly message: string;
  readonly errorCode?: string;
  readonly retryable?: boolean;
}

export const edgeVoiceIds = [
  'zh-CN-XiaoxiaoNeural',
  'zh-CN-XiaoyiNeural',
  'zh-CN-YunxiNeural',
  'zh-CN-YunyangNeural',
  'zh-CN-YunjianNeural',
  'zh-CN-liaoning-XiaobeiNeural',
  'zh-CN-shaanxi-XiaoniNeural',
  'zh-TW-HsiaoChenNeural',
  'zh-HK-HiuMaanNeural',
] as const;
export type EdgeVoiceId = (typeof edgeVoiceIds)[number];

export interface EdgeVoiceOption {
  readonly id: EdgeVoiceId;
  readonly label: string;
  readonly group: 'female' | 'male' | 'dialect';
}

export interface TtsSetting {
  readonly voiceId: EdgeVoiceId;
  readonly rate: number;
}

export interface TtsSynthesisResult {
  readonly audioBase64: string;
  readonly mimeType: 'audio/mpeg';
  readonly provider: 'edgeOnline';
}
