import { useEffect, useState } from 'react';
import { ArrowRight, Clock3, History, LockKeyhole, MessageSquarePlus, ShieldCheck, Trash2 } from 'lucide-react';
import type {
  ConversationHistoryDetail,
  ConversationHistoryListResult,
  ConversationHistoryRetentionDays,
} from '@velia/protocol';

export function HistoryPanel({
  privacyView = false,
  onContinue,
  onStartNew,
}: {
  readonly privacyView?: boolean;
  readonly onContinue: (conversation: ConversationHistoryDetail) => void;
  readonly onStartNew: () => void;
}) {
  const [result, setResult] = useState<ConversationHistoryListResult>();
  const [selected, setSelected] = useState<ConversationHistoryDetail>();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');

  useEffect(() => {
    let mounted = true;
    window.velia.conversation.listHistory()
      .then((next) => { if (mounted) setResult(next); })
      .catch((error: unknown) => { if (mounted) setMessage(readError(error, '无法读取对话历史。')); });
    return () => { mounted = false; };
  }, []);

  async function refresh(preferredId?: string): Promise<void> {
    const next = await window.velia.conversation.listHistory();
    setResult(next);
    const selectedId = preferredId ?? selected?.conversationId;
    if (!selectedId || !next.conversations.some((item) => item.conversationId === selectedId)) {
      setSelected(undefined);
      return;
    }
    setSelected(await window.velia.conversation.getHistory(selectedId));
  }

  async function choose(conversationId: string): Promise<void> {
    setBusy(true);
    setMessage('');
    try { setSelected(await window.velia.conversation.getHistory(conversationId)); }
    catch (error: unknown) { setMessage(readError(error, '无法打开这条历史记录。')); }
    finally { setBusy(false); }
  }

  async function continueConversation(): Promise<void> {
    if (!selected) return;
    setBusy(true);
    setMessage('');
    try { onContinue(await window.velia.conversation.openHistory(selected.conversationId)); }
    catch (error: unknown) { setMessage(readError(error, '无法继续这段对话。')); }
    finally { setBusy(false); }
  }

  async function startNew(): Promise<void> {
    setBusy(true);
    setMessage('');
    try {
      await window.velia.conversation.startNewHistory();
      onStartNew();
    } catch (error: unknown) {
      setMessage(readError(error, '无法开始新对话。'));
    } finally {
      setBusy(false);
    }
  }

  async function updateSettings(enabled: boolean, retentionDays: ConversationHistoryRetentionDays): Promise<void> {
    if (!enabled && !window.confirm('关闭历史记录会删除已经保存的全部对话。确定关闭吗？')) return;
    setBusy(true);
    setMessage('');
    try {
      const settings = await window.velia.conversation.updateHistorySettings(enabled, retentionDays);
      setResult({ settings, conversations: enabled ? (await window.velia.conversation.listHistory()).conversations : [] });
      if (!enabled) setSelected(undefined);
      setMessage(enabled ? `历史记录已开启，自动保留 ${retentionDays} 天。` : '历史记录已关闭，已保存内容已经清除。');
    } catch (error: unknown) {
      setMessage(readError(error, '无法更新历史设置。'));
    } finally {
      setBusy(false);
    }
  }

  async function deleteSelected(): Promise<void> {
    if (!selected || !window.confirm('确定删除这条对话吗？删除后无法恢复。')) return;
    setBusy(true);
    setMessage('');
    try {
      await window.velia.conversation.deleteHistory(selected.conversationId);
      await refresh();
      setMessage('这条对话已删除。');
    } catch (error: unknown) {
      setMessage(readError(error, '无法删除这条对话。'));
    } finally {
      setBusy(false);
    }
  }

  async function clearAll(): Promise<void> {
    if (!result?.conversations.length || !window.confirm('确定清空全部对话历史吗？此操作无法恢复。')) return;
    setBusy(true);
    setMessage('');
    try {
      const cleared = await window.velia.conversation.clearHistory();
      await refresh();
      setMessage(`已清空 ${cleared.deletedCount} 条对话。`);
    } catch (error: unknown) {
      setMessage(readError(error, '无法清空对话历史。'));
    } finally {
      setBusy(false);
    }
  }

  const settings = result?.settings;
  const conversations = result?.conversations ?? [];

  return (
    <div className="history-layout">
      <section className="history-list-card">
        <div className="section-heading">
          <div>
            <p className="eyebrow">{privacyView ? 'LOCAL PRIVACY' : 'LOCAL HISTORY'}</p>
            <h3>{privacyView ? '历史与隐私' : '对话历史'}</h3>
          </div>
          {privacyView ? <LockKeyhole size={21} /> : <History size={21} />}
        </div>
        <p className="settings-copy">只保存你和 Velia 的文字，不保存录音、截图、文件正文、工具结果或 API 密钥。内容由当前 Windows 账户加密保护。</p>

        <div className="history-privacy-row">
          <div>
            <strong>{settings?.enabled ? '本地历史已开启' : '本地历史已关闭'}</strong>
            <span>{settings?.enabled ? `自动保留 ${settings.retentionDays} 天` : '本次会话关闭后不会留下记录'}</span>
          </div>
          <button
            className={`wake-toggle${settings?.enabled ? ' wake-toggle--active' : ''}`}
            type="button"
            disabled={busy || !settings}
            aria-pressed={settings?.enabled ?? false}
            onClick={() => settings && void updateSettings(!settings.enabled, settings.retentionDays)}
          >
            {settings?.enabled ? '已开启' : '已关闭'}
          </button>
        </div>

        <label className="history-retention">
          <span><Clock3 size={14} /> 自动保留期限</span>
          <select
            value={settings?.retentionDays ?? 30}
            disabled={busy || !settings}
            onChange={(event) => settings && void updateSettings(
              settings.enabled,
              Number(event.target.value) as ConversationHistoryRetentionDays,
            )}
          >
            <option value={7}>7 天</option>
            <option value={30}>30 天</option>
            <option value={90}>90 天</option>
            <option value={365}>365 天</option>
          </select>
        </label>

        <div className="history-toolbar">
          <button className="button button--primary" type="button" disabled={busy} onClick={() => void startNew()}>
            <MessageSquarePlus size={14} />新对话
          </button>
          <button className="button button--danger" type="button" disabled={busy || conversations.length === 0} onClick={() => void clearAll()}>
            <Trash2 size={14} />全部清空
          </button>
        </div>
        {message && <p className="inline-message" role="status">{message}</p>}

        <div className="history-list" aria-label="已保存的对话">
          {!result && !message && <p className="history-empty">正在读取本机加密历史…</p>}
          {result && !settings?.enabled && <p className="history-empty">开启后，完成的普通文字和语音对话会显示在这里。</p>}
          {result && settings?.enabled && conversations.length === 0 && <p className="history-empty">还没有保存的对话。</p>}
          {conversations.map((conversation) => (
            <button
              className={`history-list-item${selected?.conversationId === conversation.conversationId ? ' history-list-item--active' : ''}`}
              type="button"
              key={conversation.conversationId}
              disabled={busy}
              onClick={() => void choose(conversation.conversationId)}
            >
              <strong>{conversation.title}</strong>
              <span>{conversation.preview}</span>
              <small>{formatTime(conversation.updatedAtUtc)} · {conversation.messageCount} 条消息</small>
            </button>
          ))}
        </div>
      </section>

      <section className="history-detail-card">
        {!selected && (
          <div className="history-detail-empty">
            <ShieldCheck size={30} />
            <strong>本机加密，随时可清除</strong>
            <p>选择左侧的一条记录可查看内容并继续对话。</p>
          </div>
        )}
        {selected && (
          <>
            <div className="history-detail-header">
              <div><p className="eyebrow">CONVERSATION</p><h3>{selected.title}</h3><span>{formatTime(selected.updatedAtUtc)}</span></div>
              <div>
                <button className="button button--danger" type="button" disabled={busy} onClick={() => void deleteSelected()}><Trash2 size={14} />删除</button>
                <button className="button button--primary" type="button" disabled={busy} onClick={() => void continueConversation()}>继续对话<ArrowRight size={14} /></button>
              </div>
            </div>
            <div className="history-messages">
              {selected.messages.map((item, index) => (
                <div className={`chat-message chat-message--${item.role}`} key={`${item.atUtc}-${index}`}>
                  <span>{item.role === 'user' ? '你' : 'Velia'}</span>
                  <p>{item.text}</p>
                </div>
              ))}
            </div>
          </>
        )}
      </section>
    </div>
  );
}

function formatTime(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? '时间未知'
    : new Intl.DateTimeFormat('zh-CN', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }).format(date);
}

function readError(error: unknown, fallback: string): string {
  if (!(error instanceof Error)) return fallback;
  const match = /(?:Error:\s*)?([a-z_]+):\s*(.+)$/iu.exec(error.message);
  return match?.[2] ?? error.message ?? fallback;
}
