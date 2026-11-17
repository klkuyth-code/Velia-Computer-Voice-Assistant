export function isSpeechCancellationError(error: string | undefined): boolean {
  return error === 'canceled' || error === 'interrupted';
}
