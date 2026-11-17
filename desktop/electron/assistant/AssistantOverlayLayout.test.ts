import { describe, expect, it } from 'vitest';
import type { AssistantOverlaySnapshot } from '@velia/protocol';
import { compactAssistantSize, shouldExpandAssistant } from './AssistantOverlayLayout';

function snapshot(overrides: Partial<AssistantOverlaySnapshot> = {}): AssistantOverlaySnapshot {
  return {
    phase: 'idle',
    status: '随时待命',
    transcript: '',
    answer: '',
    canStop: false,
    shortcutLabel: 'F8',
    metrics: {},
    updatedAtUtc: new Date(0).toISOString(),
    ...overrides,
  };
}

describe('assistant overlay layout', () => {
  it('always keeps the top surface as a compact orb without transcript panels', () => {
    expect(shouldExpandAssistant(snapshot({ phase: 'recording', status: '正在聆听' }))).toBe(false);
    expect(shouldExpandAssistant(snapshot({ phase: 'speaking', answer: '已经打开。' }))).toBe(false);
    expect(compactAssistantSize).toEqual({ width: 118, height: 112 });
  });
});
