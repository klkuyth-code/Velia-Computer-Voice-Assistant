import type { CoreEvent } from '@velia/protocol';

/**
 * A voice-initiated task must provide audible terminal feedback even when the
 * verified outcome is a refusal or conflict. User cancellation stays silent.
 */
export function coreFailureSpeechText(event: CoreEvent): string | undefined {
  if (event.type !== 'failed') return undefined;
  const message = event.error?.message?.trim();
  return message || '任务没有完成。';
}
