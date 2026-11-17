import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { extractDocument, MAX_DOCUMENT_CHARACTERS } from './DocumentExtractor.js';

describe('DocumentExtractor', () => {
  it('reads and normalizes a bounded UTF-8 text file', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'velia-document-'));
    const path = join(directory, 'notes.txt');
    await writeFile(path, '\ufeff第一行\r\n第二行\u0001');

    const result = await extractDocument(path);

    expect(result.kind).toBe('text');
    expect(result.name).toBe('notes.txt');
    expect(result.text).toBe('第一行\n第二行');
    expect(result.truncated).toBe(false);
  });

  it('truncates extracted text at the Core contract limit', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'velia-document-'));
    const path = join(directory, 'large.md');
    await writeFile(path, 'a'.repeat(MAX_DOCUMENT_CHARACTERS + 12));

    const result = await extractDocument(path);

    expect(result.text).toHaveLength(MAX_DOCUMENT_CHARACTERS);
    expect(result.truncated).toBe(true);
  });

  it('rejects unsupported and binary text files', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'velia-document-'));
    const exe = join(directory, 'sample.exe');
    const binary = join(directory, 'binary.txt');
    await writeFile(exe, 'x');
    await writeFile(binary, Buffer.from([1, 0, 2]));

    await expect(extractDocument(exe)).rejects.toThrow(/只支持/);
    await expect(extractDocument(binary)).rejects.toThrow(/二进制/);
  });

  it('rejects invalid PDF and DOCX magic bytes before parser use', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'velia-document-'));
    const pdf = join(directory, 'fake.pdf');
    const docx = join(directory, 'fake.docx');
    await writeFile(pdf, 'not-pdf'); await writeFile(docx, 'not-docx');

    await expect(extractDocument(pdf)).rejects.toThrow(/PDF 文件结构无效/);
    await expect(extractDocument(docx)).rejects.toThrow(/DOCX 文件结构无效/);
  });

  it('rejects a file that no longer matches the trusted selection binding', async () => {
    const directory = await mkdtemp(join(tmpdir(), 'velia-document-'));
    const path = join(directory, 'bound.txt');
    await writeFile(path, 'trusted text');

    await expect(extractDocument(path, {
      sizeBytes: 99,
      lastWriteTimeUnixMs: 0,
      creationTimeUnixMs: 0,
    })).rejects.toThrow(/发生变化/);
  });

  it('extracts real PDF and DOCX fixture text without uploading files', async () => {
    const fixtureRoot = join(process.cwd(), 'test', 'fixtures');
    const pdfBytes = await readFile(join(fixtureRoot, 'document.pdf'));
    const docxBytes = await readFile(join(fixtureRoot, 'document.docx'));
    const directory = await mkdtemp(join(tmpdir(), 'velia-document-'));
    const pdf = join(directory, 'fixture.pdf');
    const docx = join(directory, 'fixture.docx');
    await writeFile(pdf, pdfBytes); await writeFile(docx, docxBytes);

    const pdfResult = await extractDocument(pdf);
    const docxResult = await extractDocument(docx);

    expect(pdfResult.text).toContain('PDF-0216-B');
    expect(pdfResult.pageCount).toBe(1);
    expect(docxResult.text).toContain('DOCX-0216-A');
  });
});
