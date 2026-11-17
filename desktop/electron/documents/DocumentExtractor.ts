import { open } from 'node:fs/promises';
import { basename, extname, resolve } from 'node:path';
import mammoth from 'mammoth';
import { PDFParse } from 'pdf-parse';
import type { DocumentExtractionResult, DocumentKind } from '@velia/protocol';

export const MAX_DOCUMENT_BYTES = 20 * 1024 * 1024;
export const MAX_DOCUMENT_CHARACTERS = 80_000;
const allowedExtensions = new Map<string, DocumentKind>([
  ['.txt', 'text'], ['.md', 'text'], ['.markdown', 'text'], ['.csv', 'text'], ['.log', 'text'],
  ['.pdf', 'pdf'], ['.docx', 'docx'],
]);

export interface DocumentFileBinding {
  readonly sizeBytes: number;
  readonly lastWriteTimeUnixMs: number;
  readonly creationTimeUnixMs: number;
}

export async function extractDocument(
  filePath: string,
  binding?: DocumentFileBinding,
): Promise<DocumentExtractionResult> {
  if (typeof filePath !== 'string' || filePath.length === 0 || filePath.length > 32_768) {
    throw new TypeError('文件路径无效。');
  }
  const absolutePath = resolve(filePath);
  const extension = extname(absolutePath).toLowerCase();
  const kind = allowedExtensions.get(extension);
  if (!kind) throw new Error('只支持 TXT、Markdown、CSV、PDF 和 DOCX 文件。');
  const handle = await open(absolutePath, 'r');
  let buffer: Buffer | undefined;
  try {
    const before = await handle.stat();
    if (!before.isFile() || before.size <= 0) throw new Error('所选项目不是有效文件。');
    if (before.size > MAX_DOCUMENT_BYTES) throw new Error('单个文件最大支持 20 MB。');
    if (binding && (before.size !== binding.sizeBytes ||
        Math.abs(before.mtimeMs - binding.lastWriteTimeUnixMs) > 1 ||
        Math.abs(before.birthtimeMs - binding.creationTimeUnixMs) > 1)) {
      throw new Error('选中文档在读取前发生变化，请重新选择。');
    }

    buffer = Buffer.allocUnsafe(before.size);
    let offset = 0;
    while (offset < buffer.length) {
      const { bytesRead } = await handle.read(buffer, offset, buffer.length - offset, offset);
      if (bytesRead <= 0) throw new Error('选中文档在读取期间发生变化，请重新选择。');
      offset += bytesRead;
    }
    const extra = Buffer.alloc(1);
    try {
      const { bytesRead } = await handle.read(extra, 0, 1, offset);
      if (bytesRead !== 0) throw new Error('选中文档在读取期间发生变化，请重新选择。');
    } finally {
      extra.fill(0);
    }
    const after = await handle.stat();
    if (after.size !== before.size || after.dev !== before.dev || after.ino !== before.ino ||
        after.mtimeMs !== before.mtimeMs || after.ctimeMs !== before.ctimeMs) {
      throw new Error('选中文档在读取期间发生变化，请重新选择。');
    }

    const parsed = await parseByKind(buffer, kind);
    const normalized = normalizeText(parsed.text);
    if (!normalized) throw new Error('没有从文件中提取到可分析的文字。扫描版 PDF 暂不支持 OCR。');
    const truncated = normalized.length > MAX_DOCUMENT_CHARACTERS;
    return {
      name: basename(absolutePath),
      kind,
      text: normalized.slice(0, MAX_DOCUMENT_CHARACTERS),
      originalBytes: before.size,
      ...(parsed.pageCount === undefined ? {} : { pageCount: parsed.pageCount }),
      truncated,
    };
  } finally {
    buffer?.fill(0);
    await handle.close().catch(() => undefined);
  }
}

async function parseByKind(buffer: Buffer, kind: DocumentKind): Promise<{ text: string; pageCount?: number }> {
  if (kind === 'text') return { text: decodeText(buffer) };
  if (kind === 'docx') {
    if (!hasZipMagic(buffer)) throw new Error('DOCX 文件结构无效。');
    // Raw-text extraction never renders links or HTML, and Mammoth disables
    // external-file access by default for Buffer inputs.
    const result = await mammoth.extractRawText({ buffer });
    return { text: result.value };
  }
  if (!hasPdfMagic(buffer)) throw new Error('PDF 文件结构无效。');
  const parser = new PDFParse({ data: new Uint8Array(buffer) });
  try {
    const result = await parser.getText();
    return { text: result.text, pageCount: result.total };
  } finally {
    await parser.destroy();
  }
}

function decodeText(buffer: Buffer): string {
  if (buffer.includes(0)) throw new Error('文本文件包含二进制数据，无法安全读取。');
  const utf8 = new TextDecoder('utf-8', { fatal: false }).decode(buffer);
  const replacementRatio = (utf8.match(/\uFFFD/gu)?.length ?? 0) / Math.max(1, utf8.length);
  if (replacementRatio > 0.01) throw new Error('文本编码无法识别，请将文件另存为 UTF-8 后重试。');
  return utf8;
}

function normalizeText(text: string): string {
  return text
    .replace(/\r\n?/gu, '\n')
    .replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/gu, '')
    .replace(/[ \t]+\n/gu, '\n')
    .replace(/\n{4,}/gu, '\n\n\n')
    .trim();
}

function hasPdfMagic(buffer: Buffer): boolean {
  return buffer.length >= 5 && buffer.subarray(0, 5).toString('ascii') === '%PDF-';
}

function hasZipMagic(buffer: Buffer): boolean {
  return buffer.length >= 4 && buffer[0] === 0x50 && buffer[1] === 0x4b && [0x03, 0x05, 0x07].includes(buffer[2] ?? -1);
}
