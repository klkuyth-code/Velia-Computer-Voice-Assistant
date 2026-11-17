import type { AssistantOverlaySnapshot } from '@velia/protocol';

export function createTimerAlertSnapshot(
  current: AssistantOverlaySnapshot,
  message: string,
  now = new Date(),
): AssistantOverlaySnapshot {
  return {
    phase: 'idle',
    status: '计时结束',
    transcript: '',
    answer: message,
    canStop: false,
    shortcutLabel: current.shortcutLabel,
    metrics: {},
    updatedAtUtc: now.toISOString(),
  };
}
