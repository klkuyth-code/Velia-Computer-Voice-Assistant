import { describe, expect, it } from 'vitest';
import { assistantVisualState } from '../src/components/AssistantOverlay';

describe('assistant overlay three-state presentation', () => {
  it.each(['idle', 'starting', 'transcribing', 'thinking', 'error'] as const)(
    'keeps %s in the idle blue family',
    (phase) => expect(assistantVisualState(phase)).toBe('idle'),
  );

  it('uses cyan-green only while listening', () => {
    expect(assistantVisualState('recording')).toBe('listening');
  });

  it.each(['responding', 'speaking'] as const)(
    'uses blue-purple while answering in %s',
    (phase) => expect(assistantVisualState(phase)).toBe('answering'),
  );
});
