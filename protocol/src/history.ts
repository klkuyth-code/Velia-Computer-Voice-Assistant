export const conversationHistoryRetentionDays = [7, 30, 90, 365] as const;
export type ConversationHistoryRetentionDays = (typeof conversationHistoryRetentionDays)[number];

export type ConversationHistoryRole = 'user' | 'assistant';

export interface ConversationHistorySettings {
  readonly schema: 'velia-conversation-history-settings/v1';
  readonly enabled: boolean;
  readonly retentionDays: ConversationHistoryRetentionDays;
  readonly storedConversationCount: number;
  readonly protection: 'windowsCurrentUserDpapi';
}

export interface ConversationHistoryMessage {
  readonly role: ConversationHistoryRole;
  readonly text: string;
  readonly atUtc: string;
}

export interface ConversationHistorySummary {
  readonly conversationId: string;
  readonly title: string;
  readonly preview: string;
  readonly messageCount: number;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
}

export interface ConversationHistoryDetail extends ConversationHistorySummary {
  readonly schema: 'velia-conversation-history/v1';
  readonly messages: readonly ConversationHistoryMessage[];
}

export interface ConversationHistoryListResult {
  readonly settings: ConversationHistorySettings;
  readonly conversations: readonly ConversationHistorySummary[];
}

export interface UpdateConversationHistorySettingsParams extends Record<string, unknown> {
  readonly enabled: boolean;
  readonly retentionDays: ConversationHistoryRetentionDays;
}

export interface ConversationHistoryIdParams extends Record<string, unknown> {
  readonly conversationId: string;
}

export interface ConversationHistoryDeleteResult {
  readonly conversationId: string;
  readonly deleted: boolean;
}

export interface ConversationHistoryClearResult {
  readonly deletedCount: number;
}

export interface ConversationHistoryStartNewResult {
  readonly started: true;
}
