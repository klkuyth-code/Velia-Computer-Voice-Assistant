import { cleanup, render, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LivePanel, replaceCurrentTranscript } from '../src/components/LivePanel.js';

afterEach(() => cleanup());

describe('LivePanel navigation lifecycle', () => {
  it('replaces only the current Gemini transcript when local Chinese ASR handles a command', () => {
    expect(replaceCurrentTranscript(
      '第一轮正确内容\nde que 删除香梨',
      '点击当前窗口的编辑菜单',
    )).toBe('第一轮正确内容\n点击当前窗口的编辑菜单');
  });

  it('stays mounted while another settings page is visible', async () => {
    const unsubscribeEvent = vi.fn();
    const unsubscribeVision = vi.fn();
    const stop = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(window, 'velia', {
      configurable: true,
      value: {
        live: {
          onEvent: vi.fn(() => unsubscribeEvent),
          onVisionStatus: vi.fn(() => unsubscribeVision),
          status: vi.fn().mockResolvedValue({ state: 'inactive' }),
          stop,
        },
        assistant: {
          onCommand: vi.fn(() => vi.fn()),
          publish: vi.fn().mockResolvedValue(undefined),
        },
        system: { openExternalResource: vi.fn() },
      },
    });

    const view = render(<LivePanel active configured onOpenVision={vi.fn()} />);
    await waitFor(() => expect(window.velia.live.status).toHaveBeenCalledOnce());
    const panel = view.container.firstElementChild;
    expect(panel?.hasAttribute('hidden')).toBe(false);

    view.rerender(<LivePanel active={false} configured onOpenVision={vi.fn()} />);
    expect(view.container.firstElementChild).toBe(panel);
    expect(panel?.hasAttribute('hidden')).toBe(true);
    expect(unsubscribeEvent).not.toHaveBeenCalled();
    expect(unsubscribeVision).not.toHaveBeenCalled();
    expect(stop).not.toHaveBeenCalled();

    view.rerender(<LivePanel active configured onOpenVision={vi.fn()} />);
    expect(view.container.firstElementChild).toBe(panel);
    expect(panel?.hasAttribute('hidden')).toBe(false);
  });
});
