import { describe, expect, it } from 'vitest';
import { isBridgeCancel, isBridgeRequest } from '../src/protocol';

describe('bridge protocol', () => {
  it('accepts allowlisted requests and rejects arbitrary methods', () => {
    const common = {
      schema: 'velia-browser-message/v1',
      type: 'request',
      id: 'request-1234',
      params: {},
    };
    expect(isBridgeRequest({ ...common, method: 'getPage' })).toBe(true);
    expect(isBridgeRequest({ ...common, method: 'getArticle' })).toBe(true);
    expect(isBridgeRequest({ ...common, method: 'executeJavaScript' })).toBe(false);
  });
});

describe('bridge cancellation', () => {
  it('accepts only bounded cancellation identifiers', () => {
    expect(isBridgeCancel({ schema: 'velia-browser-message/v1', type: 'cancel', id: 'request-123' })).toBe(true);
    expect(isBridgeCancel({ schema: 'velia-browser-message/v1', type: 'cancel', id: 'x' })).toBe(false);
  });
});
