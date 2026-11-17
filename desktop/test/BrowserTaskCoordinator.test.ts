import { describe, expect, it } from 'vitest';
import { BrowserTaskCoordinator } from '../electron/browser/BrowserTaskCoordinator.js';
import type { BrowserMethod } from '../electron/browser/BrowserGateway.js';

describe('Browser task coordinator', () => {
  it('reuses a same-site baseline tab for the fast path', async () => {
    const browser = new FakeBrowser([{ id: 1, active: true, url: 'https://www.bilibili.com/home' }]);
    const coordinator = new BrowserTaskCoordinator(browser);

    const result = await coordinator.execute('task-1', 'open', { url: 'https://search.bilibili.com/all?keyword=Velia' });

    expect(browser.openCalls).toBe(0);
    expect(browser.tabs).toHaveLength(1);
    expect(browser.tabs[0]?.url).toContain('search.bilibili.com');
    expect(result).toMatchObject({ taskNewTabCount: 0 });
  });

  it('reuses an exact tab without recapturing the complete page DOM', async () => {
    const browser = new FakeBrowser([{ id: 1, active: false, url: 'https://www.douyin.com/search/Velia' }]);
    const coordinator = new BrowserTaskCoordinator(browser);

    const result = await coordinator.execute('task-exact', 'open', { url: 'https://www.douyin.com/search/Velia' });

    expect(browser.getPageCalls).toBe(0);
    expect(browser.tabs[0]?.active).toBe(true);
    expect(result).toMatchObject({ reusedExistingTab: true });
  });

  it('returns the reconciled active tab as independent navigation evidence', async () => {
    const browser = new FakeBrowser([{ id: 1, active: true, url: 'https://www.google.com/search?q=ai' }]);
    const coordinator = new BrowserTaskCoordinator(browser);

    const result = await coordinator.execute('task-pagination', 'act', {
      tabId: 1,
      actions: [{ type: 'navigate', url: 'https://www.google.com/search?q=ai&start=50' }],
    });

    expect(result).toMatchObject({
      verifiedTab: { id: 1, active: true, url: 'https://www.google.com/search?q=ai&start=50' },
    });
  });

  it('creates at most one task tab and cleans only extra task tabs', async () => {
    const browser = new FakeBrowser([{ id: 1, active: true, url: 'https://example.org' }]);
    const coordinator = new BrowserTaskCoordinator(browser);
    await coordinator.execute('task-2', 'open', { url: 'https://bilibili.com/video/1' });
    const firstTaskTab = browser.tabs.find((tab) => tab.id !== 1)?.id;
    expect(firstTaskTab).toBeDefined();

    const preferred = browser.spawnTab('https://bilibili.com/video/2');
    const result = await coordinator.execute('task-2', 'act', { tabId: preferred, actions: [{ type: 'wait', ms: 1 }] });

    expect(browser.tabs.some((tab) => tab.id === 1)).toBe(true);
    expect(browser.tabs.some((tab) => tab.id === preferred)).toBe(true);
    expect(browser.tabs.some((tab) => tab.id === firstTaskTab)).toBe(false);
    expect(result).toMatchObject({ taskNewTabCount: 1, tabBudgetCleanupCount: 1 });
  });

  it('refuses to close a baseline user tab', async () => {
    const browser = new FakeBrowser([{ id: 1, active: true, url: 'https://example.org' }]);
    const coordinator = new BrowserTaskCoordinator(browser);

    await expect(coordinator.execute('task-3', 'act', {
      tabId: 1,
      actions: [{ type: 'close_tab', tabId: 1 }],
    })).rejects.toThrow('only a tab created by the current browser task');
    expect(browser.tabs).toHaveLength(1);
  });

  it('closes one baseline tab only after an explicit user tab-close command', async () => {
    const browser = new FakeBrowser([
      { id: 1, active: false, url: 'https://example.org' },
      { id: 2, active: true, url: 'https://www.baidu.com/search' },
    ]);
    const coordinator = new BrowserTaskCoordinator(browser);

    await coordinator.execute('task-close', 'act', {
      tabId: 2,
      userExplicitTabClose: true,
      actions: [{ type: 'close_tab', tabId: 2 }],
    });

    expect(browser.tabs.map((tab) => tab.id)).toEqual([1]);
  });

  it('preserves committed same-tab navigation when the follow-up tab listing fails', async () => {
    const browser = new FakeBrowser([{ id: 1, active: true, url: 'https://example.org/?page=1' }]);
    browser.failListCall = 2;
    const coordinator = new BrowserTaskCoordinator(browser);

    const result = await coordinator.execute('task-nav-proof', 'act', {
      tabId: 1,
      actions: [{ type: 'navigate', url: 'https://example.org/?page=3' }],
    });

    expect(result).toMatchObject({
      ok: true,
      reconciliationDeferred: true,
      finalPage: { page: { url: 'https://example.org/?page=3' } },
    });
  });

  it('keeps click actions strict when the follow-up tab listing fails', async () => {
    const browser = new FakeBrowser([{ id: 1, active: true, url: 'https://example.org/' }]);
    browser.failListCall = 2;
    const coordinator = new BrowserTaskCoordinator(browser);

    await expect(coordinator.execute('task-click-proof', 'act', {
      tabId: 1,
      actions: [{ type: 'click', ref: 'v111111111111' }],
    })).rejects.toThrow('Synthetic tab listing failure');
  });
});

interface TabState {
  id: number;
  active: boolean;
  url: string;
}

class FakeBrowser {
  tabs: TabState[];
  openCalls = 0;
  getPageCalls = 0;
  listCalls = 0;
  failListCall: number | undefined;
  #nextId: number;

  constructor(tabs: readonly TabState[]) {
    this.tabs = tabs.map((tab) => ({ ...tab }));
    this.#nextId = Math.max(0, ...tabs.map((tab) => tab.id)) + 1;
  }

  spawnTab(url: string): number {
    for (const tab of this.tabs) tab.active = false;
    const id = this.#nextId++;
    this.tabs.push({ id, active: true, url });
    return id;
  }

  async request<TResult>(method: BrowserMethod, params: Readonly<Record<string, unknown>>, _signal?: AbortSignal): Promise<TResult> {
    let result: unknown;
    if (method === 'listTabs') {
      this.listCalls += 1;
      if (this.listCalls === this.failListCall) throw new Error('Synthetic tab listing failure');
      result = { tabs: this.tabs.map((tab) => ({ ...tab })) };
    } else if (method === 'open') {
      this.openCalls += 1;
      const id = this.spawnTab(String(params.url));
      result = this.page(id);
    } else if (method === 'getPage') {
      this.getPageCalls += 1;
      result = this.page(Number(params.tabId));
    } else if (method === 'act') {
      let tabId = Number(params.tabId);
      for (const raw of Array.isArray(params.actions) ? params.actions : []) {
        const action = raw as Record<string, unknown>;
        if (action.type === 'switch_tab') {
          tabId = Number(action.tabId);
          for (const tab of this.tabs) tab.active = tab.id === tabId;
        } else if (action.type === 'navigate') {
          const tab = this.tabs.find((item) => item.id === tabId)!;
          tab.url = String(action.url);
        } else if (action.type === 'close_tab') {
          this.tabs = this.tabs.filter((item) => item.id !== Number(action.tabId));
        }
      }
      result = { ok: true, steps: Array.isArray(params.actions)
        ? params.actions.map((action) => ({ type: (action as Record<string, unknown>).type, ok: true }))
        : [], finalPage: this.page(tabId) };
    } else {
      result = { pong: true };
    }
    return result as TResult;
  }

  private page(id: number): Record<string, unknown> {
    const tab = this.tabs.find((item) => item.id === id);
    return { tab: tab ? { ...tab } : { id }, page: { url: tab?.url ?? '' } };
  }
}
