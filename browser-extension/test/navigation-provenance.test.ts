import { describe, expect, it } from 'vitest';
import {
  consumeReturnPoint,
  forgetTabReturnPoints,
  peekReturnPoint,
  rememberReturnPoint,
  type NavigationReturnPoint,
} from '../src/navigation-provenance';

describe('browser navigation provenance', () => {
  it('keeps same-tab return URLs in stack order', () => {
    const state = new Map<number, NavigationReturnPoint[]>();
    rememberReturnPoint(state, 12, { kind: 'same-tab', previousUrl: 'https://example.com/search' });
    rememberReturnPoint(state, 12, { kind: 'same-tab', previousUrl: 'https://example.com/images' });

    expect(consumeReturnPoint(state, 12)).toEqual({
      kind: 'same-tab',
      previousUrl: 'https://example.com/images',
    });
    expect(peekReturnPoint(state, 12)).toEqual({
      kind: 'same-tab',
      previousUrl: 'https://example.com/search',
    });
  });

  it('remembers the source tab for links that open a new tab', () => {
    const state = new Map<number, NavigationReturnPoint[]>();
    rememberReturnPoint(state, 22, { kind: 'new-tab', sourceTabId: 21 });
    expect(peekReturnPoint(state, 22)).toEqual({ kind: 'new-tab', sourceTabId: 21 });
  });

  it('deduplicates identical consecutive points and forgets closed tabs', () => {
    const state = new Map<number, NavigationReturnPoint[]>();
    rememberReturnPoint(state, 32, { kind: 'same-tab', previousUrl: 'https://example.com/' });
    rememberReturnPoint(state, 32, { kind: 'same-tab', previousUrl: 'https://example.com/' });
    expect(state.get(32)).toHaveLength(1);
    forgetTabReturnPoints(state, 32);
    expect(peekReturnPoint(state, 32)).toBeUndefined();
  });
});
