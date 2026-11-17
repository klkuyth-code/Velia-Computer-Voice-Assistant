import { useEffect, useState } from 'react';
import type { AssistantOverlaySnapshot } from '@velia/protocol';
import veliaLifeOrbImage from '../assets/velia-life-orb.png';

interface OverlayState { readonly snapshot: AssistantOverlaySnapshot; readonly compact: boolean; }
type AssistantVisualState = 'idle' | 'listening' | 'answering';

const initial: OverlayState = {
  compact: true,
  snapshot: { phase: 'idle', status: '按住 F8 说话', transcript: '', answer: '', canStop: false, shortcutLabel: 'F8', metrics: {}, updatedAtUtc: new Date(0).toISOString() },
};

/** The top overlay is intentionally only the approved orb. Conversation text lives in history. */
export function AssistantOverlay() {
  const [state, setState] = useState<OverlayState>(initial);

  useEffect(() => {
    let mounted = true;
    void window.velia.assistant.getState().then((next) => { if (mounted) setState(next); });
    const unsubscribe = window.velia.assistant.onSnapshot((next) => setState(next));
    return () => { mounted = false; unsubscribe(); };
  }, []);

  const visualState = assistantVisualState(state.snapshot.phase);
  return (
    <main className={`assistant-orb-shell assistant-state--${visualState}`}>
      <button
        className="assistant-orb-button"
        type="button"
        aria-label="打开 Velia"
        onClick={() => void window.velia.assistant.openMain()}
      >
        <span className="assistant-orb-aura" />
        <span className="assistant-orb-surface"><img className="assistant-orb-image" src={veliaLifeOrbImage} alt="" draggable={false} /></span>
        <span className="assistant-orb-pulse assistant-orb-pulse--one" />
        <span className="assistant-orb-pulse assistant-orb-pulse--two" />
      </button>
    </main>
  );
}

export function assistantVisualState(phase: AssistantOverlaySnapshot['phase']): AssistantVisualState {
  if (phase === 'recording') return 'listening';
  if (phase === 'responding' || phase === 'speaking') return 'answering';
  return 'idle';
}
