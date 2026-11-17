import { describe, expect, it } from 'vitest';
import type { InputEvent } from '@velia/protocol';
import { LongConversationInputBridge } from '../electron/voice/LongConversationInputBridge.js';
import type { InputShortcutDiagnostics } from '../electron/voice/ShortcutDiagnostics.js';

describe('long-conversation input bridge', () => {
  it('forwards a press immediately when the renderer acknowledged its listeners', () => {
    const recording = createRecording();
    const bridge = new LongConversationInputBridge(recording.publish, recording.diagnostics);
    bridge.setRendererReady(true);
    bridge.forward(input('pressed', 1));

    expect(recording.events.map((event) => event.sequence)).toEqual([1]);
    expect(recording.dispositions).toContain('forwarded');
  });

  it('queues a press across renderer reload and forwards it after acknowledgement', () => {
    const recording = createRecording();
    const bridge = new LongConversationInputBridge(recording.publish, recording.diagnostics);
    bridge.forward(input('pressed', 1));
    expect(recording.events).toEqual([]);

    bridge.setRendererReady(true);
    expect(recording.events.map((event) => event.sequence)).toEqual([1]);
  });

  it('cancels two queued toggle presses instead of replaying a stale state', () => {
    const recording = createRecording();
    const bridge = new LongConversationInputBridge(recording.publish, recording.diagnostics);
    bridge.forward(input('pressed', 1));
    bridge.forward(input('pressed', 2));
    bridge.setRendererReady(true);

    expect(recording.events).toEqual([]);
    expect(recording.dispositions).toContain('queue-cancelled');
  });

  it('does not queue a release over the press that still needs delivery', () => {
    const recording = createRecording();
    const bridge = new LongConversationInputBridge(recording.publish, recording.diagnostics);
    bridge.forward(input('pressed', 1));
    bridge.forward(input('released', 2));
    bridge.setRendererReady(true);

    expect(recording.events.map((event) => event.sequence)).toEqual([1]);
    expect(recording.dispositions).toContain('released-ignored');
  });

  it('keeps a press queued when renderer delivery unexpectedly fails', () => {
    const recording = createRecording();
    let deliver = false;
    const bridge = new LongConversationInputBridge((event) => {
      if (!deliver) return false;
      recording.events.push(event);
      return true;
    }, recording.diagnostics);
    bridge.setRendererReady(true);
    bridge.forward(input('pressed', 1));
    deliver = true;
    bridge.setRendererReady(true);

    expect(recording.events.map((event) => event.sequence)).toEqual([1]);
  });
});

function createRecording(): {
  readonly events: InputEvent[];
  readonly dispositions: string[];
  readonly publish: (event: InputEvent) => boolean;
  readonly diagnostics: InputShortcutDiagnostics;
} {
  const events: InputEvent[] = [];
  const dispositions: string[] = [];
  return {
    events,
    dispositions,
    publish: (event) => { events.push(event); return true; },
    diagnostics: {
      rendererChanged: (ready) => dispositions.push(`renderer:${ready}`),
      longConversation: (disposition) => dispositions.push(disposition),
    },
  };
}

function input(edge: InputEvent['edge'], sequence: number): InputEvent {
  return {
    schema: 'velia-input-event/v1',
    eventId: `1196ea21-1692-46a8-b87b-92c608429ea${sequence}`,
    sequence,
    atUtc: '2026-08-21T00:00:00Z',
    shortcut: 'voiceLongConversation',
    key: 'Ctrl+Alt+L',
    edge,
  };
}
