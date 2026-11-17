import { describe, expect, it } from 'vitest';
import type { CoreEvent } from '@velia/protocol';
import { coreFailureSpeechText } from './coreFailureSpeech';

describe('ordinary voice failure speech', () => {
  it('speaks a verified local conflict instead of leaving text-only feedback', () => {
    expect(coreFailureSpeechText(event('failed', '桌面上已经存在“小鹿乱撞.txt”，为避免覆盖没有创建新文件。')))
      .toBe('桌面上已经存在“小鹿乱撞.txt”，为避免覆盖没有创建新文件。');
  });

  it('uses a bounded fallback when Core provides no message', () => {
    expect(coreFailureSpeechText(event('failed'))).toBe('任务没有完成。');
  });

  it('does not speak completion or user cancellation as a failure', () => {
    expect(coreFailureSpeechText(event('cancelled'))).toBeUndefined();
    expect(coreFailureSpeechText(event('completed'))).toBeUndefined();
  });
});

function event(type: CoreEvent['type'], message?: string): CoreEvent {
  return {
    schema: 'velia-core-event/v1',
    operationId: '1196ea21-1692-46a8-b87b-92c608429ead',
    sequence: 1,
    atUtc: '2026-08-21T15:00:00Z',
    type,
    origin: 'voice',
    ...(type === 'failed'
      ? { error: { code: 'document_already_exists', message: message ?? '', retryable: false } }
      : {}),
  };
}
