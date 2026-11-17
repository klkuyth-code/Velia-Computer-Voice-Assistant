/** Conservative last line of defence against submitting Velia's own playback. */
export function isLikelyPlaybackEcho(recognizedText: string, playbackText: string): boolean {
  const recognized = normalize(recognizedText);
  const playback = normalize(playbackText);
  if (recognized.length < 4 || playback.length < 8) return false;
  if (playback.includes(recognized)) return true;
  if (recognized.length < 6) return false;
  const recognizedPairs = characterPairs(recognized);
  const playbackPairs = new Set(characterPairs(playback));
  const matching = recognizedPairs.filter((pair) => playbackPairs.has(pair)).length;
  return recognizedPairs.length > 0 && matching / recognizedPairs.length >= 0.78;
}

function normalize(value: string): string {
  return value.normalize('NFKC').toLowerCase().replace(/[^\p{Letter}\p{Number}]+/gu, '');
}

function characterPairs(value: string): string[] {
  const characters = [...value];
  return characters.slice(0, -1).map((character, index) => `${character}${characters[index + 1]}`);
}
