import type { LiveSendVideoResult, LiveVisionStatus } from '@velia/protocol';
import type { CoreProcessClient } from '../core/CoreProcessClient.js';
import type { ScreenCaptureService } from './ScreenCaptureService.js';

const FRAME_INTERVAL_MS = 1_000;

export class LiveVisionStreamer {
  #sessionId: string | undefined;
  #timer: ReturnType<typeof setInterval> | undefined;
  #inFlight = false;
  #lastDigest = '';
  #frames = 0;

  constructor(
    private readonly core: Pick<CoreProcessClient, 'request'>,
    private readonly capture: Pick<ScreenCaptureService, 'captureLiveFrame'>,
    private readonly publish: (status: LiveVisionStatus) => void,
  ) {}

  get activeSessionId(): string | undefined { return this.#sessionId; }

  start(sessionId: string): void {
    this.stop('restarting');
    this.#sessionId = sessionId;
    this.#frames = 0;
    this.#lastDigest = '';
    this.publish({ active: true, frames: 0, reason: 'starting' });
    void this.#captureAndSend();
    this.#timer = setInterval(() => void this.#captureAndSend(), FRAME_INTERVAL_MS);
  }

  stop(reason = 'stopped'): void {
    if (this.#timer) clearInterval(this.#timer);
    this.#timer = undefined;
    this.#sessionId = undefined;
    this.#lastDigest = '';
    this.publish({ active: false, frames: this.#frames, reason });
  }

  async #captureAndSend(): Promise<void> {
    const sessionId = this.#sessionId;
    if (!sessionId || this.#inFlight) return;
    this.#inFlight = true;
    try {
      const frame = await this.capture.captureLiveFrame();
      if (this.#sessionId !== sessionId || frame.digest === this.#lastDigest) return;
      const result = await this.core.request<LiveSendVideoResult>('live.sendVideo', {
        sessionId,
        imageBase64: frame.imageBase64,
        mimeType: frame.mimeType,
      }, 5_000);
      if (this.#sessionId !== sessionId || !result.accepted) return;
      this.#lastDigest = frame.digest;
      this.#frames += 1;
      this.publish({
        active: true,
        frames: this.#frames,
        source: frame.source,
        width: frame.width,
        height: frame.height,
      });
    } catch (error: unknown) {
      if (this.#sessionId === sessionId) {
        this.publish({
          active: false,
          frames: this.#frames,
          error: error instanceof Error ? error.message.slice(0, 240) : '实时画面共享失败。',
        });
      }
    } finally {
      this.#inFlight = false;
    }
  }
}
