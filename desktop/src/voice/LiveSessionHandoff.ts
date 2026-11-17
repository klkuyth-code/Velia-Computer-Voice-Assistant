export type LiveSessionStop = () => Promise<void>;

interface LiveSessionRegistration {
  readonly isActive: () => boolean;
  readonly stop: LiveSessionStop;
}

export class LiveSessionHandoff {
  #registration: LiveSessionRegistration | undefined;

  register(isActive: () => boolean, stop: LiveSessionStop): () => void {
    const registration = { isActive, stop };
    this.#registration = registration;
    return () => {
      if (this.#registration === registration) this.#registration = undefined;
    };
  }

  isActive(): boolean {
    return this.#registration?.isActive() ?? false;
  }

  async stopBeforeOrdinaryRecording(): Promise<boolean> {
    const registration = this.#registration;
    if (!registration?.isActive()) return false;
    await registration.stop();
    return true;
  }
}

export const liveSessionHandoff = new LiveSessionHandoff();
