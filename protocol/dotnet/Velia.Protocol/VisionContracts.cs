using System.Text.Json.Serialization;

namespace Velia.Protocol;

public static class VisionModels
{
    public const string Gemini35FlashLite = "gemini-3.5-flash-lite";
    public const string Gemini36Flash = "gemini-3.6-flash";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Gemini35FlashLite,
        Gemini36Flash,
    };
}

public sealed record VisionAnalyzeParams(
    [property: JsonPropertyName("imageBase64")] string ImageBase64,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("displayId")] string DisplayId,
    [property: JsonPropertyName("capturedAtUtc")] DateTimeOffset CapturedAtUtc);

public sealed record VisionAnalyzeResult(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("capturedAtUtc")] DateTimeOffset CapturedAtUtc,
    [property: JsonPropertyName("providerLatencyMs")] long ProviderLatencyMs,
    [property: JsonPropertyName("inputBytes")] int InputBytes);

public sealed record VisionTestResult(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("testedAtUtc")] DateTimeOffset TestedAtUtc,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("retryable")] bool? Retryable = null);
