using System.Text.Json.Serialization;

namespace Velia.Protocol;

public static class DocumentKinds
{
    public const string Text = "text";
    public const string Pdf = "pdf";
    public const string Docx = "docx";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Text, Pdf, Docx,
    };
}

public sealed record DocumentOpenParams(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("originalBytes")] long OriginalBytes,
    [property: JsonPropertyName("pageCount")] int? PageCount,
    [property: JsonPropertyName("truncated")] bool Truncated);

public sealed record DocumentOpenResult(
    [property: JsonPropertyName("documentId")] Guid DocumentId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("characters")] int Characters,
    [property: JsonPropertyName("originalBytes")] long OriginalBytes,
    [property: JsonPropertyName("pageCount")] int? PageCount,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("preview")] string Preview,
    [property: JsonPropertyName("openedAtUtc")] DateTimeOffset OpenedAtUtc);

public sealed record DocumentAnalyzeParams(
    [property: JsonPropertyName("documentId")] Guid DocumentId,
    [property: JsonPropertyName("prompt")] string Prompt);

public sealed record DocumentAnalyzeResult(
    [property: JsonPropertyName("documentId")] Guid DocumentId,
    [property: JsonPropertyName("answer")] string Answer,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("providerLatencyMs")] long ProviderLatencyMs);

public sealed record DocumentCloseParams(
    [property: JsonPropertyName("documentId")] Guid DocumentId);

public sealed record DocumentCloseResult(
    [property: JsonPropertyName("documentId")] Guid DocumentId,
    [property: JsonPropertyName("closed")] bool Closed);
