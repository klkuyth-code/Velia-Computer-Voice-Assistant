using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Velia.Core.WindowsControl;

namespace Velia.Core.Documents;

internal sealed record DesktopWordDocumentCreateCommand(
    string FileName,
    bool OpenAfterCreate = true,
    string InitialText = "");

internal sealed record DesktopWordDocumentCreateResult(
    bool Verified,
    bool Opened,
    string State,
    string FileName,
    string FullPath);

internal interface IDesktopWordAutomationService
{
    Task<string?> TryGetCurrentDocumentPathAsync(CancellationToken cancellationToken);

    Task<bool> OpenAsync(string fullPath, CancellationToken cancellationToken);

    Task<DesktopTextEditorResult> ExecuteAsync(
        DesktopTextDocumentEditCommand command,
        string fullPath,
        CancellationToken cancellationToken);
}

internal interface IDesktopWordDocumentWorkflowService
{
    Task<string?> TryBindCurrentAsync(CancellationToken cancellationToken);

    Task<DesktopFileOpenResult?> TryOpenExistingAsync(
        DesktopFileOpenCommand command,
        CancellationToken cancellationToken);

    Task<DesktopWordDocumentCreateResult> CreateAsync(
        DesktopWordDocumentCreateCommand command,
        CancellationToken cancellationToken);

    Task<DesktopTextDocumentEditResult> ExecuteAsync(
        DesktopTextDocumentEditCommand command,
        string fileName,
        CancellationToken cancellationToken);
}

internal static partial class DesktopWordDocumentCreationIntentDetector
{
    private static readonly string[] DeniedOrInstructionalTerms = ["不要", "别", "怎么", "如何"];

    public static bool TryParseExplicitCommand(string request, out DesktopWordDocumentCreateCommand command)
    {
        command = null!;
        if (string.IsNullOrWhiteSpace(request)) return false;
        var value = request.Normalize(NormalizationForm.FormKC).Trim().TrimEnd('。', '！', '!', '？', '?');
        if ((!value.Contains("创建", StringComparison.Ordinal) && !value.Contains("新建", StringComparison.Ordinal)) ||
            (!value.Contains("Word", StringComparison.OrdinalIgnoreCase) && !value.Contains("沃德文档", StringComparison.Ordinal)) ||
            DeniedOrInstructionalTerms.Any(value.Contains))
            return false;
        var match = NameAfterActionPattern().Match(value);
        var before = NameBeforeKindPattern().Match(value);
        var name = match.Success ? match.Groups["name"].Value : before.Success ? before.Groups["name"].Value : string.Empty;
        name = name.Trim(' ', '：', ':', '“', '”', '"', '‘', '’');
        if (name.EndsWith('的')) name = name[..^1].TrimEnd();
        if (name.Length is < 1 or > 100) return false;
        if (!name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) name += ".docx";
        command = new DesktopWordDocumentCreateCommand(name);
        return true;
    }

    public static string GetCanonicalKey(DesktopWordDocumentCreateCommand command) =>
        $"document-create:word:{Normalize(command.FileName)}";

    private static string Normalize(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[\s\p{P}\p{S}]", string.Empty);

    [GeneratedRegex(@"(?:改名|更名|命名|取名|起名|文件名|名字)(?:叫做|叫|为|是)?\s*[：:]?\s*(?<name>[^，。！？!?\r\n]{1,100}?)(?=\s*[，,]?\s*(?:然后|并且|并打开|再打开|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex NameAfterActionPattern();

    [GeneratedRegex("""(?:名为|叫做)\s*[“"]?(?<name>[^“”"，。！？!?\r\n]{1,100}?)[”"]?\s*的(?:Word|沃德)文档""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex NameBeforeKindPattern();
}

internal sealed class DesktopWordDocumentWorkflowService(
    IDesktopWordAutomationService automation,
    DesktopFileOpenService? fileResolver = null,
    string? desktopPath = null) : IDesktopWordDocumentWorkflowService
{
    private readonly string desktop = Path.GetFullPath(
        desktopPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

    public async Task<string?> TryBindCurrentAsync(CancellationToken cancellationToken)
    {
        var candidate = await automation.TryGetCurrentDocumentPathAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        try
        {
            var fullPath = Path.GetFullPath(candidate);
            if (!string.Equals(Path.GetDirectoryName(fullPath), desktop, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath))
                return null;
            return ValidateName(Path.GetFileName(fullPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public async Task<DesktopFileOpenResult?> TryOpenExistingAsync(
        DesktopFileOpenCommand command,
        CancellationToken cancellationToken)
    {
        if (fileResolver is null) return null;
        var file = fileResolver.ResolveFile(command.RequestedName);
        if (!file.Extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)) return null;
        if (!await automation.OpenAsync(file.FullName, cancellationToken).ConfigureAwait(false))
            throw new WindowsControlException(
                "desktop_file_open_unverified", $"已请求打开桌面文件“{file.Name}”，但没有验证到对应文档窗口。", true);
        return new DesktopFileOpenResult(true, "opened", file.Name);
    }

    public async Task<DesktopWordDocumentCreateResult> CreateAsync(
        DesktopWordDocumentCreateCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var fileName = ValidateName(command.FileName);
        if (!Directory.Exists(desktop))
            throw new DesktopDocumentWorkflowException("desktop_unavailable", "当前用户桌面不可用。", true);
        var target = Path.GetFullPath(Path.Combine(desktop, fileName));
        if (Path.GetDirectoryName(target) != desktop) throw InvalidName();
        if (File.Exists(target))
            throw new DesktopDocumentWorkflowException(
                "document_already_exists", $"桌面上已经存在“{fileName}”，为避免覆盖没有创建新文件。" );
        var temporary = Path.Combine(desktop, $".velia-{Guid.NewGuid():N}.docx.tmp");
        var moved = false;
        try
        {
            using (var document = WordprocessingDocument.Create(temporary, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                var body = new Body();
                if (!string.IsNullOrEmpty(command.InitialText))
                    body.Append(new Paragraph(new Run(new Text(command.InitialText) { Space = SpaceProcessingModeValues.Preserve })));
                else
                    body.Append(new Paragraph());
                main.Document = new Document(body);
                main.Document.Save();
            }
            ValidatePackage(temporary);
            File.Move(temporary, target, false);
            moved = true;
            if (!command.OpenAfterCreate)
                return new DesktopWordDocumentCreateResult(true, false, "created", fileName, target);
            var opened = await automation.OpenAsync(target, cancellationToken).ConfigureAwait(false);
            if (!opened)
                throw new DesktopDocumentWorkflowException(
                    "document_created_open_failed", $"“{fileName}”已经创建，但没有验证到 Word 窗口。", true, true);
            return new DesktopWordDocumentCreateResult(true, true, "createdAndOpened", fileName, target);
        }
        catch (DesktopDocumentWorkflowException) { throw; }
        catch (IOException exception)
        {
            throw new DesktopDocumentWorkflowException(
                File.Exists(target) ? "document_already_exists" : "document_create_failed",
                File.Exists(target) ? $"桌面上已经存在“{fileName}”，为避免覆盖没有创建新文件。" : "Word 文档创建失败。",
                !File.Exists(target), moved, exception);
        }
        finally
        {
            if (!moved && File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    public async Task<DesktopTextDocumentEditResult> ExecuteAsync(
        DesktopTextDocumentEditCommand command,
        string fileName,
        CancellationToken cancellationToken)
    {
        var safeName = ValidateName(fileName);
        var path = Path.Combine(desktop, safeName);
        if (!File.Exists(path))
            throw new DesktopDocumentWorkflowException("document_not_found", $"桌面上没有找到“{safeName}”。", true);
        var result = await automation.ExecuteAsync(command, path, cancellationToken).ConfigureAwait(false);
        if (!result.Verified)
            throw new DesktopDocumentWorkflowException("document_edit_unverified", "Word 没有确认文档操作结果。", true);
        if (!await VerifySavedPackageAsync(path, result.Content, cancellationToken).ConfigureAwait(false))
            throw new DesktopDocumentWorkflowException("document_save_unverified", "Word 文档没有通过磁盘内容验证。", true);
        // Foreground ownership is secondary shell state. COM plus saved DOCX
        // verification proves the requested edit; a transient Windows focus
        // refusal must not turn a completed edit into a false failure.
        return new DesktopTextDocumentEditResult(true, command.Action, safeName, result.Content.Length, result.Closed);
    }

    internal static string ReadPackageText(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var document = WordprocessingDocument.Open(stream, false);
        var root = document.MainDocumentPart?.Document ?? throw new InvalidDataException("DOCX document missing.");
        var body = root.Body ?? throw new InvalidDataException("DOCX body missing.");
        return string.Join("\n", body.Elements<Paragraph>().Select(paragraph => paragraph.InnerText));
    }

    internal static void WritePackageText(string path, string text)
    {
        using var document = WordprocessingDocument.Open(path, true);
        var root = document.MainDocumentPart?.Document ?? throw new InvalidDataException("DOCX document missing.");
        root.Body = new Body(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Select(line => new Paragraph(new Run(new Text(line) { Space = SpaceProcessingModeValues.Preserve }))));
        root.Save();
    }

    private static void ValidatePackage(string path) => _ = ReadPackageText(path);

    private static async Task<bool> VerifySavedPackageAsync(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 21; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (StringComparer.Ordinal.Equals(Normalize(ReadPackageText(path)), Normalize(expected)))
                    return true;
            }
            catch (IOException) { }
            catch (InvalidDataException) { }
            catch (OpenXmlPackageException) { }
            if (attempt < 20)
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();

    private static string ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.EndsWith(' ')) throw InvalidName();
        var name = value.Normalize(NormalizationForm.FormKC).Trim();
        if (name != Path.GetFileName(name) || name.Length > 120 ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) throw InvalidName();
        return name;
    }

    private static DesktopDocumentWorkflowException InvalidName() =>
        new("document_name_invalid", "Word 文档名称无效，请使用普通文件名和 .docx 扩展名。" );
}

internal sealed class WindowsWordAutomationService : IDesktopWordAutomationService, IDisposable
{
    private const int ShowMaximized = 3;
    private const int ShowRestored = 9;
    private const uint SetWindowNoSize = 0x0001;
    private const uint SetWindowNoMove = 0x0002;
    private const uint SetWindowShow = 0x0040;
    private static readonly nint WindowTopMost = new(-1);
    private static readonly nint WindowNotTopMost = new(-2);
    private static readonly TimeSpan DocumentReadyTimeout = TimeSpan.FromSeconds(6);
    private static readonly string[] CompatibleWriterProgIds = ["KWPS.Application", "Word.Application"];
    private static readonly string[] PreferredWriterProgIds = ["KWPS.Application", "Word.Application"];
    private readonly BlockingCollection<Action> work = new();
    private readonly Dictionary<string, RunningDocument> openDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread worker;
    private int disposed;

    public WindowsWordAutomationService()
    {
        worker = new Thread(RunWorker) { IsBackground = true, Name = "Velia Word Automation" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public Task<string?> TryGetCurrentDocumentPathAsync(CancellationToken cancellationToken) =>
        RunStaAsync(() => TryGetCurrentDocumentPath(cancellationToken));

    public Task<bool> OpenAsync(string fullPath, CancellationToken cancellationToken) =>
        RunStaAsync(() => Open(fullPath, cancellationToken));

    public Task<DesktopTextEditorResult> ExecuteAsync(
        DesktopTextDocumentEditCommand command,
        string fullPath,
        CancellationToken cancellationToken) => RunStaAsync(() => Execute(command, fullPath, cancellationToken));

    internal Task<bool> SelectRangeAsync(
        string fullPath,
        int start,
        int end,
        CancellationToken cancellationToken) =>
        RunStaAsync(() => SelectRange(fullPath, start, end, cancellationToken));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        work.CompleteAdding();
        if (Thread.CurrentThread != worker) worker.Join(TimeSpan.FromSeconds(5));
        work.Dispose();
    }

    private bool Open(string fullPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = NormalizePath(fullPath);
        if (openDocuments.TryGetValue(key, out var existing))
        {
            if (BringWriterWindowToForeground(existing.Application, existing.Document, fullPath)) return true;
            RemoveOpenDocument(fullPath);
        }
        var alreadyOpenWindow = FindWriterDocumentWindow(fullPath);
        if (alreadyOpenWindow is not null and not 0)
        {
            _ = RestoreMaximizeAndForeground(alreadyOpenWindow.Value);
            return IsWindowVisible(alreadyOpenWindow.Value) && !IsIconic(alreadyOpenWindow.Value);
        }
        foreach (var progId in PreferredWriterProgIds)
        {
            object? applicationObject = null;
            object? documentsObject = null;
            object? documentObject = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var applicationType = Type.GetTypeFromProgID(progId, throwOnError: false);
                if (applicationType is null) continue;
                applicationObject = Activator.CreateInstance(applicationType);
                if (applicationObject is null) continue;
                dynamic application = applicationObject;
                application.Visible = true;
                documentsObject = application.Documents;
                dynamic documents = documentsObject;
                documentObject = documents.Open(fullPath);
                _ = BringWriterWindowToForeground(applicationObject, documentObject, fullPath);
                if (FindWriterDocumentWindow(fullPath) is not { } visibleWindow ||
                    !IsWindowVisible(visibleWindow)) return false;
                openDocuments[key] = new RunningDocument(applicationObject, documentObject);
                applicationObject = null;
                documentObject = null;
                return true;
            }
            catch (COMException) { }
            finally
            {
                Release(documentObject);
                Release(documentsObject);
                Release(applicationObject);
            }
        }
        return false;
    }

    private string? TryGetCurrentDocumentPath(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var foregroundWindow = GetForegroundWindow();
        var requireForegroundMatch = IsWriterWindow(foregroundWindow);
        foreach (var opened in openDocuments.Values.Reverse())
        {
            if (TryReadEditableDocumentPath(opened.Document, out var cachedPath) &&
                (!requireForegroundMatch || FindWriterDocumentWindow(cachedPath) == foregroundWindow))
                return cachedPath;
        }

        foreach (var progId in CompatibleWriterProgIds)
        {
            object? applicationObject = null;
            object? documentObject = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                applicationObject = TryGetRunningWriter(progId);
                if (applicationObject is null) continue;
                dynamic application = applicationObject;
                documentObject = application.ActiveDocument;
                if (documentObject is null || !TryReadEditableDocumentPath(documentObject, out var fullPath))
                    continue;
                if (requireForegroundMatch && FindWriterDocumentWindow(fullPath) != foregroundWindow)
                    continue;
                var key = NormalizePath(fullPath);
                if (!openDocuments.ContainsKey(key))
                {
                    openDocuments[key] = new RunningDocument(applicationObject, documentObject);
                    applicationObject = null;
                    documentObject = null;
                }
                return fullPath;
            }
            catch (COMException) { }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            finally
            {
                Release(documentObject);
                Release(applicationObject);
            }
        }
        return null;
    }

    private static bool IsWriterWindow(nint window)
    {
        if (window == 0 || GetWindowThreadProcessId(window, out var processId) == 0) return false;
        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            return process.ProcessName.Equals("wps", StringComparison.OrdinalIgnoreCase) ||
                   process.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool TryReadEditableDocumentPath(object documentObject, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            dynamic document = documentObject;
            if ((bool)document.ReadOnly) return false;
            var candidate = (string)document.FullName;
            if (string.IsNullOrWhiteSpace(candidate) ||
                !candidate.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                return false;
            fullPath = NormalizePath(candidate);
            return true;
        }
        catch (COMException) { return false; }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    private static bool BringWriterWindowToForeground(
        object applicationObject,
        object documentObject,
        string fullPath)
    {
        object? windowObject = null;
        try
        {
            dynamic application = applicationObject;
            dynamic document = documentObject;
            application.Visible = true;
            document.Activate();
            windowObject = application.ActiveWindow;
            dynamic window = windowObject;
            window.WindowState = 1;
            window.Activate();
        }
        catch (COMException) { }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally
        {
            Release(windowObject);
        }
        var deadline = DateTimeOffset.UtcNow + DocumentReadyTimeout;
        nint? handle;
        do
        {
            handle = FindWriterDocumentWindow(fullPath);
            if (handle is not null and not 0) break;
            if (DateTimeOffset.UtcNow >= deadline) return false;
            Thread.Sleep(100);
        }
        while (true);
        return RestoreMaximizeAndForeground(handle.Value);
    }

    private static nint? FindWriterDocumentWindow(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath).Normalize(NormalizationForm.FormKC);
        var stem = Path.GetFileNameWithoutExtension(fullPath).Normalize(NormalizationForm.FormKC);
        nint? found = null;
        nint? readOnlyFallback = null;
        _ = EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            var titleLength = GetWindowTextLength(window);
            if (titleLength <= 0) return true;
            var buffer = new char[Math.Min(1_024, titleLength + 1)];
            var copied = GetWindowText(window, buffer, buffer.Length);
            if (copied <= 0) return true;
            var title = new string(buffer, 0, copied).Normalize(NormalizationForm.FormKC);
            if (!title.Contains(fileName, StringComparison.OrdinalIgnoreCase) &&
                !title.Contains(stem, StringComparison.OrdinalIgnoreCase)) return true;
            if (GetWindowThreadProcessId(window, out var processId) == 0) return true;
            try
            {
                using var process = Process.GetProcessById(unchecked((int)processId));
                if (!process.ProcessName.Equals("wps", StringComparison.OrdinalIgnoreCase) &&
                    !process.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
            if (title.Contains("只读", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("read-only", StringComparison.OrdinalIgnoreCase))
            {
                readOnlyFallback ??= window;
                return true;
            }
            found = window;
            return false;
        }, 0);
        return found ?? readOnlyFallback;
    }

    private static bool RestoreMaximizeAndForeground(nint window)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            ActivateWindow(window);
            if (IsWindowVisible(window) &&
                !IsIconic(window) &&
                IsZoomed(window) &&
                GetForegroundWindow() == window)
                return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static void ActivateWindow(nint window)
    {
        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(window, out _);
        var foregroundWindow = GetForegroundWindow();
        var foregroundThread = foregroundWindow == 0
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        var targetAttached = targetThread != 0 && targetThread != currentThread &&
            AttachThreadInput(currentThread, targetThread, true);
        var foregroundAttached = foregroundThread != 0 &&
            foregroundThread != currentThread &&
            foregroundThread != targetThread &&
            AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            _ = ShowWindow(window, ShowRestored);
            _ = ShowWindow(window, ShowMaximized);
            _ = SetWindowPos(
                window,
                WindowTopMost,
                0,
                0,
                0,
                0,
                SetWindowNoSize | SetWindowNoMove | SetWindowShow);
            _ = SetWindowPos(
                window,
                WindowNotTopMost,
                0,
                0,
                0,
                0,
                SetWindowNoSize | SetWindowNoMove | SetWindowShow);
            _ = BringWindowToTop(window);
            _ = SetForegroundWindow(window);
            _ = SetActiveWindow(window);
            _ = SetFocus(window);
        }
        finally
        {
            if (foregroundAttached) _ = AttachThreadInput(currentThread, foregroundThread, false);
            if (targetAttached) _ = AttachThreadInput(currentThread, targetThread, false);
        }
    }

    private DesktopTextEditorResult Execute(
        DesktopTextDocumentEditCommand command,
        string fullPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var opened = WaitForOpenDocument(fullPath, cancellationToken);
        if (opened is null)
            throw new DesktopDocumentWorkflowException("document_window_mismatch", $"Word 中没有打开“{Path.GetFileName(fullPath)}”。", true);
        try
        {
            dynamic document = opened.Document;
            object? contentObject = null;
            string verified;
            try
            {
                contentObject = document.Content;
                dynamic contentRange = contentObject;
                var content = NormalizeWordText((string)contentRange.Text);
                string? expected = command.Action switch
                {
                    DesktopTextDocumentEditActions.Append => Append(content, command.Text!),
                    DesktopTextDocumentEditActions.SetLine => DesktopTextDocumentLineEditor.SetLine(content, command.LineNumber!.Value, command.Text!),
                    DesktopTextDocumentEditActions.InsertLine => DesktopTextDocumentLineEditor.InsertLine(content, command.LineNumber!.Value, command.Text!),
                    DesktopTextDocumentEditActions.DeleteLine => DesktopTextDocumentLineEditor.DeleteLine(content, command.LineNumber!.Value),
                    DesktopTextDocumentEditActions.Replace => Replace(content, command.Match!, command.Text!),
                    DesktopTextDocumentEditActions.Delete => Replace(content, command.Match!, string.Empty),
                    DesktopTextDocumentEditActions.Clear => string.Empty,
                    DesktopTextDocumentEditActions.Clean => DocumentContentCleaner.CleanText(content),
                    DesktopTextDocumentEditActions.DeleteSelection => null,
                    DesktopTextDocumentEditActions.Save or DesktopTextDocumentEditActions.Close => content,
                    _ => throw new DesktopDocumentWorkflowException("document_edit_not_allowed", "Word 操作不在本地白名单中。"),
                };
                ApplyTextMutation(command, opened, document, contentObject, content);
                verified = SaveAndVerifyMutation(
                    document,
                    content,
                    expected,
                    command.Action,
                    cancellationToken);
            }
            finally
            {
                Release(contentObject);
            }
            var closed = command.Action == DesktopTextDocumentEditActions.Close;
            var foregrounded = true;
            if (closed)
                document.Close(-1);
            else
                foregrounded = BringWriterWindowToForeground(opened.Application, opened.Document, fullPath);
            var result = new DesktopTextEditorResult(
                true,
                command.Action,
                verified,
                Path.GetFileName(fullPath),
                closed,
                foregrounded);
            if (closed) RemoveOpenDocument(fullPath);
            return result;
        }
        catch (COMException exception)
        {
            RemoveOpenDocument(fullPath);
            throw new DesktopDocumentWorkflowException(
                "document_window_mismatch", $"Word 中没有打开“{Path.GetFileName(fullPath)}”。", true, false, exception);
        }
    }

    private bool SelectRange(
        string fullPath,
        int start,
        int end,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (start < 0 || end <= start) return false;
        var opened = WaitForOpenDocument(fullPath, cancellationToken);
        if (opened is null) return false;
        object? rangeObject = null;
        try
        {
            dynamic document = opened.Document;
            rangeObject = document.Range(start, end);
            dynamic range = rangeObject;
            range.Select();
            return Convert.ToInt32(range.Start, System.Globalization.CultureInfo.InvariantCulture) == start &&
                   Convert.ToInt32(range.End, System.Globalization.CultureInfo.InvariantCulture) == end;
        }
        catch (COMException) { return false; }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return false; }
        finally
        {
            ReleaseSingleReference(rangeObject);
        }
    }

    internal static string SaveAndVerifyMutation(
        object documentObject,
        string before,
        string? expected,
        string action,
        CancellationToken cancellationToken)
    {
        string verified = before;
        var mutationObserved = false;
        for (var attempt = 0; attempt < 21; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            verified = ReadCurrentDocumentText(documentObject);
            mutationObserved = expected is not null
                ? StringComparer.Ordinal.Equals(NormalizeWordText(expected), verified)
                : action == DesktopTextDocumentEditActions.DeleteSelection &&
                  !StringComparer.Ordinal.Equals(before, verified) &&
                  verified.Length < before.Length;
            if (mutationObserved) break;
            if (attempt < 20) Thread.Sleep(25);
        }
        if (!mutationObserved)
            throw new DesktopDocumentWorkflowException(
                "document_edit_unverified",
                action == DesktopTextDocumentEditActions.DeleteSelection
                    ? "Word 选中文字删除没有通过回读验证。"
                    : "Word 文档修改没有通过回读验证。",
                true);

        dynamic document = documentObject;
        document.Save();
        for (var attempt = 0; attempt < 21; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Convert.ToBoolean(document.Saved, System.Globalization.CultureInfo.InvariantCulture))
                    return ReadCurrentDocumentText(documentObject);
            }
            catch (COMException) { return ReadCurrentDocumentText(documentObject); }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                return ReadCurrentDocumentText(documentObject);
            }
            if (attempt == 4) document.Save();
            if (attempt < 20) Thread.Sleep(25);
        }
        throw new DesktopDocumentWorkflowException(
            "document_save_unverified",
            "Word 文档没有确认保存完成。",
            true);
    }

    private static string ReadCurrentDocumentText(object documentObject)
    {
        object? contentObject = null;
        try
        {
            dynamic document = documentObject;
            contentObject = document.Content;
            dynamic content = contentObject;
            return NormalizeWordText(Convert.ToString(
                content.Text,
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        }
        finally
        {
            Release(contentObject);
        }
    }

    private RunningDocument? WaitForOpenDocument(string fullPath, CancellationToken cancellationToken)
    {
        var key = NormalizePath(fullPath);
        if (openDocuments.TryGetValue(key, out var cached))
        {
            if (TryReadEditableDocumentPath(cached.Document, out var cachedPath) &&
                PathsReferToSameFile(cachedPath, fullPath))
                return cached;
            RemoveOpenDocument(fullPath);
        }
        var deadline = DateTimeOffset.UtcNow + DocumentReadyTimeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var progId in CompatibleWriterProgIds)
            {
                var application = TryGetRunningWriter(progId);
                if (application is null) continue;
                var document = TryFindOpenDocument(application, fullPath);
                if (document is not null)
                {
                    var opened = new RunningDocument(application, document);
                    openDocuments[key] = opened;
                    return opened;
                }
                Release(application);
            }
            if (DateTimeOffset.UtcNow >= deadline) break;
            Thread.Sleep(100);
        }
        while (true);
        return null;
    }

    private static object? TryFindOpenDocument(object application, string fullPath)
    {
        object? documentsObject = null;
        try
        {
            dynamic writer = application;
            documentsObject = writer.Documents;
            dynamic documents = documentsObject;
            for (var index = 1; index <= (int)documents.Count; index++)
            {
                object? candidateObject = null;
                try
                {
                    candidateObject = documents.Item(index);
                    dynamic candidate = candidateObject;
                    if (!PathsReferToSameFile((string)candidate.FullName, fullPath)) continue;
                    var result = candidateObject;
                    candidateObject = null;
                    return result;
                }
                finally
                {
                    Release(candidateObject);
                }
            }
            return null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Release(documentsObject);
        }
    }

    private void RemoveOpenDocument(string fullPath)
    {
        if (!openDocuments.Remove(NormalizePath(fullPath), out var opened)) return;
        Release(opened.Document);
        Release(opened.Application);
    }

    private void RunWorker()
    {
        try
        {
            foreach (var action in work.GetConsumingEnumerable()) action();
        }
        finally
        {
            foreach (var opened in openDocuments.Values)
            {
                Release(opened.Document);
                Release(opened.Application);
            }
            openDocuments.Clear();
        }
    }

    private Task<T> RunStaAsync<T>(Func<T> action)
    {
        if (Volatile.Read(ref disposed) != 0)
            return Task.FromException<T>(new ObjectDisposedException(nameof(WindowsWordAutomationService)));
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            work.Add(() =>
            {
                try { completion.SetResult(action()); }
                catch (Exception exception) { completion.SetException(exception); }
            });
        }
        catch (InvalidOperationException)
        {
            completion.SetException(new ObjectDisposedException(nameof(WindowsWordAutomationService)));
        }
        return completion.Task;
    }

    private static bool PathsReferToSameFile(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string value) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));

    private static object? TryGetRunningWriter(string progId)
    {
        var classId = Guid.Empty;
        if (ClsidFromProgId(progId, out classId) != 0 ||
            GetActiveObject(ref classId, 0, out var instance) != 0 || instance is null)
            return null;
        return instance;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private static void ReleaseSingleReference(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.ReleaseComObject(value);
    }

    private static string Append(string current, string text) =>
        current.Length == 0 ? text : current + "\n" + text;

    private static void ApplyTextMutation(
        DesktopTextDocumentEditCommand command,
        RunningDocument opened,
        object documentObject,
        object contentObject,
        string current)
    {
        dynamic document = documentObject;
        dynamic content = contentObject;
        switch (command.Action)
        {
            case DesktopTextDocumentEditActions.Append:
                content.Collapse(0);
                content.InsertAfter(ToWriterText(current.Length == 0 ? command.Text! : "\n" + command.Text!));
                return;
            case DesktopTextDocumentEditActions.SetLine:
                ApplyLogicalLineMutation(documentObject, current, command, insert: false, delete: false);
                return;
            case DesktopTextDocumentEditActions.InsertLine:
                ApplyLogicalLineMutation(documentObject, current, command, insert: true, delete: false);
                return;
            case DesktopTextDocumentEditActions.DeleteLine:
                ApplyLogicalLineMutation(documentObject, current, command, insert: false, delete: true);
                return;
            case DesktopTextDocumentEditActions.Replace:
                ReplaceAllPreservingFormatting(documentObject, command.Match!, command.Text!);
                return;
            case DesktopTextDocumentEditActions.Delete:
                ReplaceAllPreservingFormatting(documentObject, command.Match!, string.Empty);
                return;
            case DesktopTextDocumentEditActions.DeleteSelection:
                DeleteCurrentSelection(opened.Application, documentObject);
                return;
            case DesktopTextDocumentEditActions.Clear:
                content.Delete();
                return;
            case DesktopTextDocumentEditActions.Clean:
                CleanParagraphsPreservingFormatting(documentObject);
                return;
            case DesktopTextDocumentEditActions.Save:
            case DesktopTextDocumentEditActions.Close:
                return;
            default:
                throw new DesktopDocumentWorkflowException("document_edit_not_allowed", "Word 操作不在本地白名单中。");
        }
    }

    private static void ApplyLogicalLineMutation(
        object documentObject,
        string current,
        DesktopTextDocumentEditCommand command,
        bool insert,
        bool delete)
    {
        var lineNumber = command.LineNumber!.Value;
        var lines = current.Split('\n').ToList();
        if (delete && lineNumber > lines.Count)
            throw new DesktopDocumentWorkflowException(
                "document_line_not_found",
                $"当前 Word 文档没有第 {lineNumber} 行。" );

        // Word and WPS expose paragraph positions with one paragraph mark per
        // logical line. Replacing only the selected paragraph range preserves
        // formatting in every unaffected paragraph.
        var start = 0;
        for (var index = 0; index < Math.Min(lineNumber - 1, lines.Count); index++)
            start += lines[index].Length + 1;
        object? rangeObject = null;
        try
        {
            dynamic document = documentObject;
            if (lineNumber <= lines.Count)
            {
                var existingLength = lines[lineNumber - 1].Length;
                var end = delete
                    ? start + existingLength + (lineNumber < lines.Count ? 1 : 0)
                    : start + (insert ? 0 : existingLength);
                rangeObject = document.Range(start, end);
                dynamic range = rangeObject;
                range.Text = delete
                    ? string.Empty
                    : ToWriterText(command.Text! + (insert ? "\n" : string.Empty));
                return;
            }

            rangeObject = document.Content;
            dynamic content = rangeObject;
            content.Collapse(0);
            var missingSeparators = lineNumber - lines.Count;
            content.InsertAfter(ToWriterText(new string('\n', missingSeparators) + command.Text!));
        }
        finally
        {
            Release(rangeObject);
        }
    }

    private static void ReplaceAllPreservingFormatting(object documentObject, string match, string replacement)
    {
        object? rangeObject = null;
        object? findObject = null;
        object? replacementObject = null;
        try
        {
            dynamic document = documentObject;
            rangeObject = document.Content;
            dynamic range = rangeObject;
            findObject = range.Find;
            dynamic find = findObject;
            find.ClearFormatting();
            replacementObject = find.Replacement;
            dynamic replace = replacementObject;
            replace.ClearFormatting();
            find.Text = match;
            replace.Text = replacement;
            find.Forward = true;
            find.Wrap = 1;
            find.Format = false;
            find.MatchCase = true;
            find.MatchWholeWord = false;
            find.MatchWildcards = false;
            _ = find.Execute(Replace: 2);
        }
        finally
        {
            Release(replacementObject);
            Release(findObject);
            Release(rangeObject);
        }
    }

    internal static void DeleteCurrentSelection(object applicationObject, object documentObject)
    {
        object? selectionObject = null;
        object? activeDocumentObject = null;
        try
        {
            dynamic application = applicationObject;
            selectionObject = application.Selection;
            if (selectionObject is null)
                throw new DesktopDocumentWorkflowException("document_selection_missing", "当前 Word 文档没有可删除的选中文字。", true);
            dynamic selection = selectionObject;
            activeDocumentObject = application.ActiveDocument;
            if (activeDocumentObject is null)
                throw new DesktopDocumentWorkflowException("document_window_mismatch", "当前没有活动的 Word 文档。", true);
            dynamic activeDocument = activeDocumentObject;
            dynamic document = documentObject;
            if (!PathsReferToSameFile((string)activeDocument.FullName, (string)document.FullName))
                throw new DesktopDocumentWorkflowException("document_window_mismatch", "当前选区不属于已绑定的 Word 文档。", true);
            var start = Convert.ToInt64(selection.Start, System.Globalization.CultureInfo.InvariantCulture);
            var end = Convert.ToInt64(selection.End, System.Globalization.CultureInfo.InvariantCulture);
            var selectedValue = selection.Text;
            var selected = selectedValue is null
                ? string.Empty
                : NormalizeWordText(Convert.ToString(
                    selectedValue,
                    System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            if (end <= start || selected.Length == 0)
                throw new DesktopDocumentWorkflowException("document_selection_missing", "当前 Word 文档没有可删除的选中文字。", true);
            selection.Delete();
        }
        finally
        {
            // ActiveDocument can share the cached document RCW used immediately
            // afterward for Save. FinalReleaseComObject would disconnect it.
            ReleaseSingleReference(activeDocumentObject);
            ReleaseSingleReference(selectionObject);
        }
    }

    private static void CleanParagraphsPreservingFormatting(object documentObject)
    {
        object? paragraphsObject = null;
        try
        {
            dynamic document = documentObject;
            paragraphsObject = document.Paragraphs;
            dynamic paragraphs = paragraphsObject;
            for (var index = (int)paragraphs.Count; index >= 1; index--)
            {
                object? paragraphObject = null;
                object? rangeObject = null;
                try
                {
                    paragraphObject = paragraphs.Item(index);
                    dynamic paragraph = paragraphObject;
                    rangeObject = paragraph.Range;
                    dynamic range = rangeObject;
                    var text = NormalizeWordText((string)range.Text).Trim();
                    if (DocumentContentCleaner.IsNoiseParagraph(text)) range.Delete();
                }
                finally
                {
                    Release(rangeObject);
                    Release(paragraphObject);
                }
            }

            object? contentObject = null;
            try
            {
                contentObject = document.Content;
                dynamic content = contentObject;
                var text = NormalizeWordText((string)content.Text);
                foreach (var credit in DocumentContentCleaner.FindInlineCredits(text))
                    ReplaceAllPreservingFormatting(documentObject, credit, string.Empty);
            }
            finally
            {
                Release(contentObject);
            }
        }
        finally
        {
            Release(paragraphsObject);
        }
    }

    private static string Replace(string current, string match, string replacement)
    {
        if (!current.Contains(match, StringComparison.Ordinal))
            throw new DesktopDocumentWorkflowException("document_text_not_found", $"Word 文档中没有找到“{match}”。" );
        return current.Replace(match, replacement, StringComparison.Ordinal);
    }

    private static string NormalizeWordText(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r\a", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n', '\a');

    private static string ToWriterText(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\n', '\r');

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string progId, out Guid classId);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid classId, nint reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, [Out] char[] text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private static int ClsidFromProgId(string progId, out Guid classId) => CLSIDFromProgID(progId, out classId);

    private sealed record RunningDocument(object Application, object Document);
}
