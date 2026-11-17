import { describe, expect, it } from 'vitest';
import type { CoreEvent } from '@velia/protocol';
import { shouldForwardConversationEvent } from '../electron/core/CoreEventRouting.js';

describe('Core event routing', () => {
  it('keeps Live-delegated output out of the normal conversation renderer', () => {
    expect(shouldForwardConversationEvent(event('live'))).toBe(false);
    expect(shouldForwardConversationEvent(event('voice'))).toBe(true);
    expect(shouldForwardConversationEvent(event('text'))).toBe(true);
  });
});

function event(origin: CoreEvent['origin']): CoreEvent {
  return {
    schema: 'velia-core-event/v1',
    operationId: '1196ea21-1692-46a8-b87b-92c608429ead',
    sequence: 1,
    atUtc: '2026-08-17T00:00:00Z',
    type: 'content',
    origin,
    delta: '测试',
  };
}
