namespace Velia.Core.Providers.Vision;

internal interface IVisionProvider
{
    Task<VisionProviderResult> AnalyzeAsync(
        ReadOnlyMemory<byte> jpegImage,
        string prompt,
        string model,
        CancellationToken cancellationToken);

    Task<long> TestAsync(CancellationToken cancellationToken);
}

internal sealed record VisionProviderResult(string Text, long ProviderLatencyMs);

internal sealed class VisionProviderException(
    string code,
    string message,
    bool retryable,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
