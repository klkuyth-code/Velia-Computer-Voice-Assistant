import { describe, expect, it } from 'vitest';
import { isVoiceConversationStop } from './voiceConversationControl';

describe('shared voice conversation stop grammar', () => {
  it.each([
    '停止', '请停一下。', '结束对话！', '停止长对话', '停止回答',
    '关闭 Live 模式', '不用了', '没事了', '好了，我知道了', '好的我知道了', '可以了',
  ])('accepts a complete stop utterance in every voice mode: %s', (text) => {
    expect(isVoiceConversationStop(text)).toBe(true);
  });

  it.each([
    '不要停止', '别停下', '继续说', '帮我关闭浏览器',
    '这个任务结束后提醒我', '停止搜索后打开记事本', '好了，继续说下一项',
  ])('does not cancel a longer or negated request: %s', (text) => {
    expect(isVoiceConversationStop(text)).toBe(false);
  });
});
