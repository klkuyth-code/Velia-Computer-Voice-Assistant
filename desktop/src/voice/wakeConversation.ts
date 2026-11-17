import { isVoiceConversationStop } from './voiceConversationControl';

export const MAXIMUM_WAKE_CONVERSATION_MS = 10 * 60 * 1_000;

export function isWakeConversationExit(text: string): boolean {
  return isVoiceConversationStop(text);
}

export function wakeConversationExpired(startedAt: number | undefined, now: number): boolean {
  return startedAt !== undefined && now - startedAt >= MAXIMUM_WAKE_CONVERSATION_MS;
}
