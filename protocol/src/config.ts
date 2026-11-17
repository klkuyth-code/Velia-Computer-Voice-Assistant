export const providerIds = ['llm', 'asr', 'vision', 'web'] as const;

export type ProviderId = (typeof providerIds)[number];
export type SecretConfigurationState = 'configured' | 'notConfigured';

export interface ConfigStatusResult {
  readonly llm: SecretConfigurationState;
  readonly asr: SecretConfigurationState;
  readonly vision: SecretConfigurationState;
  readonly web: SecretConfigurationState;
}

export const providerProfileIds = ['deepseek-v4-flash'] as const;
export type ProviderProfileId = (typeof providerProfileIds)[number];
export type ProviderKind = 'deepseek';
export type ProviderConnectionState = 'notConfigured' | 'configured' | 'testing' | 'ready' | 'error';

export interface ProviderProfile {
  readonly id: ProviderProfileId;
  readonly kind: ProviderKind;
  readonly displayName: string;
  readonly model: string;
  readonly selected: boolean;
  readonly secretState: SecretConfigurationState;
  readonly connectionState: ProviderConnectionState;
  readonly supportsReasoning: boolean;
  readonly supportsTools: boolean;
  readonly lastTestedAtUtc?: string;
  readonly message?: string;
}

export interface ProviderProfilesResult {
  readonly selectedProfileId: ProviderProfileId;
  readonly profiles: readonly ProviderProfile[];
}

export interface SelectProviderProfileParams extends Record<string, unknown> {
  readonly profileId: ProviderProfileId;
}

export interface TestProviderParams extends Record<string, unknown> {
  readonly profileId: ProviderProfileId;
}

export interface ProviderTestResult {
  readonly profileId: ProviderProfileId;
  readonly state: 'ready' | 'error';
  readonly testedAtUtc: string;
  readonly message: string;
  readonly errorCode?: string;
  readonly retryable?: boolean;
}

export interface WebTestResult {
  readonly state: 'ready' | 'error';
  readonly testedAtUtc: string;
  readonly message: string;
  readonly remainingCredits?: number;
  readonly errorCode?: string;
  readonly retryable?: boolean;
}
