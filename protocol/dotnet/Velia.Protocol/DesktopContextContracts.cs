using System.Text.Json.Serialization;

namespace Velia.Protocol;

public static class DesktopContextModes
{
    public const string Auto = "auto";
    public const string Selection = "selection";
    public const string Clipboard = "clipboard";
}

public static class DesktopContextKinds
{
    public const string SelectedText = "selectedText";
    public const string SelectedFiles = "selectedFiles";
    public const string ClipboardText = "clipboardText";
    public const string None = "none";
}

public static class DesktopContextFileOperations
{
    public const string CopyToSelectedFolder = "copyToSelectedFolder";
    public const string MoveToSelectedFolder = "moveToSelectedFolder";
    public const string RenameSelectedFile = "renameSelectedFile";
}

public sealed record DesktopContextCaptureParams(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("mode")] string Mode);

public sealed record DesktopContextFileResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("extension")] string Extension,
    [property: JsonPropertyName("sizeBytes")] long? SizeBytes,
    [property: JsonPropertyName("isDirectory")] bool IsDirectory);

public sealed record DesktopContextSourceResult(
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("windowTitle")] string WindowTitle);

public sealed record DesktopContextCaptureResult(
    [property: JsonPropertyName("contextId")] Guid ContextId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("source")] DesktopContextSourceResult Source,
    [property: JsonPropertyName("clipboardRestored")] bool ClipboardRestored,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("files")] IReadOnlyList<DesktopContextFileResult>? Files = null);

public sealed record DesktopContextReleaseParams(
    [property: JsonPropertyName("operationId")] Guid OperationId);

public sealed record DesktopContextReleaseResult(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("released")] bool Released);

public sealed record DesktopContextReplaceSelectionParams(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("contextId")] Guid ContextId,
    [property: JsonPropertyName("text")] string Text);

public sealed record DesktopContextReplaceSelectionResult(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("contextId")] Guid ContextId,
    [property: JsonPropertyName("replaced")] bool Replaced,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("clipboardRestored")] bool ClipboardRestored,
    [property: JsonPropertyName("source")] DesktopContextSourceResult Source);

public sealed record DesktopContextFileOperationParams(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("contextId")] Guid ContextId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("newName")] string? NewName = null);

public sealed record DesktopContextFileOperationResult(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("contextId")] Guid ContextId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("files")] IReadOnlyList<DesktopContextFileResult> Files,
    [property: JsonPropertyName("destinationFolder")] DesktopContextFileResult? DestinationFolder = null);

public sealed record DesktopContextResolveSelectedDocumentParams(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("contextId")] Guid ContextId);

/// <summary>
/// Trusted Core-to-Electron-main handoff. InternalPath must never be forwarded
/// to the renderer or included in a model tool result.
/// </summary>
public sealed record DesktopContextResolveSelectedDocumentResult(
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("contextId")] Guid ContextId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("extension")] string Extension,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("lastWriteTimeUnixMs")] long LastWriteTimeUnixMs,
    [property: JsonPropertyName("creationTimeUnixMs")] long CreationTimeUnixMs,
    [property: JsonPropertyName("internalPath")] string InternalPath);
