import { describe, expect, it, vi } from 'vitest';
import { LiveSessionHandoff } from './LiveSessionHandoff';

describe('LiveSessionHandoff', () => {
  it('stops an active Live session before ordinary recording', async () => {
    const handoff = new LiveSessionHandoff();
    const stop = vi.fn(async () => undefined);
    handoff.register(() => true, stop);

    expect(handoff.isActive()).toBe(true);
    await expect(handoff.stopBeforeOrdinaryRecording()).resolves.toBe(true);
    expect(stop).toHaveBeenCalledOnce();
  });

  it('does not disturb an inactive or unregistered Live panel', async () => {
    const handoff = new LiveSessionHandoff();
    const stop = vi.fn(async () => undefined);
    const unregister = handoff.register(() => false, stop);

    expect(handoff.isActive()).toBe(false);
    await expect(handoff.stopBeforeOrdinaryRecording()).resolves.toBe(false);
    unregister();
    expect(handoff.isActive()).toBe(false);
    await expect(handoff.stopBeforeOrdinaryRecording()).resolves.toBe(false);
    expect(stop).not.toHaveBeenCalled();
  });
});
