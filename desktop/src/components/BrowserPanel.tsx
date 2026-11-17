import { useEffect, useState } from 'react';
import {
  Check,
  Copy,
  ExternalLink,
  FolderOpen,
  KeyRound,
  PlugZap,
  Search,
  Trash2,
  X,
} from 'lucide-react';
import type { SecretConfigurationState, WebTestResult } from '@velia/protocol';
import type { ProviderState } from '../lib/presentation';
import { StatusBadge } from './StatusBadge';

interface BrowserGatewayStatus {
  readonly connected: boolean;
  readonly port: number;
  readonly extensionVersion?: string;
  readonly connectedAtUtc?: string;
}

export function BrowserPanel({
  configured,
  onConfigChange,
}: {
  readonly configured: boolean;
  readonly onConfigChange: (state: SecretConfigurationState) => void;
}) {
  const [status, setStatus] = useState<BrowserGatewayStatus>();
  const [chromeMessage, setChromeMessage] = useState('');
  const [chromeBusy, setChromeBusy] = useState(false);
  const [webApiKey, setWebApiKey] = useState('');
  const [webConfigured, setWebConfigured] = useState(configured);
  const [webState, setWebState] = useState<ProviderState>(configured ? 'configured' : 'notConfigured');
  const [webMessage, setWebMessage] = useState('');
  const [webBusy, setWebBusy] = useState(false);
  const [webConfigOpen, setWebConfigOpen] = useState(false);

  useEffect(() => {
    let active = true;
    void window.velia.browser.status().then((next) => {
      if (active) setStatus(next);
    }).catch((error: unknown) => {
      if (active) setChromeMessage(readError(error, '无法读取 Chrome 桥接状态。'));
    });
    const unsubscribe = window.velia.browser.onStatusChanged((next) => {
      if (active) setStatus(next);
    });
    return () => {
      active = false;
      unsubscribe();
    };
  }, []);

  useEffect(() => {
    setWebConfigured(configured);
    setWebState((current) => {
      if (!configured) return 'notConfigured';
      return current === 'online' || current === 'error' ? current : 'configured';
    });
  }, [configured]);

  async function runChrome(action: () => Promise<void>, success: string): Promise<void> {
    setChromeBusy(true);
    setChromeMessage('');
    try {
      await action();
      setChromeMessage(success);
    } catch (error: unknown) {
      setChromeMessage(readError(error, '浏览器桥接操作失败。'));
    } finally {
      setChromeBusy(false);
    }
  }

  async function openFirecrawlPortal(): Promise<void> {
    setWebMessage('');
    try {
      await window.velia.system.openExternalResource('firecrawlApiKeys');
      setWebMessage('已打开 Firecrawl API Keys 页面。登录后创建或复制 API Key，再返回 Velia 粘贴。');
    } catch (error: unknown) {
      setWebMessage(readError(error, '无法打开 Firecrawl API Keys 页面。'));
    }
  }

  async function saveFirecrawlKey(): Promise<void> {
    const normalized = webApiKey.trim();
    if (!normalized) return;
    if (!/^fc-[A-Za-z0-9_-]{5,16381}$/u.test(normalized)) {
      setWebMessage('Firecrawl 密钥格式不正确，应以 fc- 开头。');
      return;
    }
    setWebBusy(true);
    setWebMessage('');
    try {
      await window.velia.config.setSecret('web', normalized);
      setWebApiKey('');
      setWebConfigured(true);
      setWebState('configured');
      onConfigChange('configured');
      setWebMessage('密钥已加密保存。Velia 不会在界面中回显它。');
    } catch (error: unknown) {
      setWebMessage(readError(error, 'Firecrawl 密钥保存失败。'));
    } finally {
      setWebBusy(false);
    }
  }

  async function testFirecrawl(): Promise<void> {
    if (!webConfigured) return;
    setWebBusy(true);
    setWebState('testing');
    setWebMessage('正在检测 Firecrawl 连接，不会消耗抓取额度…');
    try {
      const result = await window.velia.web.test();
      setWebState(result.state === 'ready' ? 'online' : 'error');
      setWebMessage(formatWebTestMessage(result));
    } catch (error: unknown) {
      setWebState('error');
      setWebMessage(readError(error, 'Firecrawl 连接检测失败。'));
    } finally {
      setWebBusy(false);
    }
  }

  async function removeFirecrawlKey(): Promise<void> {
    setWebBusy(true);
    setWebMessage('');
    try {
      await window.velia.config.removeSecret('web');
      setWebApiKey('');
      setWebConfigured(false);
      setWebState('notConfigured');
      onConfigChange('notConfigured');
      setWebMessage('已从本机加密存储中删除 Firecrawl 密钥。');
    } catch (error: unknown) {
      setWebMessage(readError(error, 'Firecrawl 密钥删除失败。'));
    } finally {
      setWebBusy(false);
    }
  }

  return (
    <div className="browser-layout">
      <section className={status?.connected ? 'browser-status-card browser-status-card--ready' : 'browser-status-card'}>
        <div className="browser-status-card__icon">
          {status?.connected ? <Check size={26} /> : <PlugZap size={26} />}
        </div>
        <div>
          <h2>{status?.connected ? 'Chrome 扩展已连接' : '等待 Chrome 扩展连接'}</h2>
          <p>
            {status?.connected
              ? '网页搜索、点击、标签页和正文读取通道正常。'
              : '安装扩展并完成配对后，即可使用网页搜索与点击能力。'}
          </p>
        </div>
        <span className={status?.connected ? 'connection-pill connection-pill--ready' : 'connection-pill'}>
          {status?.connected ? '已就绪' : '未连接'}
        </span>
      </section>

      <section className="browser-setup-card browser-connection-card">
        <div className="section-heading">
          <div><p className="eyebrow">扩展管理</p><h3>Chrome 连接</h3></div>
          <PlugZap size={20} />
        </div>
        <p className="settings-copy">打开扩展目录或复制当前配对令牌。</p>
        <div className="browser-actions">
          <button
            className="button button--primary"
            type="button"
            disabled={chromeBusy}
            onClick={() => void runChrome(() => window.velia.browser.openExtensionFolder(), '扩展目录已打开。')}
          >
            <FolderOpen size={15} />打开扩展目录
          </button>
          <button
            className="button button--ghost"
            type="button"
            disabled={chromeBusy}
            onClick={() => void runChrome(() => window.velia.browser.copyPairingToken(), '配对令牌已复制。')}
          >
            <Copy size={15} />复制配对令牌
          </button>
        </div>
        {chromeMessage && <p className="inline-message" role="status">{chromeMessage}</p>}
      </section>

      <section className="browser-firecrawl-card browser-firecrawl-summary">
        <div className="section-heading">
          <div><p className="eyebrow">网页提取</p><h3>Firecrawl</h3></div>
          <StatusBadge state={webState} />
        </div>
        <p className="settings-copy">公开网页检索与正文提取服务。</p>
        <button className="button button--ghost" type="button" onClick={() => setWebConfigOpen(true)}>配置服务</button>
      </section>

      <div className={`browser-config-layer${webConfigOpen ? ' is-open' : ''}`} aria-hidden={!webConfigOpen} onMouseDown={(event) => { if (event.target === event.currentTarget) setWebConfigOpen(false); }}>
        <section className="browser-config-modal" aria-label="Firecrawl 配置">
          <header><div><h2>Firecrawl</h2><p>公开网页检索与正文提取</p></div><button type="button" aria-label="关闭 Firecrawl 配置" onClick={() => setWebConfigOpen(false)}><X size={18} /></button></header>
          <div className="section-heading__actions browser-config-resource">
            <button className="resource-link-button" type="button" onClick={() => void openFirecrawlPortal()}><ExternalLink size={13} />获取 API</button>
            <StatusBadge state={webState} />
          </div>
          <label className="secret-field">
            <span><KeyRound size={14} /> Firecrawl API 密钥</span>
            <input type="password" value={webApiKey} maxLength={16_384} disabled={webBusy} autoComplete="off" placeholder={webConfigured ? '已保存；输入新密钥可替换' : '在这里粘贴，不要发到聊天中'} onChange={(event) => setWebApiKey(event.target.value)} />
          </label>
          <div className="model-actions">
            <button className="button button--primary" type="button" disabled={webBusy || !webApiKey.trim()} onClick={() => void saveFirecrawlKey()}>保存密钥</button>
            <button className="button button--ghost" type="button" disabled={webBusy || !webConfigured} onClick={() => void testFirecrawl()}><Search size={14} />检测连接</button>
            <button className="button button--danger" type="button" disabled={webBusy || !webConfigured} onClick={() => void removeFirecrawlKey()}><Trash2 size={14} />删除</button>
          </div>
          {webMessage && <p className="inline-message" role="status">{webMessage}</p>}
        </section>
      </div>
    </div>
  );
}

function formatWebTestMessage(result: WebTestResult): string {
  if (result.state !== 'ready' || result.remainingCredits === undefined) return result.message;
  return `${result.message} 剩余额度：${result.remainingCredits}。`;
}

function readError(error: unknown, fallback: string): string {
  return error instanceof Error ? error.message : fallback;
}
