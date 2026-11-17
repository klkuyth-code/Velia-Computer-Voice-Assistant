import type { InputEvent } from '@velia/protocol';
import type { InputShortcutDiagnostics } from './ShortcutDiagnostics.js';

export class LongConversationInputBridge {
  #rendererReady = false;
  #pending: InputEvent | undefined;

  constructor(
    private readonly publish: (event: InputEvent) => boolean,
    private readonly diagnostics: InputShortcutDiagnostics,
  ) {}

  setRendererReady(ready: boolean): void {
    this.#rendererReady = ready;
    this.diagnostics.rendererChanged(ready);
    if (!ready || !this.#pending) return;
    if (!this.publish(this.#pending)) {
      this.#rendererReady = false;
      this.diagnostics.rendererChanged(false);
      return;
    }
    this.#pending = undefined;
    this.diagnostics.longConversation('forwarded');
  }

  forward(event: InputEvent): void {
    if (event.edge === 'released') {
      this.diagnostics.longConversation('released-ignored');
      return;
    }
    if (!this.#rendererReady) {
      if (this.#pending) {
        this.#pending = undefined;
        this.diagnostics.longConversation('queue-cancelled');
      } else {
        this.#pending = event;
        this.diagnostics.longConversation('queued');
      }
      return;
    }
    if (this.publish(event)) {
      this.diagnostics.longConversation('forwarded');
      return;
    }
    this.#rendererReady = false;
    this.#pending = event;
    this.diagnostics.rendererChanged(false);
    this.diagnostics.longConversation('queued');
  }
}
