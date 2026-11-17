import { useEffect, useState } from 'react';
import { Camera, ExternalLink, Eye, KeyRound, MonitorUp, RefreshCw, Trash2, Wifi } from 'lucide-react';
import type {
  SecretConfigurationState,
  VisionAnalyzeResult,
  VisionDisplay,
  VisionModel,
} from '@velia/protocol';
import { StatusBadge } from './StatusBadge';

const defaultPrompt = '请准确描述当前屏幕的主要内容，并指出重要文字、窗口和可操作区域。';

export function VisionPanel({
  configured,
  onConfigChange,
}: {
  readonly configured: boolean;
  readonly onConfigChange: (state: SecretConfigurationState) => void;
}) {
  const [apiKey, setApiKey] = useState('');
  const [model, setModel] = useState<VisionModel>('gemini-3.5-flash-lite');
  const [displays, setDisplays] = useState<readonly VisionDisplay[]>([]);
  const [displayId, setDisplayId] = useState('');
  const [prompt, setPrompt] = useState(defaultPrompt);
  const [busy, setBusy] = useState<'idle' | 'saving' | 'testing' | 'capturing' | 'analyzing'>('idle');
  const [message, setMessage] = useState('');
  const [result, setResult] = useState<VisionAnalyzeResult>();

  useEffect(() => { void refreshDisplays(); }, []);

  async function refreshDisplays(): Promise<void> {
    try {
      const next = await window.velia.vision.listDisplays();
      setDisplays(next);
      setDisplayId((current) => next.some((item) => item.id === current)
        ? current
        : next.find((item) => item.primary)?.id ?? next[0]?.id ?? '');
    } catch (error: unknown) {
      setMessage(readError(error, '无法读取显示器列表。'));
    }
  }

  async function saveKey(): Promise<void> {
    const normalized = apiKey.trim();
    if (!normalized) return;
    setBusy('saving'); setMessage('');
    try {
      await window.velia.config.setSecret('vision', normalized);
      setApiKey(''); onConfigChange('configured');
      setMessage('Gemini API 密钥已加密保存，不会在界面中回显。');
    } catch (error: unknown) { setMessage(readError(error, '密钥保存失败。')); }
    finally { setBusy('idle'); }
  }

  async function removeKey(): Promise<void> {
    setBusy('saving'); setMessage('');
    try {
      await window.velia.config.removeSecret('vision');
      onConfigChange('notConfigured'); setResult(undefined);
      setMessage('已从本机加密存储中删除 Gemini API 密钥。');
    } catch (error: unknown) { setMessage(readError(error, '密钥删除失败。')); }
    finally { setBusy('idle'); }
  }

  async function testConnection(): Promise<void> {
    setBusy('testing'); setMessage('正在小流量检测 Gemini 连接…');
    try {
      const test = await window.velia.vision.test();
      setMessage(test.message);
    } catch (error: unknown) { setMessage(readError(error, '连接检测失败。')); }
    finally { setBusy('idle'); }
  }

  async function captureAndAnalyze(): Promise<void> {
    if (!displayId || !prompt.trim()) return;
    setResult(undefined); setMessage('Velia 将短暂隐藏，然后只截取一次所选屏幕…'); setBusy('capturing');
    try {
      setBusy('analyzing');
      const analysis = await window.velia.vision.captureAndAnalyze(displayId, prompt.trim(), model);
      setResult(analysis);
      setMessage(`分析完成 · Gemini 用时 ${(analysis.providerLatencyMs / 1_000).toFixed(2)} 秒 · 截图 ${(analysis.inputBytes / 1024).toFixed(0)} KB`);
    } catch (error: unknown) { setMessage(readError(error, '屏幕分析失败。')); }
    finally { setBusy('idle'); }
  }

  async function openApiPortal(): Promise<void> {
    try {
      await window.velia.system.openExternalResource('geminiApiKeys');
      setMessage('已打开 Google AI Studio API Keys 页面。创建或复制 API Key，再返回 Velia 粘贴。');
    } catch (error: unknown) { setMessage(readError(error, '无法打开 Gemini API Keys 页面。')); }
  }

  const active = busy !== 'idle';
  const canAnalyze = configured && Boolean(displayId) && Boolean(prompt.trim()) && !active;

  return (
    <div className="vision-layout">
      <section className="vision-capture-card">
        <div className="section-heading">
          <div><p className="eyebrow">一次性屏幕理解</p><h3>查看当前画面</h3></div>
          <StatusBadge state={configured ? 'configured' : 'notConfigured'} />
        </div>
        <div className={`vision-preview vision-preview--${busy}`}>
          <Eye size={38} />
          <strong>{busy === 'capturing' ? '正在截图' : busy === 'analyzing' ? 'Gemini 正在理解画面' : '仅在你点击时查看'}</strong>
          <span>不持续录屏 · 不在磁盘保存截图</span>
        </div>
        <div className="vision-fields">
          <label className="select-field"><span>视觉模型</span>
            <select value={model} disabled={active} onChange={(event) => setModel(event.target.value as VisionModel)}>
              <option value="gemini-3.5-flash-lite">极速 · Gemini 3.5 Flash Lite</option>
              <option value="gemini-3.6-flash">高质量 · Gemini 3.6 Flash</option>
            </select>
          </label>
          <label className="select-field"><span>要查看的显示器</span>
            <div className="display-select-row">
              <select value={displayId} disabled={active} onChange={(event) => setDisplayId(event.target.value)}>
                {displays.map((display) => <option value={display.id} key={display.id}>{display.name} · {display.width}×{display.height}{display.primary ? ' · 主屏' : ''}</option>)}
              </select>
              <button className="icon-button" type="button" aria-label="刷新显示器" disabled={active} onClick={() => void refreshDisplays()}><RefreshCw size={15} /></button>
            </div>
          </label>
          <label className="vision-prompt"><span>你想了解什么</span>
            <textarea value={prompt} maxLength={4_000} disabled={active} onChange={(event) => setPrompt(event.target.value)} />
          </label>
        </div>
        {!configured && <p className="chat-requirement">请先保存 Gemini API 密钥。</p>}
        <button className="button button--primary vision-main-button" type="button" disabled={!canAnalyze} onClick={() => void captureAndAnalyze()}>
          <Camera size={15} /> {busy === 'capturing' ? '正在截图' : busy === 'analyzing' ? '正在分析' : '截图并分析'}
        </button>
      </section>

      <div className="vision-side-stack">
        <section className="vision-settings-card">
          <div className="section-heading">
            <div><p className="eyebrow">视觉模型配置</p><h3>Google Gemini</h3></div>
            <button className="resource-link-button" type="button" onClick={() => void openApiPortal()}><ExternalLink size={13} />获取 API</button>
          </div>
          <p className="settings-copy">接口和模型由 Velia Core 固定。密钥使用当前 Windows 账户的 DPAPI 加密，界面不会回显。</p>
          <p className="credential-help">按钮直达 Google AI Studio 的 API Key 页面。国内网络可能需要能够访问 Google 服务的网络环境。</p>
          <label className="secret-field"><span><KeyRound size={14} /> Gemini API 密钥</span>
            <input type="password" value={apiKey} disabled={active} autoComplete="off" placeholder={configured ? '已保存；输入新密钥可替换' : '在这里粘贴，不要发到聊天中'} onChange={(event) => setApiKey(event.target.value)} />
          </label>
          <div className="model-actions">
            <button className="button button--primary" type="button" disabled={active || apiKey.trim().length < 8} onClick={() => void saveKey()}>保存密钥</button>
            <button className="button button--ghost" type="button" disabled={active || !configured} onClick={() => void testConnection()}><Wifi size={14} />检测连接</button>
            <button className="button button--danger" type="button" disabled={active || !configured} onClick={() => void removeKey()}><Trash2 size={14} />删除</button>
          </div>
        </section>

        <section className="vision-result-card" aria-live="polite">
          <div className="section-heading"><div><p className="eyebrow">视觉回答</p><h3>画面分析结果</h3></div><MonitorUp size={20} /></div>
          {result ? <p className="vision-result-text">{result.text}</p> : <div className="vision-result-empty"><Eye size={24} /><span>分析结果会显示在这里，原始截图不会保留。</span></div>}
          {message && <p className="inline-message">{message}</p>}
        </section>
      </div>
    </div>
  );
}

function readError(error: unknown, fallback: string): string {
  if (!(error instanceof Error)) return fallback;
  const match = /(?:Error:\s*)?([a-z_]+):\s*(.+)$/iu.exec(error.message);
  return match?.[2] ?? error.message ?? fallback;
}
