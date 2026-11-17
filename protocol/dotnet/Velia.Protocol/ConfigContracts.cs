using System.Text.Json.Serialization;

namespace Velia.Protocol;

public static class ProviderIds
{
    public const string Llm = "llm";
    public const string Asr = "asr";
    public const string Vision = "vision";
    public const string Web = "web";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Llm,
        Asr,
        Vision,
        Web,
    };
}

public sealed record SetProviderSecretParams(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("secret")] string Secret);

public sealed record RemoveProviderSecretParams(
    [property: JsonPropertyName("provider")] string Provider);

public sealed record ConfigStatusResult
{
    [JsonPropertyName("llm")]
    public required string Llm { get; init; }

    [JsonPropertyName("asr")]
    public required string Asr { get; init; }

    [JsonPropertyName("vision")]
    public required string Vision { get; init; }

    [JsonPropertyName("web")]
    public required string Web { get; init; }
}

public static class ProviderProfileIds
{
    public const string DeepSeekV4Flash = "deepseek-v4-flash";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        DeepSeekV4Flash,
    };
}

public sealed record ProviderProfileResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("selected")] bool Selected,
    [property: JsonPropertyName("secretState")] string SecretState,
    [property: JsonPropertyName("connectionState")] string ConnectionState,
    [property: JsonPropertyName("supportsReasoning")] bool SupportsReasoning,
    [property: JsonPropertyName("supportsTools")] bool SupportsTools,
    [property: JsonPropertyName("lastTestedAtUtc")] DateTimeOffset? LastTestedAtUtc = null,
    [property: JsonPropertyName("message")] string? Message = null);

public sealed record ProviderProfilesResult(
    [property: JsonPropertyName("selectedProfileId")] string SelectedProfileId,
    [property: JsonPropertyName("profiles")] IReadOnlyList<ProviderProfileResult> Profiles);

public sealed record SelectProviderProfileParams(
    [property: JsonPropertyName("profileId")] string ProfileId);

public sealed record TestProviderParams(
    [property: JsonPropertyName("profileId")] string ProfileId);

public sealed record ProviderTestResult(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("testedAtUtc")] DateTimeOffset TestedAtUtc,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("retryable")] bool? Retryable = null);

public sealed record WebTestResult(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("testedAtUtc")] DateTimeOffset TestedAtUtc,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("remainingCredits")] int? RemainingCredits = null,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null,
    [property: JsonPropertyName("retryable")] bool? Retryable = null);
