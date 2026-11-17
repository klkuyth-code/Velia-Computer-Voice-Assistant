import { useEffect, useState } from 'react';
import { FileCheck2, FilePlus2, FileSearch, LockKeyhole, RotateCcw, Send, X } from 'lucide-react';
import type { DocumentOpenResult } from '@velia/protocol';

const summaryPrompt = '请总结这份文件的核心内容，列出关键结论、重要数字和需要注意的事项。';

export function DocumentsPanel({
  modelConfigured,
}: {
  readonly modelConfigured: boolean;
}) {
  const [document, setDocument] = useState<DocumentOpenResult>();
  const [prompt, setPrompt] = useState(summaryPrompt);
  const [answer, setAnswer] = useState('');
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState<'idle' | 'opening' | 'analyzing'>('idle');

  useEffect(() => () => {
    if (document) void window.velia.document.close(document.documentId).catch(() => undefined);
  }, [document]);

  async function chooseFile(): Promise<void> {
    setBusy('opening'); setMessage('正在本机只读提取文件文字…'); setAnswer('');
    try {
      const opened = await window.velia.document.chooseAndOpen();
      if (!opened) { setMessage('已取消选择文件。'); return; }
      if (document) await window.velia.document.close(document.documentId).catch(() => undefined);
      setDocument(opened);
      setMessage(opened.truncated
        ? '文件已载入；内容较长，仅分析前 24 万字符。'
        : '文件已载入内存，不会修改原文件。');
    } catch (error: unknown) {
      setDocument(undefined); setMessage(readError(error, '文件读取失败。'));
    } finally { setBusy('idle'); }
  }

  async function closeDocument(): Promise<void> {
    const current = document;
    setDocument(undefined); setAnswer(''); setMessage('文件内容已从 Velia Core 内存中移除。');
    if (current) await window.velia.document.close(current.documentId).catch(() => undefined);
  }

  async function analyze(): Promise<void> {
    if (!document || !prompt.trim()) return;
    setBusy('analyzing'); setAnswer(''); setMessage('正在使用 DeepSeek 分析文件文字…');
    try {
      const result = await window.velia.document.analyze(document.documentId, prompt.trim());
      setAnswer(result.answer);
      setMessage(`分析完成 · ${result.model} · ${(result.providerLatencyMs / 1_000).toFixed(2)} 秒`);
    } catch (error: unknown) { setMessage(readError(error, '文件分析失败。')); }
    finally { setBusy('idle'); }
  }

  const canAnalyze = Boolean(document && modelConfigured && prompt.trim() && busy === 'idle');

  return (
    <div className="documents-layout">
      <section className="documents-source-card">
        <div className="section-heading"><div><p className="eyebrow">本机只读导入</p><h3>选择一个文件</h3></div><FileSearch size={21} /></div>
        {!document ? (
          <div className="document-drop-zone">
            <FilePlus2 size={34} />
            <strong>TXT · Markdown · CSV · PDF · DOCX</strong>
            <p>单个文件最大 20 MB；扫描版 PDF 暂不支持 OCR。</p>
            <button className="button button--primary" type="button" disabled={busy !== 'idle'} onClick={() => void chooseFile()}>
              选择文件
            </button>
          </div>
        ) : (
          <div className="document-loaded">
            <div className="document-loaded__header"><FileCheck2 size={28} /><div><strong>{document.name}</strong><span>{kindLabel(document.kind)} · {formatBytes(document.originalBytes)}</span></div><button className="icon-button" type="button" aria-label="移除文件" disabled={busy !== 'idle'} onClick={() => void closeDocument()}><X size={15} /></button></div>
            <dl className="document-facts">
              <div><dt>提取文字</dt><dd>{document.characters.toLocaleString('zh-CN')} 字符</dd></div>
              {document.pageCount && <div><dt>PDF 页数</dt><dd>{document.pageCount} 页</dd></div>}
              <div><dt>原文件</dt><dd>只读 · 未修改</dd></div>
              <div><dt>内存会话</dt><dd>关闭或更换文件即清除</dd></div>
            </dl>
            <div className="document-preview"><span>文字预览</span><p>{document.preview}</p></div>
            <button className="button button--ghost" type="button" disabled={busy !== 'idle'} onClick={() => void chooseFile()}><RotateCcw size={14} />更换文件</button>
          </div>
        )}
        <div className="document-privacy-note"><LockKeyhole size={15} /><p><strong>隐私边界</strong>只有你选择文件后才会读取；提取在本机进行。点击分析时，仅提取出的文字会发送给 DeepSeek，不上传原文件。</p></div>
      </section>

      <section className="document-analysis-card">
        <div className="section-heading"><div><p className="eyebrow">文件问答</p><h3>总结与提问</h3></div><Send size={20} /></div>
        <div className="document-quick-prompts">
          <button type="button" onClick={() => setPrompt(summaryPrompt)}>核心总结</button>
          <button type="button" onClick={() => setPrompt('请提取这份文件中的所有重要数字、日期、金额和对应含义。')}>数字与日期</button>
          <button type="button" onClick={() => setPrompt('请找出这份文件中的风险、矛盾、遗漏和需要进一步确认的问题。')}>风险检查</button>
        </div>
        <label className="document-prompt"><span>你想了解什么</span><textarea value={prompt} maxLength={4_000} disabled={busy !== 'idle'} onChange={(event) => setPrompt(event.target.value)} /></label>
        {!modelConfigured && <p className="chat-requirement">请先在“模型与对话”中配置 DeepSeek API 密钥。</p>}
        <button className="button button--primary document-analyze-button" type="button" disabled={!canAnalyze} onClick={() => void analyze()}>
          <Send size={14} />{busy === 'analyzing' ? '正在分析' : '分析文件'}
        </button>
        <div className="document-answer" aria-live="polite">
          {answer ? <p>{answer}</p> : <div><FileSearch size={25} /><span>选择文件并提出问题后，回答会显示在这里。</span></div>}
        </div>
        {message && <p className="inline-message">{message}</p>}
      </section>
    </div>
  );
}

function kindLabel(kind: DocumentOpenResult['kind']): string {
  return kind === 'pdf' ? 'PDF' : kind === 'docx' ? 'Word DOCX' : '文本文件';
}

function formatBytes(bytes: number): string {
  return bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(0)} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

function readError(error: unknown, fallback: string): string {
  if (!(error instanceof Error)) return fallback;
  const match = /(?:Error:\s*)?([a-z_]+):\s*(.+)$/iu.exec(error.message);
  return match?.[2] ?? error.message ?? fallback;
}
