using Velia.Protocol;

namespace Velia.Core.DesktopContext;

internal interface IDesktopContextBackend
{
    Task<DesktopContextObservation> CaptureAsync(string mode, CancellationToken cancellationToken);
    Task<DesktopContextReplaceObservation> ReplaceSelectionAsync(
        DesktopContextTarget target,
        string text,
        CancellationToken cancellationToken);
}

internal sealed record DesktopContextObservation(
    string Kind,
    DesktopContextSourceResult Source,
    bool ClipboardRestored,
    nint WindowHandle,
    nint ControlHandle,
    int ProcessId,
    string? Text = null,
    IReadOnlyList<string>? FilePaths = null);

internal sealed record DesktopContextTarget(
    nint WindowHandle,
    nint ControlHandle,
    int ProcessId,
    string Kind,
    string? Text);

internal sealed record DesktopContextReplaceObservation(
    DesktopContextSourceResult Source,
    bool ClipboardRestored);

internal sealed class DesktopContextService(
    IDesktopContextBackend backend,
    TimeProvider? timeProvider = null,
    TimeSpan? contextLifetime = null,
    ISelectedFileOperator? selectedFileOperator = null)
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    private const long MaximumSelectedDocumentBytes = 20L * 1024 * 1024;
    private static readonly HashSet<string> SupportedDocumentExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".csv", ".log", ".pdf", ".docx",
        };
    private readonly object gate = new();
    private readonly Dictionary<Guid, StoredContext> contexts = [];
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan lifetime = ValidateLifetime(contextLifetime ?? DefaultLifetime);
    private readonly ISelectedFileOperator fileOperator = selectedFileOperator ?? new WindowsSelectedFileOperator();

    public async Task<DesktopContextCaptureResult> CaptureAsync(
        DesktopContextCaptureParams parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.Mode is not (DesktopContextModes.Auto or DesktopContextModes.Selection or DesktopContextModes.Clipboard))
            throw new DesktopContextException("invalid_context_mode", "桌面上下文模式无效。");
        var observation = await backend.CaptureAsync(parameters.Mode, cancellationToken).ConfigureAwait(false);
        var contextId = Guid.NewGuid();
        var expiresAt = clock.GetUtcNow().Add(lifetime);
        var files = observation.FilePaths?.Take(32).Select(ToFileResult).ToArray();
        lock (gate)
        {
            RemoveExpired();
            contexts[parameters.OperationId] = new StoredContext(
                contextId, parameters.OperationId, observation.WindowHandle, observation.ControlHandle, observation.ProcessId,
                observation.Kind, observation.Text, observation.FilePaths?.ToArray(), expiresAt);
        }
        return new DesktopContextCaptureResult(
            contextId,
            observation.Kind,
            observation.Source,
            observation.ClipboardRestored,
            expiresAt,
            observation.Text,
            files);
    }

    public DesktopContextReleaseResult Release(Guid operationId)
    {
        lock (gate) return new DesktopContextReleaseResult(operationId, contexts.Remove(operationId));
    }

    public async Task<DesktopContextReplaceSelectionResult> ReplaceSelectionAsync(
        DesktopContextReplaceSelectionParams parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.Text is null || parameters.Text.Length is < 1 or > 32_768)
            throw new DesktopContextException("invalid_replacement_text", "替换文字长度必须在 1 到 32768 个字符之间。");
        var stored = Get(parameters.OperationId, parameters.ContextId)
            ?? throw new DesktopContextException("desktop_context_stale", "桌面上下文已过期或不属于当前任务，请重新读取选中文字。");
        if (stored.Kind != DesktopContextKinds.SelectedText || string.IsNullOrEmpty(stored.Text))
            throw new DesktopContextException("context_not_replaceable", "当前上下文不是可替换的选中文字。");

        DesktopContextReplaceObservation observation;
        try
        {
            observation = await backend.ReplaceSelectionAsync(
                new DesktopContextTarget(
                    stored.WindowHandle,
                    stored.ControlHandle,
                    stored.ProcessId,
                    stored.Kind,
                    stored.Text),
                parameters.Text,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RemoveIfMatching(parameters.OperationId, parameters.ContextId);
            throw;
        }

        lock (gate)
        {
            RemoveExpired();
            if (!contexts.TryGetValue(parameters.OperationId, out var current) || current.ContextId != parameters.ContextId)
                throw new DesktopContextException("desktop_context_stale", "桌面上下文在替换期间失效。");
            contexts[parameters.OperationId] = current with
            {
                Text = parameters.Text,
                ExpiresAtUtc = clock.GetUtcNow().Add(lifetime),
            };
        }
        return new DesktopContextReplaceSelectionResult(
            parameters.OperationId,
            parameters.ContextId,
            true,
            true,
            observation.ClipboardRestored,
            observation.Source);
    }

    public async Task<DesktopContextFileOperationResult> OperateSelectedFilesAsync(
        DesktopContextFileOperationParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var validShape = parameters.Action switch
        {
            DesktopContextFileOperations.CopyToSelectedFolder or DesktopContextFileOperations.MoveToSelectedFolder =>
                parameters.NewName is null,
            DesktopContextFileOperations.RenameSelectedFile => parameters.NewName is { Length: >= 1 and <= 240 },
            _ => false,
        };
        if (!validShape)
            throw new DesktopContextException("invalid_file_operation", "选中文件操作参数无效。" );
        StoredContext stored;
        lock (gate)
        {
            RemoveExpired();
            if (!contexts.TryGetValue(parameters.OperationId, out var current) || current.ContextId != parameters.ContextId)
                throw new DesktopContextException("desktop_context_stale", "文件选区已过期或不属于当前任务，请重新选择。" );
            if (current.Kind != DesktopContextKinds.SelectedFiles || current.FilePaths is not { Count: > 0 })
                throw new DesktopContextException("context_not_file_selection", "当前上下文不是可操作的文件选区。" );
            stored = current;
            contexts.Remove(parameters.OperationId);
        }

        var observation = await fileOperator.ExecuteAsync(
            new SelectedFileOperationRequest(parameters.Action, stored.FilePaths, parameters.NewName),
            cancellationToken).ConfigureAwait(false);
        return new DesktopContextFileOperationResult(
            parameters.OperationId,
            parameters.ContextId,
            parameters.Action,
            true,
            observation.ResultPaths.Select(ToFileResult).ToArray(),
            observation.DestinationFolderPath is null ? null : ToFileResult(observation.DestinationFolderPath));
    }

    public DesktopContextResolveSelectedDocumentResult ResolveSelectedDocument(
        DesktopContextResolveSelectedDocumentParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var stored = Get(parameters.OperationId, parameters.ContextId)
            ?? throw new DesktopContextException(
                "desktop_context_stale",
                "文件选区已过期或不属于当前任务，请重新选择。" );
        if (stored.Kind != DesktopContextKinds.SelectedFiles || stored.FilePaths is not { Count: 1 })
            throw new DesktopContextException(
                "document_selection_invalid",
                "读取文档需要只选中一个受支持的文件。" );

        var path = WindowsSelectedFileOperator.NormalizeAndValidatePath(stored.FilePaths[0]);
        if (!File.Exists(path))
            throw new DesktopContextException("selected_file_missing", "选中的文档已经不存在，请重新选择。", true);
        var extension = Path.GetExtension(path);
        if (!SupportedDocumentExtensions.Contains(extension))
            throw new DesktopContextException(
                "document_type_not_supported",
                "只支持 TXT、Markdown、CSV、LOG、PDF 和 DOCX 文档。" );
        string name;
        string fullName;
        long length;
        long lastWriteTimeUnixMs;
        long creationTimeUnixMs;
        try
        {
            var info = new FileInfo(path);
            name = info.Name;
            fullName = info.FullName;
            length = info.Length;
            lastWriteTimeUnixMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            creationTimeUnixMs = new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeMilliseconds();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new DesktopContextException("document_access_failed", "无法读取选中文档的信息。", true);
        }
        if (length is <= 0 or > MaximumSelectedDocumentBytes)
            throw new DesktopContextException("document_size_invalid", "文档必须大于 0 字节且不超过 20 MB。" );

        return new DesktopContextResolveSelectedDocumentResult(
            parameters.OperationId,
            parameters.ContextId,
            name,
            extension.ToLowerInvariant(),
            length,
            lastWriteTimeUnixMs,
            creationTimeUnixMs,
            fullName);
    }

    internal StoredContext? Get(Guid operationId, Guid contextId)
    {
        lock (gate)
        {
            RemoveExpired();
            return contexts.TryGetValue(operationId, out var value) && value.ContextId == contextId ? value : null;
        }
    }

    private void RemoveExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var key in contexts.Where(item => item.Value.ExpiresAtUtc <= now).Select(item => item.Key).ToArray())
            contexts.Remove(key);
    }

    private void RemoveIfMatching(Guid operationId, Guid contextId)
    {
        lock (gate)
        {
            if (contexts.TryGetValue(operationId, out var stored) && stored.ContextId == contextId)
                contexts.Remove(operationId);
        }
    }

    internal static DesktopContextFileResult ToFileResult(string path)
    {
        var name = Path.GetFileName(path);
        var isDirectory = Directory.Exists(path);
        var extension = isDirectory ? string.Empty : Path.GetExtension(name);
        long? size = null;
        try { if (File.Exists(path)) size = new FileInfo(path).Length; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new DesktopContextFileResult(
            string.IsNullOrWhiteSpace(name) ? "未命名文件" : name[..Math.Min(name.Length, 260)],
            extension[..Math.Min(extension.Length, 32)],
            size,
            isDirectory);
    }

    private static TimeSpan ValidateLifetime(TimeSpan value) =>
        value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value));

    internal sealed record StoredContext(
        Guid ContextId,
        Guid OperationId,
        nint WindowHandle,
        nint ControlHandle,
        int ProcessId,
        string Kind,
        string? Text,
        IReadOnlyList<string>? FilePaths,
        DateTimeOffset ExpiresAtUtc);
}

internal sealed class DesktopContextException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
