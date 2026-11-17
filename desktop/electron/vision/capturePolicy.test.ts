import { describe, expect, it } from 'vitest';
import { boundedCaptureSize, selectLiveSource, selectScreenSource } from './capturePolicy.js';

describe('screen capture policy', () => {
  it('bounds the longest edge without changing aspect ratio', () => {
    expect(boundedCaptureSize(3840, 2160)).toEqual({ width: 2048, height: 1152 });
    expect(boundedCaptureSize(1280, 720)).toEqual({ width: 1280, height: 720 });
  });

  it('requires an exact display mapping when multiple sources exist', () => {
    const sources = [{ display_id: '1', value: 'one' }, { display_id: '2', value: 'two' }];
    expect(selectScreenSource(sources, '2').value).toBe('two');
    expect(() => selectScreenSource(sources, '3')).toThrow(/安全匹配/);
  });

  it('accepts an empty provider id only for an unambiguous single screen', () => {
    expect(selectScreenSource([{ display_id: '', value: 'only' }], '7').value).toBe('only');
  });

  it('excludes every Velia-owned window by exact media source id', () => {
    const sources = [
      { id: 'window:100:0', display_id: '', value: 'Velia Live' },
      { id: 'window:200:0', display_id: '', value: 'Codex' },
      { id: 'screen:0:0', display_id: '7', value: 'screen' },
    ];
    expect(selectLiveSource(sources, new Set(['window:100:0']), '7').value).toBe('Codex');
  });

  it('uses the cursor display only when no external application window is available', () => {
    const sources = [
      { id: 'window:100:0', display_id: '', value: 'Velia Live' },
      { id: 'screen:0:0', display_id: '7', value: 'screen' },
    ];
    expect(selectLiveSource(sources, new Set(['window:100:0']), '7').value).toBe('screen');
  });
});
