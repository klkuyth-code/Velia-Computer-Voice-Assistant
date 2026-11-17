export const desktopContextModes = ['auto', 'selection', 'clipboard'] as const;
export type DesktopContextMode = (typeof desktopContextModes)[number];
export type DesktopContextKind = 'selectedText' | 'selectedFiles' | 'clipboardText' | 'none';
export const desktopContextFileOperations = [
  'copyToSelectedFolder',
  'moveToSelectedFolder',
  'renameSelectedFile',
] as const;
export type DesktopContextFileOperation = (typeof desktopContextFileOperations)[number];

export interface DesktopContextFile {
  readonly name: string;
  readonly extension: string;
  readonly sizeBytes?: number;
  readonly isDirectory: boolean;
}

export interface DesktopContextSource {
  readonly processName: string;
  readonly windowTitle: string;
}

export interface DesktopContextCaptureParams extends Record<string, unknown> {
  readonly operationId: string;
  readonly mode: DesktopContextMode;
}

export interface DesktopContextCaptureResult {
  readonly contextId: string;
  readonly kind: DesktopContextKind;
  readonly text?: string;
  readonly files?: readonly DesktopContextFile[];
  readonly source: DesktopContextSource;
  readonly clipboardRestored: boolean;
  readonly expiresAtUtc: string;
}

export interface DesktopContextReleaseParams extends Record<string, unknown> {
  readonly operationId: string;
}

export interface DesktopContextReleaseResult {
  readonly operationId: string;
  readonly released: boolean;
}

export interface DesktopContextReplaceSelectionParams extends Record<string, unknown> {
  readonly operationId: string;
  readonly contextId: string;
  readonly text: string;
}

export interface DesktopContextReplaceSelectionResult {
  readonly operationId: string;
  readonly contextId: string;
  readonly replaced: boolean;
  readonly verified: boolean;
  readonly clipboardRestored: boolean;
  readonly source: DesktopContextSource;
}

export interface DesktopContextFileOperationParams extends Record<string, unknown> {
  readonly operationId: string;
  readonly contextId: string;
  readonly action: DesktopContextFileOperation;
  readonly newName?: string;
}

export interface DesktopContextFileOperationResult {
  readonly operationId: string;
  readonly contextId: string;
  readonly action: DesktopContextFileOperation;
  readonly verified: boolean;
  readonly files: readonly DesktopContextFile[];
  readonly destinationFolder?: DesktopContextFile;
}

export interface DesktopContextResolveSelectedDocumentParams extends Record<string, unknown> {
  readonly operationId: string;
  readonly contextId: string;
}

/** Trusted Core-to-Electron-main handoff; never expose internalPath to a model or renderer. */
export interface DesktopContextResolveSelectedDocumentResult {
  readonly operationId: string;
  readonly contextId: string;
  readonly name: string;
  readonly extension: string;
  readonly sizeBytes: number;
  readonly lastWriteTimeUnixMs: number;
  readonly creationTimeUnixMs: number;
  readonly internalPath: string;
}
