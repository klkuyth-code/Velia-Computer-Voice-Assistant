import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { HistoryPanel } from '../src/components/HistoryPanel.js';

afterEach(() => cleanup());

function installVelia() {
  const detail = {
    schema: 'velia-conversation-history/v1' as const,
    conversationId: '1196ea21-1692-46a8-b87b-92c608429ead',
    title: '测试历史',
    preview: '这是回答',
    messageCount: 2,
    createdAtUtc: '2026-08-16T01:00:00.000Z',
    updatedAtUtc: '2026-08-16T01:01:00.000Z',
    messages: [
      { role: 'user' as const, text: '这是问题', atUtc: '2026-08-16T01:00:00.000Z' },
      { role: 'assistant' as const, text: '这是回答', atUtc: '2026-08-16T01:01:00.000Z' },
    ],
  };
  const api = {
    conversation: {
      listHistory: vi.fn().mockResolvedValue({
        settings: {
          schema: 'velia-conversation-history-settings/v1',
          enabled: true,
          retentionDays: 30,
          storedConversationCount: 1,
          protection: 'windowsCurrentUserDpapi',
        },
        conversations: [detail],
      }),
      getHistory: vi.fn().mockResolvedValue(detail),
      openHistory: vi.fn().mockResolvedValue(detail),
      startNewHistory: vi.fn().mockResolvedValue({ started: true }),
      updateHistorySettings: vi.fn().mockResolvedValue({
        schema: 'velia-conversation-history-settings/v1',
        enabled: false,
        retentionDays: 30,
        storedConversationCount: 0,
        protection: 'windowsCurrentUserDpapi',
      }),
      deleteHistory: vi.fn().mockResolvedValue({ conversationId: detail.conversationId, deleted: true }),
      clearHistory: vi.fn().mockResolvedValue({ deletedCount: 1 }),
    },
  };
  Object.defineProperty(window, 'velia', { configurable: true, value: api });
  Object.defineProperty(window, 'confirm', { configurable: true, value: vi.fn(() => true) });
  return { api, detail };
}

describe('HistoryPanel', () => {
  it('loads a local conversation and continues it through the fixed API', async () => {
    const { api, detail } = installVelia();
    const onContinue = vi.fn();
    render(<HistoryPanel onContinue={onContinue} onStartNew={vi.fn()} />);

    await waitFor(() => expect(api.conversation.listHistory).toHaveBeenCalledOnce());
    fireEvent.click(screen.getByRole('button', { name: /测试历史/u }));
    await waitFor(() => expect(screen.getByText('这是问题')).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: /继续对话/u }));

    await waitFor(() => expect(api.conversation.openHistory).toHaveBeenCalledWith(detail.conversationId));
    expect(onContinue).toHaveBeenCalledWith(detail);
  });

  it('requires an explicit confirmation before disabling and clearing history', async () => {
    const { api } = installVelia();
    render(<HistoryPanel onContinue={vi.fn()} onStartNew={vi.fn()} />);
    await waitFor(() => expect(api.conversation.listHistory).toHaveBeenCalledOnce());

    fireEvent.click(screen.getByRole('button', { name: '已开启' }));
    await waitFor(() => expect(api.conversation.updateHistorySettings).toHaveBeenCalledWith(false, 30));
    expect(window.confirm).toHaveBeenCalledWith(expect.stringContaining('关闭历史记录'));
    expect(screen.getByText('历史记录已关闭，已保存内容已经清除。')).toBeTruthy();
  });
});
