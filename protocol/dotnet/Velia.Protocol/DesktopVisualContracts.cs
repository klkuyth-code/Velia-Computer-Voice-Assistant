using System.Text.Json.Serialization;

namespace Velia.Protocol;

public static class DesktopVisualActions
{
    public const string Click = "click";
    public const string Type = "type";
    public const string Scroll = "scroll";
    public const string Press = "press";
    // Internal composite action used by the verified screen-to-document fast path.
    // It is deliberately absent from the model-facing desktop_visual schema.
    public const string AppendText = "appendText";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Click, Type, Scroll, Press, AppendText,
    };
}

public static class DesktopVisualKeys
{
    public const string Enter = "enter";
    public const string Escape = "escape";
    public const string Tab = "tab";
    public const string Space = "space";
    public const string Backspace = "backspace";
    public const string ArrowUp = "arrowUp";
    public const string ArrowDown = "arrowDown";
    public const string ArrowLeft = "arrowLeft";
    public const string ArrowRight = "arrowRight";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Enter, Escape, Tab, Space, Backspace, ArrowUp, ArrowDown, ArrowLeft, ArrowRight,
    };
}

public sealed record DesktopVisualGetForegroundResult(
    [property: JsonPropertyName("targetToken")] Guid TargetToken,
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("windowTitle")] string WindowTitle,
    [property: JsonPropertyName("screenX")] int ScreenX,
    [property: JsonPropertyName("screenY")] int ScreenY,
    [property: JsonPropertyName("capturedAtUtc")] DateTimeOffset CapturedAtUtc);

public sealed record DesktopVisualGetForegroundParams(
    [property: JsonPropertyName("operationId")] Guid OperationId);

public sealed record DesktopVisualExecuteActionParams(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("targetToken")] Guid TargetToken,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("displayId")] string DisplayId,
    [property: JsonPropertyName("screenX")] int ScreenX,
    [property: JsonPropertyName("screenY")] int ScreenY,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("scrollDelta")] int? ScrollDelta,
    [property: JsonPropertyName("expectedProcessId")] int ExpectedProcessId,
    [property: JsonPropertyName("expectedProcessName")] string ExpectedProcessName,
    [property: JsonPropertyName("expectedWindowTitle")] string ExpectedWindowTitle,
    [property: JsonPropertyName("structuredTargetOnly")] bool StructuredTargetOnly = false);

public sealed record DesktopVisualExecuteActionResult(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("executed")] bool Executed,
    [property: JsonPropertyName("screenX")] int ScreenX,
    [property: JsonPropertyName("screenY")] int ScreenY,
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("windowTitle")] string WindowTitle,
    [property: JsonPropertyName("verification")] string Verification = "inputAccepted");
