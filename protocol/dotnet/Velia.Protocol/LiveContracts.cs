using System.Text.Json.Serialization;

namespace Velia.Protocol;

public static class LiveModels
{
    public const string Gemini31FlashLivePreview = "gemini-3.1-flash-live-preview";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Gemini31FlashLivePreview,
    };
}

public static class LiveVoiceNames
{
    public const string Zephyr = "Zephyr";
    public const string Kore = "Kore";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Zephyr,
        Kore,
    };
}

public static class LiveStates
{
    public const string Inactive = "inactive";
    public const string Connecting = "connecting";
    public const string Listening = "listening";
    public const string Speaking = "speaking";
    public const string Reconnecting = "reconnecting";
    public const string Error = "error";
}

public static class LiveEventTypes
{
    public const string Setup = "setup";
    public const string Listening = "listening";
    public const string Speaking = "speaking";
    public const string Audio = "audio";
    public const string InputTranscript = "inputTranscript";
    public const string OutputTranscript = "outputTranscript";
    public const string LocalResult = "localResult";
    public const string ToolCall = "toolCall";
    public const string Interrupted = "interrupted";
    public const string TurnComplete = "turnComplete";
    public const string Reconnecting = "reconnecting";
    public const string Closed = "closed";
    public const string Failed = "failed";
}

public sealed record LiveStartParams(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("voiceName")] string VoiceName);

public sealed record LiveStartResult(
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("inputSampleRate")] int InputSampleRate,
    [property: JsonPropertyName("outputSampleRate")] int OutputSampleRate);

public sealed record LiveSendAudioParams(
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("audioBase64")] string AudioBase64,
    [property: JsonPropertyName("mimeType")] string MimeType);

public sealed record LiveSendAudioResult(
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("bufferedChunks")] int BufferedChunks);

public sealed record LiveSendVideoParams(
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("imageBase64")] string ImageBase64,
    [property: JsonPropertyName("mimeType")] string MimeType);

public sealed record LiveSendVideoResult(
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("accepted")] bool Accepted);

public sealed record LiveSubmitLocalTranscriptParams(
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("transcript")] string Transcript,
    [property: JsonPropertyName("utteranceId")] string? UtteranceId = null,
    [property: JsonPropertyName("capturedAtUtc")] DateTimeOffset? CapturedAtUtc = null);

public sealed record LiveSubmitLocalTranscriptResult(
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("handled")] bool Handled,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("duplicate")] bool Duplicate);

public sealed record LiveToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("question")] string Question);

public sealed record LiveSubmitToolResultParams(
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("callId")] string CallId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("result")] string? Result = null,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record LiveSubmitToolResultResult(
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("callId")] string CallId,
    [property: JsonPropertyName("accepted")] bool Accepted);

public sealed record LiveStopParams([property: JsonPropertyName("sessionId")] Guid SessionId);

public sealed record LiveStopResult(
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("accepted")] bool Accepted);

public sealed record LiveStatusResult(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("sessionId")] Guid? SessionId = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("message")] string? Message = null);

public sealed record LiveEvent(
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("audioBase64")] string? AudioBase64 = null,
    [property: JsonPropertyName("mimeType")] string? MimeType = null,
    [property: JsonPropertyName("transcript")] string? Transcript = null,
    [property: JsonPropertyName("toolCall")] LiveToolCall? ToolCall = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("retryable")] bool? Retryable = null,
    [property: JsonPropertyName("resultOk")] bool? ResultOk = null)
{
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = "velia-live-event/v1";
}
