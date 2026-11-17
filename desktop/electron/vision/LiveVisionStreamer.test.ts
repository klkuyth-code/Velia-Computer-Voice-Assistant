import { afterEach, describe, expect, it, vi } from 'vitest';
import type { LiveVisionStatus } from '@velia/protocol';
import { LiveVisionStreamer } from './LiveVisionStreamer';

afterEach(() => vi.useRealTimers());

describe('LiveVisionStreamer', () => {
  it('sends at most one changed JPEG per second and skips duplicate frames', async () => {
    vi.useFakeTimers();
    const statuses: LiveVisionStatus[] = [];
    const request = vi.fn(async () => ({ sessionId: 'session-1', accepted: true }));
    const captureLiveFrame = vi.fn(async () => ({
      imageBase64: '/9j/2Q==', mimeType: 'image/jpeg' as const, digest: 'same',
      source: '记事本', displayId: 'current-window', capturedAtUtc: '2026-08-14T01:00:00Z',
      width: 1280, height: 720, inputBytes: 4,
    }));
    const streamer = new LiveVisionStreamer({ request }, { captureLiveFrame }, (status) => statuses.push(status));

    streamer.start('session-1');
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(1_000);

    expect(request).toHaveBeenCalledTimes(1);
    expect(request).toHaveBeenCalledWith('live.sendVideo', expect.objectContaining({
      sessionId: 'session-1', mimeType: 'image/jpeg',
    }), 5_000);
    expect(statuses).toContainEqual(expect.objectContaining({ active: true, frames: 1, source: '记事本' }));
    streamer.stop();
  });

  it('stops scheduling frames immediately', async () => {
    vi.useFakeTimers();
    const request = vi.fn(async () => ({ sessionId: 'session-1', accepted: true }));
    const captureLiveFrame = vi.fn(async () => ({
      imageBase64: '/9j/2Q==', mimeType: 'image/jpeg' as const, digest: crypto.randomUUID(),
      source: '桌面', displayId: 'current-window', capturedAtUtc: '2026-08-14T01:00:00Z',
      width: 800, height: 600, inputBytes: 4,
    }));
    const streamer = new LiveVisionStreamer({ request }, { captureLiveFrame }, () => undefined);

    streamer.start('session-1');
    await vi.advanceTimersByTimeAsync(0);
    streamer.stop('privacy-stop');
    const sent = request.mock.calls.length;
    await vi.advanceTimersByTimeAsync(5_000);

    expect(request).toHaveBeenCalledTimes(sent);
  });
});
