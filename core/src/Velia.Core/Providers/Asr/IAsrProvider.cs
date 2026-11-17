namespace Velia.Core.Providers.Asr;

internal interface IAsrProvider
{
    Task<AsrTranscription> TranscribeAsync(
        ReadOnlyMemory<byte> wavAudio,
        CancellationToken cancellationToken);

    Task<AsrTranscription> TranscribeAsync(
        ReadOnlyMemory<byte> wavAudio,
        AsrRecognitionContext context,
        CancellationToken cancellationToken) => TranscribeAsync(wavAudio, cancellationToken);
}

internal sealed record AsrRecognitionContext(IReadOnlyList<string> Hotwords)
{
    public static AsrRecognitionContext Empty { get; } = new([]);
}

internal interface IAsrRecognitionContextProvider
{
    AsrRecognitionContext GetCurrent();
}

internal sealed record AsrTranscription(string Text, int AudioDurationMs, long ProviderLatencyMs);

internal sealed class AsrProviderException(
    string code,
    string message,
    bool retryable,
    string? providerLogId = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public string? ProviderLogId { get; } = providerLogId;
}
