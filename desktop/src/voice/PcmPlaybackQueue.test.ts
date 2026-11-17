import { describe, expect, it, vi } from 'vitest';
import {
  AdaptivePlaybackClock,
  LIVE_PLAYBACK_CLOCK_OPTIONS,
  PcmPlaybackQueue,
  PlaybackSourceRegistry,
} from './PcmPlaybackQueue';

describe('PlaybackSourceRegistry', () => {
  it('stops and disconnects every queued source on interruption', () => {
    const registry = new PlaybackSourceRegistry();
    const first = { stop: vi.fn(), disconnect: vi.fn() };
    const second = { stop: vi.fn(), disconnect: vi.fn() };
    registry.add(first); registry.add(second);

    registry.clear();

    expect(first.stop).toHaveBeenCalledOnce();
    expect(first.disconnect).toHaveBeenCalledOnce();
    expect(second.stop).toHaveBeenCalledOnce();
    expect(second.disconnect).toHaveBeenCalledOnce();
    expect(registry.size).toBe(0);
  });
});

describe('AdaptivePlaybackClock', () => {
  it('prebuffers the first chunk and schedules following chunks without gaps', () => {
    const clock = new AdaptivePlaybackClock();

    const first = clock.schedule(10, 0.1);
    const second = clock.schedule(10.05, 0.08);

    expect(first).toBeCloseTo(10.18, 6);
    expect(second).toBeCloseTo(10.28, 6);
  });

  it('uses a bounded rebuffer after an underrun and raises the next-turn lead', () => {
    const clock = new AdaptivePlaybackClock();
    clock.schedule(0, 0.02);

    const recovered = clock.schedule(0.25, 0.02);

    expect(recovered).toBeCloseTo(0.33, 6);
    expect(clock.targetLeadSeconds).toBeCloseTo(0.22, 6);
    for (let index = 0; index < 10; index += 1) clock.schedule(1 + index, 0.01);
    expect(clock.targetLeadSeconds).toBeCloseTo(0.35, 6);
  });

  it('uses a deeper adaptive buffer for Live without changing the ordinary default', () => {
    const clock = new AdaptivePlaybackClock(LIVE_PLAYBACK_CLOCK_OPTIONS);

    const first = clock.schedule(10, 0.12);
    const second = clock.schedule(10.08, 0.10);
    const recovered = clock.schedule(11.20, 0.08);

    expect(first).toBeCloseTo(10.70, 6);
    expect(second).toBeCloseTo(10.82, 6);
    expect(recovered).toBeCloseTo(11.44, 6);
    expect(clock.targetLeadSeconds).toBeCloseTo(0.80, 6);
  });

  it('raises the Live lead after repeated underruns and keeps it bounded', () => {
    const clock = new AdaptivePlaybackClock(LIVE_PLAYBACK_CLOCK_OPTIONS);
    clock.schedule(0, 0.02);
    for (let index = 1; index <= 10; index += 1) clock.schedule(index, 0.02);

    expect(clock.targetLeadSeconds).toBeCloseTo(1.1, 6);
    clock.completeResponse();
    expect(clock.targetLeadSeconds).toBeCloseTo(1.1, 6);
  });

  it('slowly reduces latency after a clean response but never below the floor', () => {
    const clock = new AdaptivePlaybackClock();
    for (let turn = 0; turn < 8; turn += 1) {
      clock.schedule(turn, 0.2);
      clock.completeResponse();
    }

    expect(clock.targetLeadSeconds).toBeCloseTo(0.14, 6);
    expect(clock.schedule(20, 0.1)).toBeCloseTo(20.14, 6);
  });
});

describe('PcmPlaybackQueue microphone gate timing', () => {
  it('does not mark playback active during the initial prebuffer', async () => {
    vi.useFakeTimers();
    try {
      const states: boolean[] = [];
      const source = {
        buffer: undefined,
        connect: vi.fn(),
        disconnect: vi.fn(),
        start: vi.fn(),
        stop: vi.fn(),
        onended: null as (() => void) | null,
      };
      const context = {
        state: 'running',
        currentTime: 10,
        destination: {},
        createBuffer: (_channels: number, length: number, sampleRate: number) => ({
          duration: length / sampleRate,
          copyToChannel: vi.fn(),
        }),
        createBufferSource: () => source,
        resume: vi.fn(async () => undefined),
        close: vi.fn(async () => undefined),
      } as unknown as AudioContext;
      const queue = new PcmPlaybackQueue((active) => states.push(active), context);

      await queue.enqueue('AAAAAA==');
      expect(states).toEqual([]);
      vi.advanceTimersByTime(650);
      expect(states).toEqual([]);
      vi.advanceTimersByTime(100);
      expect(states).toEqual([true]);
      queue.clear();
      expect(states).toEqual([true, false]);
    } finally {
      vi.useRealTimers();
    }
  });
});
