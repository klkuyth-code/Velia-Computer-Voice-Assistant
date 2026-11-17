import { describe, expect, it } from 'vitest';
import { shouldResumeOrdinaryLongConversation } from './ordinaryLongConversation';

describe('ordinary long conversation terminal policy', () => {
  it.each(['completed', 'failed', 'cancelled'] as const)(
    'keeps listening after a %s Core operation',
    (eventType) => {
      expect(shouldResumeOrdinaryLongConversation(true, eventType)).toBe(true);
    },
  );

  it('does not restart listening after the user has exited the mode', () => {
    expect(shouldResumeOrdinaryLongConversation(false, 'completed')).toBe(false);
  });
});
