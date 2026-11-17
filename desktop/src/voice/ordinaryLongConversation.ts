import type { CoreEvent } from '@velia/protocol';

type CoreTerminalEventType = Extract<CoreEvent['type'], 'completed' | 'failed' | 'cancelled'>;

/**
 * A Core operation ending is the boundary between two turns, not the end of
 * ordinary long conversation. Only the explicit stop paths owned by the voice
 * panel (shortcut/voice exit, inactivity, duration limit, or recording error)
 * may deactivate the mode.
 */
export function shouldResumeOrdinaryLongConversation(
  active: boolean,
  _eventType: CoreTerminalEventType,
): boolean {
  return active;
}
