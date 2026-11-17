using System.Text.Json;
using System.Text.Json.Serialization;

namespace Velia.Protocol;

public static class RpcSchema
{
    public const string RequestV1 = "velia-rpc-request/v1";
    public const string ResponseV1 = "velia-rpc-response/v1";
    public const string ProtocolVersion = "1.0";
}

public static class RpcMethods
{
    public const string SystemHealth = "system.health";
    public const string AppBootstrap = "app.bootstrap";
    public const string ConfigGetStatus = "config.getStatus";
    public const string ConfigGetProfiles = "config.getProfiles";
    public const string ConfigSelectProfile = "config.selectProfile";
    public const string ConfigTestProvider = "config.testProvider";
    public const string ConfigSetSecret = "config.setSecret";
    public const string ConfigRemoveSecret = "config.removeSecret";
    public const string WebTest = "web.test";
    public const string VoiceTranscribe = "voice.transcribe";
    public const string VoiceTestAsr = "voice.testAsr";
    public const string VoiceGetShortcut = "voice.getShortcut";
    public const string VoiceSetShortcut = "voice.setShortcut";
    public const string LiveStart = "live.start";
    public const string LiveSendAudio = "live.sendAudio";
    public const string LiveSendVideo = "live.sendVideo";
    public const string LiveSubmitLocalTranscript = "live.submitLocalTranscript";
    public const string LiveSubmitToolResult = "live.submitToolResult";
    public const string LiveStop = "live.stop";
    public const string LiveGetStatus = "live.getStatus";
    public const string VisionTest = "vision.test";
    public const string VisionAnalyze = "vision.analyze";
    public const string DocumentOpen = "document.open";
    public const string DocumentAnalyze = "document.analyze";
    public const string DocumentClose = "document.close";
    public const string ConversationSubmitText = "conversation.submitText";
    public const string ConversationSubmitToolResult = "conversation.submitToolResult";
    public const string ConversationCancel = "conversation.cancel";
    public const string ConversationHistoryGetSettings = "conversation.history.getSettings";
    public const string ConversationHistoryUpdateSettings = "conversation.history.updateSettings";
    public const string ConversationHistoryList = "conversation.history.list";
    public const string ConversationHistoryGet = "conversation.history.get";
    public const string ConversationHistoryOpen = "conversation.history.open";
    public const string ConversationHistoryStartNew = "conversation.history.startNew";
    public const string ConversationHistoryDelete = "conversation.history.delete";
    public const string ConversationHistoryClear = "conversation.history.clear";
    public const string DesktopContextCapture = "desktopContext.capture";
    public const string DesktopContextReplaceSelection = "desktopContext.replaceSelection";
    public const string DesktopContextOperateSelectedFiles = "desktopContext.operateSelectedFiles";
    public const string DesktopContextResolveSelectedDocument = "desktopContext.resolveSelectedDocument";
    public const string DesktopContextRelease = "desktopContext.release";
    public const string DesktopVisualGetForeground = "desktopVisual.getForeground";
    public const string DesktopVisualExecuteAction = "desktopVisual.executeAction";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        SystemHealth,
        AppBootstrap,
        ConfigGetStatus,
        ConfigGetProfiles,
        ConfigSelectProfile,
        ConfigTestProvider,
        ConfigSetSecret,
        ConfigRemoveSecret,
        WebTest,
        VoiceTranscribe,
        VoiceTestAsr,
        VoiceGetShortcut,
        VoiceSetShortcut,
        LiveStart,
        LiveSendAudio,
        LiveSendVideo,
        LiveSubmitLocalTranscript,
        LiveSubmitToolResult,
        LiveStop,
        LiveGetStatus,
        VisionTest,
        VisionAnalyze,
        DocumentOpen,
        DocumentAnalyze,
        DocumentClose,
        ConversationSubmitText,
        ConversationSubmitToolResult,
        ConversationCancel,
        ConversationHistoryGetSettings,
        ConversationHistoryUpdateSettings,
        ConversationHistoryList,
        ConversationHistoryGet,
        ConversationHistoryOpen,
        ConversationHistoryStartNew,
        ConversationHistoryDelete,
        ConversationHistoryClear,
        DesktopContextCapture,
        DesktopContextReplaceSelection,
        DesktopContextOperateSelectedFiles,
        DesktopContextResolveSelectedDocument,
        DesktopContextRelease,
        DesktopVisualGetForeground,
        DesktopVisualExecuteAction,
    };
}

public sealed record RpcRequest
{
    [JsonPropertyName("schema")]
    public required string Schema { get; init; }

    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public required JsonElement Params { get; init; }
}

public sealed record RpcError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("details")] IReadOnlyDictionary<string, object?>? Details = null);

public sealed record RpcResponse
{
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = RpcSchema.ResponseV1;

    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RpcError? Error { get; init; }

    public static RpcResponse Success(Guid id, object result) => new()
    {
        Id = id,
        Ok = true,
        Result = result,
    };

    public static RpcResponse Failure(Guid id, string code, string message, bool retryable = false) => new()
    {
        Id = id,
        Ok = false,
        Error = new RpcError(code, message, retryable),
    };
}

public sealed record EmptyParams;

public sealed record SetVoiceShortcutParams(
    [property: JsonPropertyName("shortcutId")] string ShortcutId);

public sealed record HealthResult(
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("nowUtc")] DateTimeOffset NowUtc);

public static class CoreEventTypes
{
    public const string Accepted = "accepted";
    public const string State = "state";
    public const string Reasoning = "reasoning";
    public const string Content = "content";
    public const string Tool = "tool";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Mission = "mission";
    public const string Notification = "notification";
}

public sealed record ConversationToolCallDelta(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("argumentsFragment")] string? ArgumentsFragment = null);

public sealed record ConversationUsage(
    [property: JsonPropertyName("promptTokens")] int PromptTokens,
    [property: JsonPropertyName("completionTokens")] int CompletionTokens,
    [property: JsonPropertyName("totalTokens")] int TotalTokens);

public sealed record CoreNotification(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("message")] string Message);

public sealed record CoreEvent(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("delta")] string? Delta = null,
    [property: JsonPropertyName("toolCall")] ConversationToolCallDelta? ToolCall = null,
    [property: JsonPropertyName("finishReason")] string? FinishReason = null,
    [property: JsonPropertyName("usage")] ConversationUsage? Usage = null,
    [property: JsonPropertyName("error")] RpcError? Error = null,
    [property: JsonPropertyName("mission")] MissionSnapshotResult? Mission = null,
    [property: JsonPropertyName("notification")] CoreNotification? Notification = null)
{
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = "velia-core-event/v1";
}

public sealed record SubmitTextParams(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("origin")] string Origin = "text",
    [property: JsonPropertyName("relationship")] string Relationship = "auto");

public sealed record SubmitTextResult(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("mission")] MissionSnapshotResult Mission);

public sealed record CancelConversationParams(
    [property: JsonPropertyName("operationId")] Guid OperationId);

public sealed record CancelConversationResult(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("accepted")] bool Accepted);

public sealed record SubmitToolResultParams(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("result")] JsonElement? Result = null,
    [property: JsonPropertyName("error")] RpcError? Error = null);

public sealed record SubmitToolResultResult(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    [property: JsonPropertyName("accepted")] bool Accepted);
