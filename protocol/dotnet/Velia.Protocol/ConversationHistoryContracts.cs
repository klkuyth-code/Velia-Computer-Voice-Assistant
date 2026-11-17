using System.Text.Json.Serialization;

namespace Velia.Protocol;

public static class ConversationHistoryRetention
{
    public const int DefaultDays = 30;

    public static IReadOnlySet<int> AllowedDays { get; } = new HashSet<int>
    {
        7, 30, 90, 365,
    };
}

public static class ConversationHistoryRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        User, Assistant,
    };
}

public sealed record ConversationHistorySettingsResult(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("retentionDays")] int RetentionDays,
    [property: JsonPropertyName("storedConversationCount")] int StoredConversationCount,
    [property: JsonPropertyName("protection")] string Protection = "windowsCurrentUserDpapi")
{
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = "velia-conversation-history-settings/v1";
}

public sealed record ConversationHistoryMessageResult(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc);

public sealed record ConversationHistorySummaryResult(
    [property: JsonPropertyName("conversationId")] Guid ConversationId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("preview")] string Preview,
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc);

public sealed record ConversationHistoryDetailResult(
    [property: JsonPropertyName("conversationId")] Guid ConversationId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("preview")] string Preview,
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("messages")] IReadOnlyList<ConversationHistoryMessageResult> Messages)
{
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = "velia-conversation-history/v1";
}

public sealed record ConversationHistoryListResult(
    [property: JsonPropertyName("settings")] ConversationHistorySettingsResult Settings,
    [property: JsonPropertyName("conversations")] IReadOnlyList<ConversationHistorySummaryResult> Conversations);

public sealed record UpdateConversationHistorySettingsParams(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("retentionDays")] int RetentionDays);

public sealed record ConversationHistoryIdParams(
    [property: JsonPropertyName("conversationId")] Guid ConversationId);

public sealed record ConversationHistoryDeleteResult(
    [property: JsonPropertyName("conversationId")] Guid ConversationId,
    [property: JsonPropertyName("deleted")] bool Deleted);

public sealed record ConversationHistoryClearResult(
    [property: JsonPropertyName("deletedCount")] int DeletedCount);

public sealed record ConversationHistoryStartNewResult(
    [property: JsonPropertyName("started")] bool Started);
