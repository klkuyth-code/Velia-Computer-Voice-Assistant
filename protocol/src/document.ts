export const documentKinds = ['text', 'pdf', 'docx'] as const;
export type DocumentKind = (typeof documentKinds)[number];

export interface DocumentOpenParams extends Record<string, unknown> {
  readonly name: string;
  readonly kind: DocumentKind;
  readonly text: string;
  readonly originalBytes: number;
  readonly pageCount?: number;
  readonly truncated: boolean;
}

export interface DocumentOpenResult {
  readonly documentId: string;
  readonly name: string;
  readonly kind: DocumentKind;
  readonly characters: number;
  readonly originalBytes: number;
  readonly pageCount?: number;
  readonly truncated: boolean;
  readonly preview: string;
  readonly openedAtUtc: string;
}

export interface DocumentAnalyzeParams extends Record<string, unknown> {
  readonly documentId: string;
  readonly prompt: string;
}

export interface DocumentAnalyzeResult {
  readonly documentId: string;
  readonly answer: string;
  readonly model: string;
  readonly providerLatencyMs: number;
}

export interface DocumentCloseParams extends Record<string, unknown> {
  readonly documentId: string;
}

export interface DocumentCloseResult {
  readonly documentId: string;
  readonly closed: boolean;
}

export interface DocumentExtractionResult extends Record<string, unknown> {
  readonly name: string;
  readonly kind: DocumentKind;
  readonly text: string;
  readonly originalBytes: number;
  readonly pageCount?: number;
  readonly truncated: boolean;
}
