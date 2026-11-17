import { describe, expect, it } from 'vitest';
import { LiveAudioTurnOwnership } from './LiveAudioTurnOwnership';

describe('LiveAudioTurnOwnership', () => {
  it('suppresses late Gemini audio after a verified local result', () => {
    const ownership = new LiveAudioTurnOwnership();
    expect(ownership.acceptProviderAudio()).toBe(true);
    ownership.claimLocalResult();
    expect(ownership.acceptProviderAudio()).toBe(false);
  });

  it('releases local ownership as soon as the user starts a new turn', () => {
    const ownership = new LiveAudioTurnOwnership();
    ownership.claimLocalResult();
    ownership.startUserTurn();
    expect(ownership.acceptProviderAudio()).toBe(true);
  });
});
