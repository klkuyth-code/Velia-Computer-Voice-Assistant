using System.Text.Json;

namespace Velia.Core.Providers.Chat;

internal enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

internal sealed record ChatMessage(
    ChatRole Role,
    string Content,
    string? ToolCallId = null,
    IReadOnlyList<ChatAssistantToolCall>? ToolCalls = null);

internal sealed record ChatAssistantToolCall(string Id, string Name, string Arguments);

internal sealed record ChatToolDefinition(string Name, string Description, JsonElement Parameters, bool Strict = false);

internal sealed record ChatCompletionRequest(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ChatToolDefinition>? Tools = null);

internal sealed record ChatProviderOptions(
    Uri BaseUri,
    string Model,
    bool ThinkingEnabled = true,
    string ReasoningEffort = "high",
    int MaximumOutputTokens = 4096);

internal interface IChatProviderFactory
{
    IStreamingChatProvider Create(
        string profileId,
        int maximumOutputTokens = 4096,
        bool thinkingEnabled = true);
}

internal enum ChatStreamEventKind
{
    ContentDelta,
    ReasoningDelta,
    ToolCallDelta,
    Completed,
}

internal sealed record ChatToolCallDelta(
    int Index,
    string? Id,
    string? Name,
    string? ArgumentsFragment);

internal sealed record ChatUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

internal sealed record ChatStreamEvent(
    ChatStreamEventKind Kind,
    string? Text = null,
    ChatToolCallDelta? ToolCall = null,
    string? FinishReason = null,
    ChatUsage? Usage = null);

internal interface IStreamingChatProvider
{
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ChatProviderException(
    string message,
    bool retryable,
    int? statusCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public bool Retryable { get; } = retryable;

    public int? StatusCode { get; } = statusCode;
}
