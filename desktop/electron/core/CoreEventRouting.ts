import type { CoreEvent } from '@velia/protocol';

/** Live-delegated operations still reach local tool orchestrators, but their
 * conversation stream belongs to Gemini Live rather than the normal renderer. */
export function shouldForwardConversationEvent(event: CoreEvent): boolean {
  return event.origin !== 'live';
}
