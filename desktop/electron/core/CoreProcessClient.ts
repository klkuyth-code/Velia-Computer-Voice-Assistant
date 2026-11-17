import { randomUUID } from 'node:crypto';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { createInterface, type Interface } from 'node:readline';
import { app } from 'electron';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { existsSync } from 'node:fs';
import type { CoreEvent, InputEvent, LiveEvent, RpcMethod } from '@velia/protocol';
import { isCoreEvent, isInputEvent, isLiveEvent, isRpcResponse } from './CoreMessage.js';

const MAX_RESPONSE_CHARACTERS = 1_048_576;
const DEFAULT_TIMEOUT_MS = 8_000;
const STARTUP_TIMEOUT_MS = 30_000;

interface PendingRequest {
  readonly resolve: (result: unknown) => void;
  readonly reject: (error: Error) => void;
  readonly timer: ReturnType<typeof setTimeout>;
}

interface CoreLaunch {
  readonly executable: string;
  readonly args: readonly string[];
}

export class CoreProcessClient {
  readonly #pending = new Map<string, PendingRequest>();
  readonly #eventListeners = new Set<(event: CoreEvent) => void>();
  readonly #inputListeners = new Set<(event: InputEvent) => void>();
  readonly #liveListeners = new Set<(event: LiveEvent) => void>();
  #process: ChildProcessWithoutNullStreams | undefined;
  #lines: Interface | undefined;

  async start(): Promise<void> {
    if (this.#process) {
      return;
    }

    const launch = resolveCoreLaunch();
    const child = spawn(launch.executable, launch.args, {
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
      shell: false,
      env: {
        ...process.env,
        DOTNET_CLI_TELEMETRY_OPTOUT: '1',
      },
    });

    this.#process = child;
    this.#lines = createInterface({ input: child.stdout, crlfDelay: Infinity });
    this.#lines.on('line', (line) => this.#handleLine(line));
    child.stderr.on('data', (chunk: Buffer) => {
      const safeLine = chunk.toString('utf8').split(/\r?\n/u)[0]?.slice(0, 240);
      if (safeLine) {
        console.error(`[Velia Core] ${safeLine}`);
      }
    });
    child.once('exit', (code) => this.#handleExit(code));
    child.once('error', (error) => this.#handleProcessError(error));

    await this.request('system.health', {}, STARTUP_TIMEOUT_MS);
  }

  async request<TResult>(
    method: RpcMethod,
    params: Record<string, unknown>,
    timeoutMs = DEFAULT_TIMEOUT_MS,
  ): Promise<TResult> {
    const child = this.#process;
    if (!child || child.killed || !child.stdin.writable) {
      throw new Error('Velia Core is not running.');
    }

    const id = randomUUID();
    const message = JSON.stringify({
      schema: 'velia-rpc-request/v1',
      id,
      method,
      params,
    });

    return new Promise<TResult>((resolvePromise, rejectPromise) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        rejectPromise(new Error('Velia Core request timed out.'));
      }, timeoutMs);

      this.#pending.set(id, {
        resolve: (value) => resolvePromise(value as TResult),
        reject: rejectPromise,
        timer,
      });

      child.stdin.write(`${message}\n`, 'utf8', (error) => {
        if (error) {
          const pending = this.#pending.get(id);
          if (pending) {
            clearTimeout(pending.timer);
            this.#pending.delete(id);
            pending.reject(new Error('Could not send request to Velia Core.'));
          }
        }
      });
    });
  }

  subscribe(listener: (event: CoreEvent) => void): () => void {
    this.#eventListeners.add(listener);
    return () => this.#eventListeners.delete(listener);
  }

  subscribeInput(listener: (event: InputEvent) => void): () => void {
    this.#inputListeners.add(listener);
    return () => this.#inputListeners.delete(listener);
  }

  subscribeLive(listener: (event: LiveEvent) => void): () => void {
    this.#liveListeners.add(listener);
    return () => this.#liveListeners.delete(listener);
  }

  stop(): void {
    this.#lines?.close();
    this.#lines = undefined;
    const child = this.#process;
    this.#process = undefined;
    if (child && !child.killed) {
      child.kill();
    }
    this.#rejectAll(new Error('Velia Core stopped.'));
  }

  #handleLine(line: string): void {
    if (line.length === 0 || line.length > MAX_RESPONSE_CHARACTERS) {
      this.#rejectAll(new Error('Velia Core returned an invalid message size.'));
      return;
    }

    let message: unknown;
    try {
      message = JSON.parse(line) as unknown;
    } catch {
      this.#rejectAll(new Error('Velia Core returned invalid JSON.'));
      return;
    }

    if (isCoreEvent(message)) {
      for (const listener of this.#eventListeners) {
        try {
          listener(message);
        } catch (error: unknown) {
          console.error(`Velia Core event listener failed: ${error instanceof Error ? error.message : 'unknown error'}`);
        }
      }
      return;
    }

    if (isInputEvent(message)) {
      for (const listener of this.#inputListeners) {
        try {
          listener(message);
        } catch (error: unknown) {
          console.error(`Velia input event listener failed: ${error instanceof Error ? error.message : 'unknown error'}`);
        }
      }
      return;
    }

    if (isLiveEvent(message)) {
      for (const listener of this.#liveListeners) {
        try { listener(message); }
        catch (error: unknown) { console.error(`Velia Live event listener failed: ${error instanceof Error ? error.message : 'unknown error'}`); }
      }
      return;
    }

    if (!isRpcResponse(message)) {
      this.#rejectAll(new Error('Velia Core returned an unsupported message.'));
      return;
    }

    const response = message;
    const pending = this.#pending.get(response.id);
    if (!pending) {
      return;
    }

    clearTimeout(pending.timer);
    this.#pending.delete(response.id);
    if (response.ok) {
      pending.resolve(response.result);
    } else {
      pending.reject(new Error(`${response.error.code}: ${response.error.message}`));
    }
  }

  #handleExit(code: number | null): void {
    this.#process = undefined;
    this.#lines?.close();
    this.#lines = undefined;
    this.#rejectAll(new Error(`Velia Core exited unexpectedly (${code ?? 'unknown'}).`));
  }

  #handleProcessError(error: Error): void {
    this.#process = undefined;
    this.#rejectAll(new Error(`Velia Core failed to start: ${error.message}`));
  }

  #rejectAll(error: Error): void {
    for (const pending of this.#pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.#pending.clear();
  }
}

function resolveCoreLaunch(): CoreLaunch {
  const explicit = process.env.VELIA_CORE_PATH;
  if (explicit) {
    return { executable: resolve(explicit), args: [] };
  }

  if (app.isPackaged) {
    return { executable: join(process.resourcesPath, 'core', 'Velia.Core.exe'), args: [] };
  }

  const currentDirectory = dirname(fileURLToPath(import.meta.url));
  const workspace = resolve(currentDirectory, '..', '..');
  const dotnet = resolve(workspace, '..', '.tools', 'dotnet', 'dotnet.exe');
  const coreDll = resolve(
    workspace,
    '..',
    'core',
    'src',
    'Velia.Core',
    'bin',
    'Release',
    'net10.0-windows',
    'Velia.Core.dll',
  );
  if (process.env.VELIA_SMOKE_TEST === '1') {
    const debugCoreDll = resolve(
      workspace,
      '..',
      'core',
      'src',
      'Velia.Core',
      'bin',
      'Debug',
      'net10.0-windows',
      'Velia.Core.dll',
    );
    if (!existsSync(debugCoreDll)) {
      throw new Error('Velia smoke Core has not been built in Debug configuration.');
    }
    return { executable: dotnet, args: [debugCoreDll] };
  }
  return { executable: dotnet, args: [coreDll] };
}
