import { useEffect, useRef, useState } from 'react';
import { Bot, ExternalLink, KeyRound, Send, Square, Trash2, Wifi } from 'lucide-react';
import type {
  CoreEvent,
  ConversationHistoryDetail,
  ProviderConnectionState,
  ProviderProfile,
  SecretConfigurationState,
} from '@velia/protocol';
import type { ProviderState } from '../lib/presentation';
import { StatusBadge } from './StatusBadge';

interface ChatItem {
  readonly id: string;
  readonly role: 'user' | 'assistant';
  readonly text: string;
}

export function ModelsPanel({
  onLlmConfigChange,
  initialConversation,
}: {
  readonly onLlmConfigChange: (state: SecretConfigurationState) => void;
  readonly initialConversation?: ConversationHistoryDetail;
}) {
  const [profile, setProfile] = useState<ProviderProfile>();
  const [apiKey, setApiKey] = useState('');
  const [providerBusy, setProviderBusy] = useState(false);
  const [providerMessage, setProviderMessage] = useState('');
  const [messages, setMessages] = useState<readonly ChatItem[]>([]);
  const [input, setInput] = useState('');
  const [operationId, setOperationId] = useState<string>();
  const [conversationState, setConversationState] = useState('等待输入');
  const terminalOperations = useRef(new Set<string>());
  const submittingControl = useRef(false);
  const toolNames = useRef(new Map<number, string>());
  const chatEnd = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let mounted = true;
    window.velia.config.profiles()
      .then((result) => {
        if (mounted) setProfile(result.profiles[0]);
      })
      .catch((error: unknown) => {
        if (mounted) setProviderMessage(readError(error, '无法读取模型配置。'));
      });

    const unsubscribe = window.velia.conversation.onEvent((event) => {
      if (mounted) handleCoreEvent(event);
    });
    return () => {
      mounted = false;
      unsubscribe();
    };
  }, []);

  useEffect(() => {
    chatEnd.current?.scrollIntoView({ behavior: 'smooth', block: 'end' });
  }, [messages]);

  useEffect(() => {
    if (!initialConversation) return;
    terminalOperations.current.clear();
    toolNames.current.clear();
    setOperationId(undefined);
    setMessages(initialConversation.messages.map((message, index) => ({
      id: `${initialConversation.conversationId}-${index}`,
      role: message.role,
      text: message.text,
    })));
    setConversationState('已载入历史，可继续对话');
  }, [initialConversation]);

  async function refreshProfile(): Promise<void> {
    const result = await window.velia.config.profiles();
    setProfile(result.profiles[0]);
  }

  async function saveKey(): Promise<void> {
    const normalized = apiKey.trim();
    if (!normalized) return;
    setProviderBusy(true);
    setProviderMessage('');
    try {
      await window.velia.config.setSecret('llm', normalized);
      setApiKey('');
      await refreshProfile();
      onLlmConfigChange('configured');
      setProviderMessage('密钥已加密保存。Velia 不会在界面中回显它。');
    } catch (error: unknown) {
      setProviderMessage(readError(error, '密钥保存失败。'));
    } finally {
      setProviderBusy(false);
    }
  }

  async function removeKey(): Promise<void> {
    setProviderBusy(true);
    setProviderMessage('');
    try {
      await window.velia.config.removeSecret('llm');
      await refreshProfile();
      onLlmConfigChange('notConfigured');
      setProviderMessage('已从本机加密存储中删除密钥。');
    } catch (error: unknown) {
      setProviderMessage(readError(error, '密钥删除失败。'));
    } finally {
      setProviderBusy(false);
    }
  }

  async function testProvider(): Promise<void> {
    if (!profile) return;
    setProviderBusy(true);
    setProviderMessage('正在进行小流量连接检测…');
    setProfile({ ...profile, connectionState: 'testing' });
    try {
      const result = await window.velia.config.testProvider(profile.id);
      setProviderMessage(result.message);
      await refreshProfile();
    } catch (error: unknown) {
      setProviderMessage(readError(error, '连接检测失败。'));
      await refreshProfile().catch(() => undefined);
    } finally {
      setProviderBusy(false);
    }
  }

  async function openApiPortal(): Promise<void> {
    setProviderMessage('');
    try {
      await window.velia.system.openExternalResource('deepseekApiKeys');
      setProviderMessage('已打开 DeepSeek API Keys 页面。登录后创建或复制 API Key，再返回 Velia 粘贴。');
    } catch (error: unknown) {
      setProviderMessage(readError(error, '无法打开 DeepSeek API Keys 页面。'));
    }
  }

  async function submitText(): Promise<void> {
    const text = input.trim();
    if (!text) return;
    const userMessage: ChatItem = { id: crypto.randomUUID(), role: 'user', text };
    const assistantMessage: ChatItem = { id: crypto.randomUUID(), role: 'assistant', text: '' };
    setMessages((current) => [...current, userMessage, assistantMessage]);
    setInput('');
    setConversationState('正在提交');
    submittingControl.current = Boolean(operationId);
    try {
      const result = await window.velia.conversation.submitText(text, 'text', 'auto');
      if (!terminalOperations.current.has(result.operationId)) {
        setOperationId(result.operationId);
      }
    } catch (error: unknown) {
      appendAssistantError(readError(error, '无法开始对话。'));
      setConversationState('提交失败');
    } finally {
      submittingControl.current = false;
    }
  }

  async function cancel(): Promise<void> {
    if (!operationId) return;
    try {
      await window.velia.conversation.cancel(operationId);
      setConversationState('正在停止');
    } catch (error: unknown) {
      setConversationState(readError(error, '停止失败'));
    }
  }

  function handleCoreEvent(event: CoreEvent): void {
    if (!terminalOperations.current.has(event.operationId)) {
      setOperationId((current) => current ?? event.operationId);
    }

    switch (event.type) {
      case 'accepted':
        setConversationState('已接收');
        break;
      case 'state':
        setConversationState(event.state === 'responding' ? '正在回答' : '正在思考');
        break;
      case 'reasoning':
        setConversationState('正在思考');
        break;
      case 'content':
        if (event.delta) appendAssistant(event.delta);
        break;
      case 'tool':
        if (event.toolCall?.name) toolNames.current.set(event.toolCall.index, event.toolCall.name);
        setConversationState(toolStatus(toolNames.current.get(event.toolCall?.index ?? -1)));
        break;
      case 'mission':
        if (event.mission?.state === 'awaitingInput') setConversationState('等待你的信息');
        else if (event.mission?.state === 'correcting') setConversationState('正在应用纠正');
        else if (event.mission?.state === 'running') setConversationState('任务执行中');
        break;
      case 'completed':
        finishOperation(event.operationId, '回答完成');
        break;
      case 'cancelled':
        if (!submittingControl.current) appendAssistantError('回答已停止。');
        finishOperation(event.operationId, '已停止');
        break;
      case 'failed':
        appendAssistantError(event.error?.message ?? '回答生成失败。');
        finishOperation(event.operationId, '回答失败');
        break;
    }
  }

  function finishOperation(id: string, state: string): void {
    terminalOperations.current.add(id);
    if (terminalOperations.current.size > 100) {
      terminalOperations.current.clear();
      terminalOperations.current.add(id);
    }
    setOperationId((current) => current === id ? undefined : current);
    toolNames.current.clear();
    setConversationState(state);
  }

  function appendAssistant(delta: string): void {
    setMessages((current) => updateLastAssistant(current, (text) => text + delta));
  }

  function appendAssistantError(message: string): void {
    setMessages((current) => updateLastAssistant(current, (text) =>
      text ? `${text}\n\n[${message}]` : `[${message}]`));
  }

  const configured = profile?.secretState === 'configured';
  const canChat = configured;

  return (
    <div className="models-layout">
      <section className="model-settings-card">
        <div className="section-heading">
          <div><h3>{profile?.displayName ?? 'DeepSeek V4 Flash'}</h3></div>
          <div className="section-heading__actions">
            <button className="resource-link-button" type="button" onClick={() => void openApiPortal()}>
              <ExternalLink size={13} />获取 API
            </button>
            <StatusBadge state={connectionBadge(profile?.connectionState)} />
          </div>
        </div>
        <p className="settings-copy">密钥只保存在当前 Windows 用户的加密存储中。</p>
        <label className="secret-field">
          <span><KeyRound size={14} /> DeepSeek API 密钥</span>
          <input
            type="password"
            value={apiKey}
            disabled={providerBusy}
            autoComplete="off"
            placeholder={configured ? '已保存；输入新密钥可替换' : '在这里粘贴，不要发到聊天中'}
            onChange={(event) => setApiKey(event.target.value)}
          />
        </label>
        <div className="model-actions">
          <button className="button button--primary" type="button" disabled={providerBusy || !apiKey.trim()} onClick={() => void saveKey()}>
            保存密钥
          </button>
          <button className="button button--ghost" type="button" disabled={providerBusy || !configured} onClick={() => void testProvider()}>
            <Wifi size={14} /> 检测连接
          </button>
          <button className="button button--danger" type="button" disabled={providerBusy || !configured} onClick={() => void removeKey()}>
            <Trash2 size={14} /> 删除
          </button>
        </div>
        {providerMessage && <p className="inline-message">{providerMessage}</p>}
      </section>

      <section className="chat-card">
        <div className="chat-card__header">
          <div><h3>与 Velia 对话</h3></div>
          <span>{conversationState}</span>
        </div>
        <div className="chat-messages" aria-live="polite">
          {messages.length === 0 && (
            <div className="chat-empty"><Bot size={28} /><strong>先从一条文字消息开始</strong><p>可以对话，也可以明确要求 Velia 读取或操作当前浏览器。</p></div>
          )}
          {messages.map((message) => (
            <div className={`chat-message chat-message--${message.role}`} key={message.id}>
              <span>{message.role === 'user' ? '你' : 'Velia'}</span>
              <p>{message.text || '…'}</p>
            </div>
          ))}
          <div ref={chatEnd} />
        </div>
        {!configured && <p className="chat-requirement">请先保存模型 API 密钥。</p>}
        <div className="chat-composer">
          <textarea
            value={input}
            maxLength={32_768}
            disabled={!canChat}
            placeholder={canChat ? (operationId ? '可输入“补充…”“改成…”“进度”或“停止”' : '输入消息；Enter 发送，Shift+Enter 换行') : '完成上方配置后即可输入'}
            onChange={(event) => setInput(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault();
                void submitText();
              }
            }}
          />
          <div className="composer-actions">
            {operationId && <button className="composer-button composer-button--stop" type="button" aria-label="停止回答" onClick={() => void cancel()}><Square size={17} /></button>}
            <button className="composer-button" type="button" aria-label="发送消息" disabled={!canChat || !input.trim()} onClick={() => void submitText()}><Send size={18} /></button>
          </div>
        </div>
      </section>
    </div>
  );
}

function connectionBadge(state: ProviderConnectionState | undefined): ProviderState {
  return state === 'ready' ? 'online' : state ?? 'notConfigured';
}

function toolStatus(name: string | undefined): string {
  switch (name) {
    case 'web_research': return '正在检索网页';
    case 'browser': return '正在操作浏览器';
    case 'desktop_context': return '正在处理当前内容';
    case 'desktop_visual': return '正在操作当前桌面';
    case 'local_desktop_action': return '正在快速操作当前窗口';
    case 'windows_control': return '正在执行系统操作';
    default: return '正在使用工具';
  }
}

function updateLastAssistant(
  messages: readonly ChatItem[],
  update: (text: string) => string,
): readonly ChatItem[] {
  const index = messages.findLastIndex((message) => message.role === 'assistant');
  if (index < 0) return messages;
  return messages.map((message, messageIndex) =>
    messageIndex === index ? { ...message, text: update(message.text) } : message);
}

function readError(error: unknown, fallback: string): string {
  if (!(error instanceof Error)) return fallback;
  const match = /(?:Error:\s*)?([a-z_]+):\s*(.+)$/iu.exec(error.message);
  return match?.[2] ?? error.message ?? fallback;
}
