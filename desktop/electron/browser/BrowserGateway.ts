import { randomBytes, randomUUID } from 'node:crypto';
import { mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';
import { safeStorage } from 'electron';
import { WebSocket, WebSocketServer, type RawData } from 'ws';

const SCHEMA = 'velia-browser-message/v1';
const PORT = 47_839;
const MAX_MESSAGE_CHARACTERS = 1_048_576;
const REQUEST_TIMEOUT_MS = 12_000;
const methods = ['listTabs', 'getPage', 'getArticle', 'open', 'act', 'ping'] as const;
export type BrowserMethod = (typeof methods)[number];

interface PendingRequest {
  readonly resolve: (value: unknown) => void;
  readonly reject: (error: Error) => void;
  readonly timer: ReturnType<typeof setTimeout>;
}

export interface BrowserGatewayStatus {
  readonly connected: boolean;
  readonly port: number;
  readonly extensionVersion?: string;
  readonly connectedAtUtc?: string;
}

export class BrowserGateway {
  readonly #pending = new Map<string, PendingRequest>();
  readonly #listeners = new Set<(status: BrowserGatewayStatus) => void>();
  #server: WebSocketServer | undefined;
  #client: WebSocket | undefined;
  #pairingToken = '';
  #extensionVersion: string | undefined;
  #connectedAtUtc: string | undefined;

  constructor(readonly tokenPath: string, readonly port = PORT) {}

  async start(): Promise<void> {
    if (this.#server) return;
    this.#pairingToken = await loadOrCreatePairingToken(this.tokenPath);
    const server = new WebSocketServer({
      host: '127.0.0.1',
      port: this.port,
      maxPayload: MAX_MESSAGE_CHARACTERS,
      perMessageDeflate: false,
    });
    this.#server = server;
    server.on('connection', (socket, request) => this.#accept(socket, request.headers.origin));
    await new Promise<void>((resolvePromise, rejectPromise) => {
      server.once('listening', resolvePromise);
      server.once('error', rejectPromise);
    });
  }

  pairingInfo(): { readonly token: string; readonly port: number } {
    if (!this.#pairingToken) throw new Error('Browser gateway is not ready.');
    return { token: this.#pairingToken, port: this.port };
  }

  status(): BrowserGatewayStatus {
    return {
      connected: this.#client?.readyState === WebSocket.OPEN,
      port: this.port,
      ...(this.#extensionVersion ? { extensionVersion: this.#extensionVersion } : {}),
      ...(this.#connectedAtUtc ? { connectedAtUtc: this.#connectedAtUtc } : {}),
    };
  }

  subscribe(listener: (status: BrowserGatewayStatus) => void): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  request<TResult>(method: BrowserMethod, params: Readonly<Record<string, unknown>>, signal?: AbortSignal): Promise<TResult> {
    const request = this.requestCancellable<TResult>(method, params);
    if (!signal) return request.promise;
    if (signal.aborted) request.cancel();
    const abort = () => request.cancel();
    signal.addEventListener('abort', abort, { once: true });
    return request.promise.finally(() => signal.removeEventListener('abort', abort));
  }

  requestCancellable<TResult>(method: BrowserMethod, params: Readonly<Record<string, unknown>>): {
    readonly id: string;
    readonly promise: Promise<TResult>;
    readonly cancel: () => void;
  } {
    if (!methods.includes(method)) throw new Error('Browser method is not allowlisted.');
    const client = this.#client;
    if (!client || client.readyState !== WebSocket.OPEN) throw new Error('Velia Browser Bridge is not connected.');
    if (this.#pending.size >= 64) throw new Error('Browser request queue is full.');
    const id = randomUUID();
    const payload = JSON.stringify({ schema: SCHEMA, type: 'request', id, method, params });
    const promise = new Promise<TResult>((resolvePromise, rejectPromise) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        if (client.readyState === WebSocket.OPEN) {
          client.send(JSON.stringify({ schema: SCHEMA, type: 'cancel', id }));
        }
        rejectPromise(new Error('Browser request timed out.'));
      }, REQUEST_TIMEOUT_MS);
      this.#pending.set(id, {
        resolve: (value) => resolvePromise(value as TResult),
        reject: rejectPromise,
        timer,
      });
      client.send(payload, (error) => {
        if (!error) return;
        const pending = this.#pending.get(id);
        if (!pending) return;
        clearTimeout(pending.timer);
        this.#pending.delete(id);
        pending.reject(new Error('Could not send the browser request.'));
      });
    });
    const cancel = () => {
      const pending = this.#pending.get(id);
      if (!pending) return;
      clearTimeout(pending.timer);
      this.#pending.delete(id);
      if (client.readyState === WebSocket.OPEN) {
        client.send(JSON.stringify({ schema: SCHEMA, type: 'cancel', id }));
      }
      pending.reject(new Error('Browser request was cancelled.'));
    };
    return { id, promise, cancel };
  }

  async stop(): Promise<void> {
    this.#disconnect(new Error('Browser gateway stopped.'));
    const server = this.#server;
    this.#server = undefined;
    if (!server) return;
    await new Promise<void>((resolvePromise) => server.close(() => resolvePromise()));
  }

  #accept(socket: WebSocket, origin: string | undefined): void {
    if (!origin || !/^chrome-extension:\/\/[a-p]{32}$/u.test(origin)) {
      socket.close(1008, 'Chrome extension origin required');
      return;
    }
    const authenticationTimer = setTimeout(() => socket.close(1008, 'Pairing timeout'), 3_000);
    const authenticate = (data: RawData, isBinary: boolean) => {
      if (isBinary) {
        socket.close(1003, 'Text messages required');
        return;
      }
      const message = parseMessage(data);
      if (!isHello(message) || !constantTimeTokenEquals(message.token, this.#pairingToken)) {
        socket.close(1008, 'Pairing failed');
        return;
      }
      clearTimeout(authenticationTimer);
      socket.off('message', authenticate);
      this.#client?.close(1000, 'Replaced by a newer bridge connection');
      this.#client = socket;
      this.#extensionVersion = message.extensionVersion;
      this.#connectedAtUtc = new Date().toISOString();
      socket.on('message', (nextData, nextBinary) => this.#handleMessage(socket, nextData, nextBinary));
      socket.once('close', () => this.#handleClose(socket));
      socket.once('error', () => this.#handleClose(socket));
      socket.send(JSON.stringify({ schema: SCHEMA, type: 'helloAck' }));
      this.#notify();
    };
    socket.on('message', authenticate);
    socket.once('close', () => clearTimeout(authenticationTimer));
  }

  #handleMessage(socket: WebSocket, data: RawData, isBinary: boolean): void {
    if (socket !== this.#client || isBinary) return;
    const message = parseMessage(data);
    if (!isRecord(message) || message.schema !== SCHEMA) return;
    if (message.type === 'keepAlive') return;
    if (message.type !== 'response' || typeof message.id !== 'string' || typeof message.ok !== 'boolean') return;
    const pending = this.#pending.get(message.id);
    if (!pending) return;
    clearTimeout(pending.timer);
    this.#pending.delete(message.id);
    if (message.ok) {
      pending.resolve(message.result);
    } else {
      const code = isRecord(message.error) && typeof message.error.code === 'string'
        ? message.error.code
        : 'browser_action_failed';
      const detail = isRecord(message.error) && typeof message.error.message === 'string'
        ? message.error.message
        : 'Browser action failed.';
      pending.reject(new Error(`${code}: ${detail}`));
    }
  }

  #handleClose(socket: WebSocket): void {
    if (socket !== this.#client) return;
    this.#disconnect(new Error('Velia Browser Bridge disconnected.'));
  }

  #disconnect(error: Error): void {
    const client = this.#client;
    this.#client = undefined;
    this.#extensionVersion = undefined;
    this.#connectedAtUtc = undefined;
    if (client && client.readyState < WebSocket.CLOSING) client.close(1000, 'Gateway stopped');
    for (const pending of this.#pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.#pending.clear();
    this.#notify();
  }

  #notify(): void {
    const status = this.status();
    for (const listener of this.#listeners) listener(status);
  }
}

export async function loadOrCreatePairingToken(path: string): Promise<string> {
  if (!safeStorage.isEncryptionAvailable()) throw new Error('Windows credential encryption is unavailable.');
  try {
    const encrypted = Buffer.from(await readFile(path, 'utf8'), 'base64');
    const token = safeStorage.decryptString(encrypted);
    if (/^[a-f0-9]{64}$/u.test(token)) return token;
  } catch (error: unknown) {
    if (!isMissingFile(error)) throw error;
  }
  const token = randomBytes(32).toString('hex');
  const encrypted = safeStorage.encryptString(token).toString('base64');
  await mkdir(dirname(path), { recursive: true });
  const temporary = `${path}.tmp`;
  await writeFile(temporary, encrypted, { encoding: 'utf8', flag: 'w' });
  await rename(temporary, path);
  return token;
}

function parseMessage(data: RawData): unknown {
  const text = data.toString();
  if (text.length === 0 || text.length > MAX_MESSAGE_CHARACTERS) return undefined;
  try { return JSON.parse(text) as unknown; } catch { return undefined; }
}

function isHello(value: unknown): value is { token: string; extensionVersion: string } {
  return isRecord(value) && value.schema === SCHEMA && value.type === 'hello' &&
    typeof value.token === 'string' && typeof value.extensionVersion === 'string' && value.extensionVersion.length <= 40;
}

function constantTimeTokenEquals(left: string, right: string): boolean {
  if (left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index += 1) difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  return difference === 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isMissingFile(error: unknown): boolean {
  return isRecord(error) && error.code === 'ENOENT';
}
