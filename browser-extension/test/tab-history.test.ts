import { describe, expect, it } from 'vitest';
import { selectPreviousVisibleTab } from '../src/tab-history';

describe('browser history fallback tab selection', () => {
  it('returns the tab immediately to the left of a new task tab', () => {
    const selected = selectPreviousVisibleTab([
      { id: 11, index: 0 },
      { id: 12, index: 1 },
      { id: 13, index: 2 },
    ], 13);

    expect(selected?.id).toBe(12);
  });

  it('uses the right neighbor when the current tab is first', () => {
    const selected = selectPreviousVisibleTab([
      { id: 21, index: 0 },
      { id: 22, index: 1 },
    ], 21);

    expect(selected?.id).toBe(22);
  });

  it('does not close the only tab in a browser window', () => {
    expect(selectPreviousVisibleTab([{ id: 31, index: 0 }], 31)).toBeUndefined();
  });
});
