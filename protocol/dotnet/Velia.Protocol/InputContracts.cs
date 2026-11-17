using System.Text.Json.Serialization;

namespace Velia.Protocol;

public static class InputEventEdges
{
    public const string Pressed = "pressed";
    public const string Released = "released";
}

public static class VoiceShortcutIds
{
    public const string F8 = "f8";
    public const string CtrlShiftSpace = "ctrlShiftSpace";
    // Accepted only when migrating settings written by an older Velia build.
    public const string CtrlAltSpace = "ctrlAltSpace";
}

public static class VoiceInputShortcuts
{
    public const string PushToTalk = "voicePushToTalk";
    public const string LongConversation = "voiceLongConversation";
}

public sealed record VoiceShortcutSetting(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("help")] string Help);

public sealed record InputEvent(
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc,
    [property: JsonPropertyName("shortcut")] string Shortcut,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("edge")] string Edge)
{
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = "velia-input-event/v1";
}
