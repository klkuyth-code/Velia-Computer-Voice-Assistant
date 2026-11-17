export const inputEventEdges = ['pressed', 'released'] as const;
export type InputEventEdge = (typeof inputEventEdges)[number];

export const voiceShortcutIds = ['f8', 'ctrlShiftSpace'] as const;
export type VoiceShortcutId = (typeof voiceShortcutIds)[number];

export const voiceShortcutKeys = ['F8', 'Ctrl+Shift+Space'] as const;
export type VoiceShortcutKey = (typeof voiceShortcutKeys)[number];
export type VoiceInputKey = VoiceShortcutKey | 'Ctrl+Alt+L';

export interface VoiceShortcutSetting {
  readonly id: VoiceShortcutId;
  readonly key: VoiceShortcutKey;
  readonly label: string;
  readonly help: string;
}

export interface InputEvent {
  readonly schema: 'velia-input-event/v1';
  readonly eventId: string;
  readonly sequence: number;
  readonly atUtc: string;
  readonly shortcut: 'voicePushToTalk' | 'voiceLongConversation';
  readonly key: VoiceInputKey;
  readonly edge: InputEventEdge;
}
