import { describe, expect, it } from 'vitest';
import { isWakeConversationExit, wakeConversationExpired } from './wakeConversation';

describe('wake conversation policy', () => {
  it.each(['停止', '结束对话。', '不用了！', '没事了'])('accepts a short explicit exit phrase: %s', (text) => {
    expect(isWakeConversationExit(text)).toBe(true);
  });

  it.each(['不要停止', '继续说', '帮我关闭浏览器', '这个任务结束后提醒我'])('does not exit on a longer unrelated request: %s', (text) => {
    expect(isWakeConversationExit(text)).toBe(false);
  });

  it('enforces a bounded ten-minute session', () => {
    expect(wakeConversationExpired(1_000, 600_999)).toBe(false);
    expect(wakeConversationExpired(1_000, 601_000)).toBe(true);
    expect(wakeConversationExpired(undefined, 999_999)).toBe(false);
  });
});
