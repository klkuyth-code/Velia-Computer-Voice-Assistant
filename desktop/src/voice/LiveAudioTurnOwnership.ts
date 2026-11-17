/** Ensures one Live turn is spoken by either Gemini PCM or local TTS, never both. */
export class LiveAudioTurnOwnership {
  #localOwnsTurn = false;

  startUserTurn(): void { this.#localOwnsTurn = false; }
  claimLocalResult(): void { this.#localOwnsTurn = true; }
  acceptProviderAudio(): boolean { return !this.#localOwnsTurn; }
  reset(): void { this.#localOwnsTurn = false; }
}
