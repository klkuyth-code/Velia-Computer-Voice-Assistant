export const BRIDGE_SCHEMA = 'velia-browser-message/v1' as const;
export const BRIDGE_METHODS = ['listTabs', 'getPage', 'getArticle', 'open', 'act', 'ping'] as const;
export type BridgeMethod = (typeof BRIDGE_METHODS)[number];

export interface BridgeHello {
  readonly schema: typeof BRIDGE_SCHEMA;
  readonly type: 'hello';
  readonly token: string;
  readonly extensionVersion: string;
}

export interface BridgeRequest {
  readonly schema: typeof BRIDGE_SCHEMA;
  readonly type: 'request';
  readonly id: string;
  readonly method: BridgeMethod;
  readonly params: Readonly<Record<string, unknown>>;
}

export interface BridgeResponse {
  readonly schema: typeof BRIDGE_SCHEMA;
  readonly type: 'response';
  readonly id: string;
  readonly ok: boolean;
  readonly result?: unknown;
  readonly error?: { readonly code: string; readonly message: string };
}

export interface BridgeCancel {
  readonly schema: typeof BRIDGE_SCHEMA;
  readonly type: 'cancel';
  readonly id: string;
}

export function isBridgeRequest(value: unknown): value is BridgeRequest {
  if (!isRecord(value) || value.schema !== BRIDGE_SCHEMA || value.type !== 'request') return false;
  return typeof value.id === 'string' && value.id.length >= 8 && value.id.length <= 128 &&
    typeof value.method === 'string' && BRIDGE_METHODS.includes(value.method as BridgeMethod) &&
    isRecord(value.params);
}

export function isBridgeCancel(value: unknown): value is BridgeCancel {
  return isRecord(value) && value.schema === BRIDGE_SCHEMA && value.type === 'cancel' &&
    typeof value.id === 'string' && value.id.length >= 8 && value.id.length <= 128;
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
