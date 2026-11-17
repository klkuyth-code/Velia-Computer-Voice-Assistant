import { appendFile, mkdir, stat, truncate } from 'node:fs/promises';
import { dirname } from 'node:path';

const MAX_LOG_BYTES = 512 * 1024;

export interface InputShortcutDiagnostics {
  rendererChanged(ready: boolean): void;
  longConversation(disposition: 'forwarded' | 'queued' | 'queue-cancelled' | 'released-ignored'): void;
}

export const noInputShortcutDiagnostics: InputShortcutDiagnostics = {
  rendererChanged: () => undefined,
  longConversation: () => undefined,
};

export class FileShortcutDiagnostics implements InputShortcutDiagnostics {
  #pending = Promise.resolve();

  constructor(private readonly path: string) {}

  longConversation(disposition: 'forwarded' | 'queued' | 'queue-cancelled' | 'released-ignored'): void {
    this.#write('long-conversation', disposition);
  }

  rendererChanged(ready: boolean): void {
    this.#write('renderer', ready ? 'ready' : 'not-ready');
  }

  #write(event: string, state: string): void {
    const line = `${JSON.stringify({ atUtc: new Date().toISOString(), shortcut: 'Ctrl+Alt+L', event, state })}\n`;
    this.#pending = this.#pending.then(() => this.#appendBounded(line)).catch((error: unknown) => {
      console.error(`Velia shortcut diagnostics failed: ${error instanceof Error ? error.message : 'unknown error'}`);
    });
  }

  async #appendBounded(line: string): Promise<void> {
    await mkdir(dirname(this.path), { recursive: true });
    const size = await stat(this.path).then((value) => value.size).catch(() => 0);
    if (size > MAX_LOG_BYTES) await truncate(this.path, 0);
    await appendFile(this.path, line, 'utf8');
  }
}
