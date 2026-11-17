import type { EdgeVoiceId, EdgeVoiceOption, TtsSetting } from '@velia/protocol';

export const ttsStorageKey = 'velia.tts.v1';

export const edgeVoices: readonly EdgeVoiceOption[] = [
  { id: 'zh-CN-XiaoxiaoNeural', label: '晓晓（默认，温暖自然）', group: 'female' },
  { id: 'zh-CN-XiaoyiNeural', label: '晓伊（活泼灵动）', group: 'female' },
  { id: 'zh-CN-YunxiNeural', label: '云希（极富磁性）', group: 'male' },
  { id: 'zh-CN-YunyangNeural', label: '云扬（新闻播报风格）', group: 'male' },
  { id: 'zh-CN-YunjianNeural', label: '云健（浑厚有力）', group: 'male' },
  { id: 'zh-CN-liaoning-XiaobeiNeural', label: '晓贝（爽朗东北话）', group: 'dialect' },
  { id: 'zh-CN-shaanxi-XiaoniNeural', label: '晓妮（朴实陕西话）', group: 'dialect' },
  { id: 'zh-TW-HsiaoChenNeural', label: '晓臻（嗲雅台湾腔）', group: 'dialect' },
  { id: 'zh-HK-HiuMaanNeural', label: '晓曼（粤语女声）', group: 'dialect' },
];

export const defaultTtsSetting = { voiceId: 'zh-CN-XiaoxiaoNeural', rate: 20 } as const;

export function loadTtsSetting(): TtsSetting {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(ttsStorageKey) ?? 'null');
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return defaultTtsSetting;
    const setting = parsed as Record<string, unknown>;
    const voiceId = setting.voiceId;
    const rate = setting.rate;
    if (typeof voiceId !== 'string' || !edgeVoices.some((voice) => voice.id === voiceId) ||
        typeof rate !== 'number' || !Number.isInteger(rate) || rate < -50 || rate > 50 || rate % 5 !== 0) {
      return defaultTtsSetting;
    }
    return { voiceId: voiceId as EdgeVoiceId, rate };
  } catch {
    return defaultTtsSetting;
  }
}
