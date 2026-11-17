import type { AssistantOverlaySnapshot, LiveState } from '@velia/protocol';

export function liveOverlayPhase(state: LiveState): AssistantOverlaySnapshot['phase'] {
  return ({
    inactive: 'idle',
    connecting: 'starting',
    listening: 'recording',
    speaking: 'speaking',
    reconnecting: 'starting',
    error: 'error',
  })[state];
}

export function latestLiveTurn(transcript: string): string {
  const lines = transcript.split('\n');
  return (lines.at(-1) ?? '').trim();
}
