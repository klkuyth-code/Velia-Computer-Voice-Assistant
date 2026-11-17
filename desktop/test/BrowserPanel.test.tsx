import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { BrowserPanel } from '../src/components/BrowserPanel.js';

afterEach(() => cleanup());

function installVelia(options: {
  readonly webTest?: () => Promise<{
    readonly state: 'ready' | 'error';
    readonly testedAtUtc: string;
    readonly message: string;
    readonly remainingCredits?: number;
  }>;
} = {}) {
  const unsubscribe = vi.fn();
  const api = {
    browser: {
      status: vi.fn().mockResolvedValue({ connected: false, port: 47_839 }),
      onStatusChanged: vi.fn(() => unsubscribe),
      openExtensionFolder: vi.fn().mockResolvedValue(undefined),
      copyPairingToken: vi.fn().mockResolvedValue(undefined),
    },
    system: {
      openExternalResource: vi.fn().mockResolvedValue(undefined),
    },
    config: {
      setSecret: vi.fn().mockResolvedValue(undefined),
      removeSecret: vi.fn().mockResolvedValue(undefined),
    },
    web: {
      test: vi.fn(options.webTest ?? (() => Promise.resolve({
        state: 'ready' as const,
        testedAtUtc: '2026-08-16T00:00:00.000Z',
        message: 'Firecrawl 已就绪。',
        remainingCredits: 123,
      }))),
    },
  };
  Object.defineProperty(window, 'velia', { configurable: true, value: api });
  return { api, unsubscribe };
}

describe('BrowserPanel Firecrawl configuration', () => {
  it('opens only the fixed Firecrawl resource and stores the secret through the web provider', async () => {
    const onConfigChange = vi.fn();
    const { api } = installVelia();
    render(<BrowserPanel configured={false} onConfigChange={onConfigChange} />);
    await waitFor(() => expect(api.browser.status).toHaveBeenCalledOnce());

    fireEvent.click(screen.getByRole('button', { name: '配置服务' }));
    fireEvent.click(screen.getByRole('button', { name: '获取 API' }));
    await waitFor(() => expect(api.system.openExternalResource).toHaveBeenCalledWith('firecrawlApiKeys'));

    const input = screen.getByLabelText('Firecrawl API 密钥');
    fireEvent.change(input, { target: { value: '  fc-test-placeholder  ' } });
    fireEvent.click(screen.getByRole('button', { name: '保存密钥' }));

    await waitFor(() => expect(api.config.setSecret).toHaveBeenCalledWith('web', 'fc-test-placeholder'));
    expect(onConfigChange).toHaveBeenCalledWith('configured');
    expect((input as HTMLInputElement).value).toBe('');
    expect(screen.getByText(/不会在界面中回显/u)).toBeTruthy();
  });

  it('tests Firecrawl with a fixed parameterless call while Chrome controls remain available', async () => {
    let finishTest: ((value: {
      readonly state: 'ready';
      readonly testedAtUtc: string;
      readonly message: string;
      readonly remainingCredits: number;
    }) => void) | undefined;
    const pendingTest = new Promise<{
      readonly state: 'ready';
      readonly testedAtUtc: string;
      readonly message: string;
      readonly remainingCredits: number;
    }>((resolve) => { finishTest = resolve; });
    const { api } = installVelia({ webTest: () => pendingTest });
    render(<BrowserPanel configured onConfigChange={vi.fn()} />);
    await waitFor(() => expect(api.browser.status).toHaveBeenCalledOnce());

    fireEvent.click(screen.getByRole('button', { name: '配置服务' }));
    fireEvent.click(screen.getByRole('button', { name: '检测连接' }));
    expect(api.web.test).toHaveBeenCalledWith();
    expect((screen.getByRole('button', { name: '打开扩展目录' }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole('button', { name: '复制配对令牌' }) as HTMLButtonElement).disabled).toBe(false);
    expect(screen.getAllByText('检测中')).toHaveLength(2);

    finishTest?.({
      state: 'ready',
      testedAtUtc: '2026-08-16T00:00:00.000Z',
      message: 'Firecrawl 已就绪。',
      remainingCredits: 123,
    });
    await waitFor(() => expect(screen.getAllByText('已就绪')).toHaveLength(2));
    expect(screen.getByText('Firecrawl 已就绪。 剩余额度：123。')).toBeTruthy();
  });

  it('deletes only the Firecrawl secret and reports the configuration change', async () => {
    const onConfigChange = vi.fn();
    const { api } = installVelia();
    render(<BrowserPanel configured onConfigChange={onConfigChange} />);
    await waitFor(() => expect(api.browser.status).toHaveBeenCalledOnce());

    fireEvent.click(screen.getByRole('button', { name: '配置服务' }));
    fireEvent.click(screen.getByRole('button', { name: '删除' }));
    await waitFor(() => expect(api.config.removeSecret).toHaveBeenCalledWith('web'));
    expect(onConfigChange).toHaveBeenCalledWith('notConfigured');
    expect(screen.getByText(/已从本机加密存储中删除/u)).toBeTruthy();
  });

  it('rejects an invalid Firecrawl key before it crosses IPC', async () => {
    const { api } = installVelia();
    render(<BrowserPanel configured={false} onConfigChange={vi.fn()} />);
    await waitFor(() => expect(api.browser.status).toHaveBeenCalledOnce());

    fireEvent.click(screen.getByRole('button', { name: '配置服务' }));
    fireEvent.change(screen.getByLabelText('Firecrawl API 密钥'), { target: { value: 'not-a-firecrawl-key' } });
    fireEvent.click(screen.getByRole('button', { name: '保存密钥' }));

    expect(api.config.setSecret).not.toHaveBeenCalled();
    expect(screen.getByText(/应以 fc- 开头/u)).toBeTruthy();
  });
});
