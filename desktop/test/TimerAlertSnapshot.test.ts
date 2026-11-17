import { describe, expect, it } from 'vitest';
import { createTimerAlertSnapshot } from '../electron/assistant/TimerAlertSnapshot.js';

describe('timer alert snapshot', () => {
  it('creates a visible bounded overlay without inheriting conversation content', () => {
    const snapshot = createTimerAlertSnapshot({
      phase: 'speaking',
      status: '旧状态',
      transcript: '不应保留',
      answer: '旧回答',
      canStop: true,
      shortcutLabel: 'Ctrl + Shift + 空格',
      metrics: { totalMs: 1200 },
      updatedAtUtc: '2026-08-16T00:00:00.000Z',
    }, '计时结束。', new Date('2026-08-16T09:00:00.000Z'));

    expect(snapshot).toEqual({
      phase: 'idle',
      status: '计时结束',
      transcript: '',
      answer: '计时结束。',
      canStop: false,
      shortcutLabel: 'Ctrl + Shift + 空格',
      metrics: {},
      updatedAtUtc: '2026-08-16T09:00:00.000Z',
    });
  });
});
