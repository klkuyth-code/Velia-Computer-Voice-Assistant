using System.Text.Json.Serialization;

namespace Velia.Protocol;

public static class AssistantVisualStates
{
    public const string Idle = "idle";
    public const string Listening = "listening";
    public const string Thinking = "thinking";
    public const string Speaking = "speaking";
}

public static class MissionLifecycleStates
{
    public const string Accepted = "accepted";
    public const string Running = "running";
    public const string AwaitingInput = "awaitingInput";
    public const string Correcting = "correcting";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record AssistantEvent(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("missionId")] Guid? MissionId = null,
    [property: JsonPropertyName("message")] string? Message = null);

public sealed record MissionSnapshotResult(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("missionId")] Guid MissionId,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("currentRequest")] string CurrentRequest,
    [property: JsonPropertyName("requirementCount")] int RequirementCount,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("message")] string? Message = null);

public sealed record ToolConsentRequest(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("confirmationId")] Guid ConfirmationId,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("risks")] IReadOnlyList<string> Risks,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyName("target")] string? Target = null);
