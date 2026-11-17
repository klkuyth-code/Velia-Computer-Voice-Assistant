export type AudioMode = 'pushToTalk' | 'longConversation' | 'wakeCommand' | 'bargeIn' | 'live' | 'wake';

export type AudioModeListener = (owner: AudioMode | undefined) => void;

export class AudioModeCoordinator {
  #owner: AudioMode | undefined;
  readonly #listeners = new Set<AudioModeListener>();

  get owner(): AudioMode | undefined { return this.#owner; }

  acquire(mode: AudioMode): boolean {
    if (this.#owner && this.#owner !== mode) {
      if (this.#owner !== 'wake' || mode === 'wake') return false;
      this.#owner = mode;
      this.#notify();
      return true;
    }
    this.#owner = mode;
    this.#notify();
    return true;
  }

  release(mode: AudioMode): void {
    if (this.#owner !== mode) return;
    this.#owner = undefined;
    this.#notify();
  }

  subscribe(listener: AudioModeListener): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  #notify(): void {
    for (const listener of this.#listeners) listener(this.#owner);
  }
}

export const audioModeCoordinator = new AudioModeCoordinator();
