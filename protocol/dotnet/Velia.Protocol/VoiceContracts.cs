using System.Text.Json.Serialization;

namespace Velia.Protocol;

public sealed record AsrCredentials(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("apiKey")] string? ApiKey = null,
    [property: JsonPropertyName("appId")] string? AppId = null,
    [property: JsonPropertyName("accessToken")] string? AccessToken = null);

public sealed record VoiceTranscribeParams(
    [property: JsonPropertyName("wavBase64")] string WavBase64,
    [property: JsonPropertyName("maxLatencyMs")] int? MaxLatencyMs = null);

public sealed record VoiceTranscriptionResult(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("audioDurationMs")] int AudioDurationMs,
    [property: JsonPropertyName("providerLatencyMs")] long ProviderLatencyMs);

public sealed record AsrTestResult(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("testedAtUtc")] DateTimeOffset TestedAtUtc,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("retryable")] bool? Retryable = null);
