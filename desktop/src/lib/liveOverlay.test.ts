import { describe, expect, it } from 'vitest';
import { latestLiveTurn, liveOverlayPhase } from './liveOverlay';

describe('Live assistant overlay presentation', () => {
  it('maps Live runtime states onto the shared orb phases', () => {
    expect(liveOverlayPhase('connecting')).toBe('starting');
    expect(liveOverlayPhase('listening')).toBe('recording');
    expect(liveOverlayPhase('speaking')).toBe('speaking');
    expect(liveOverlayPhase('reconnecting')).toBe('starting');
    expect(liveOverlayPhase('error')).toBe('error');
  });

  it('shows only the current Live turn instead of an ever-growing transcript', () => {
    expect(latestLiveTurn('第一轮\n第二轮正在继续')).toBe('第二轮正在继续');
    expect(latestLiveTurn('')).toBe('');
  });
});
