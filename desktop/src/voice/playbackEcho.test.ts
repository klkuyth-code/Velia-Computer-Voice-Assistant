import { describe, expect, it } from 'vitest';
import { isLikelyPlaybackEcho } from './playbackEcho';

describe('playback echo guard', () => {
  it('rejects an exact or slightly truncated copy of Velia playback', () => {
    const playback = '今天下午三点，我会继续完成语音助手测试。';
    expect(isLikelyPlaybackEcho('今天下午三点我会继续完成语音助手测试', playback)).toBe(true);
    expect(isLikelyPlaybackEcho('下午三点我会继续完成语音助手测试', playback)).toBe(true);
  });

  it('allows an unrelated user interruption', () => {
    expect(isLikelyPlaybackEcho('先别说了，帮我打开浏览器', '今天下午三点，我会继续完成语音助手测试。')).toBe(false);
  });

  it('does not suppress short confirmations solely because one word overlaps', () => {
    expect(isLikelyPlaybackEcho('好的', '好的，我现在开始整理文件。')).toBe(false);
  });
});
