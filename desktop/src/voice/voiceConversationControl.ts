const exactStopCommands = new Set([
  '停止', '停下', '停一下', '先停一下', '结束', '退出', '不用了', '没事了',
  '好了', '行了', '可以了', '知道了', '我知道了', '好的我知道了', '好了我知道了', '行了我知道了',
  '停止对话', '结束对话', '退出对话', '关闭对话',
  '停止长对话', '结束长对话', '退出长对话', '关闭长对话',
  '停止语音', '结束语音', '停止播报', '停止回答', '停止任务',
  '停止live', '结束live', '退出live', '关闭live',
  '停止live模式', '结束live模式', '退出live模式', '关闭live模式',
]);

/**
 * One deliberately small stop grammar shared by push-to-talk, ordinary long
 * conversation, wake conversation and Live. It is anchored to the complete
 * utterance so phrases such as “不要停止” or “任务结束后提醒我” cannot cancel
 * work accidentally.
 */
export function isVoiceConversationStop(text: string): boolean {
  const normalized = text
    .normalize('NFKC')
    .trim()
    .replace(/[。！!？?，,；;：:]+/gu, '')
    .replace(/\s+/gu, '')
    .toLowerCase();
  if (!normalized || normalized.startsWith('不要') || normalized.startsWith('别')) return false;
  if (exactStopCommands.has(normalized)) return true;
  return /^(?:请|麻烦|现在|先|可以)?(?:停止|停下|停一下|结束|退出)(?:一下|吧|回答|播报|任务|语音|对话|长对话|live|live模式)?$/u.test(normalized);
}
