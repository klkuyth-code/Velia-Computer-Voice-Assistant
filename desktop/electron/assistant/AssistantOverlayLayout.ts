import type { AssistantOverlaySnapshot } from '@velia/protocol';

export const compactAssistantSize = { width: 118, height: 112 } as const;
export const expandedAssistantSize = { width: 760, height: 300 } as const;
export const minimumExpandedAssistantSize = { width: 430, height: 176 } as const;

export function shouldExpandAssistant(snapshot: AssistantOverlaySnapshot): boolean {
  void snapshot;
  return false;
}

export function assistantAutoCollapseDelay(snapshot: AssistantOverlaySnapshot): number | undefined {
  if (snapshot.phase === 'error') return 10_000;
  if (snapshot.phase === 'idle' && (snapshot.transcript.trim() || snapshot.answer.trim())) return 6_500;
  if (snapshot.shortcutLabel === 'Live' && snapshot.phase === 'recording' && snapshot.answer.trim()) return 3_000;
  return undefined;
}

export function assistantExpandedSize(snapshot: AssistantOverlaySnapshot): { readonly width: number; readonly height: number } {
  const blocks = [snapshot.transcript.trim(), snapshot.answer.trim()].filter(Boolean);
  if (blocks.length === 0) return minimumExpandedAssistantSize;

  const longestLine = Math.max(0, ...blocks.flatMap((block) => block.split(/\r?\n/u).map((line) => line.length)));
  const width = clamp(
    330 + longestLine * 12,
    minimumExpandedAssistantSize.width,
    expandedAssistantSize.width,
  );
  const charactersPerLine = Math.max(16, Math.floor((width - 170) / 13));
  const visualLines = blocks.reduce((total, block) => total + block.split(/\r?\n/u).reduce(
    (lineTotal, line) => lineTotal + Math.max(1, Math.ceil(line.length / charactersPerLine)),
    0,
  ), 0);
  const height = clamp(
    132 + visualLines * 25 + (blocks.length - 1) * 10,
    minimumExpandedAssistantSize.height,
    expandedAssistantSize.height,
  );
  return { width, height };
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.max(minimum, Math.min(maximum, Math.round(value)));
}
