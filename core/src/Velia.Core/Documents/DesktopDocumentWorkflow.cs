using System.Text;
using System.Text.RegularExpressions;
using Velia.Core.WindowsControl;

namespace Velia.Core.Documents;

internal sealed record DesktopDocumentCreateCommand(
    string FileName,
    bool OpenAfterCreate = true,
    string InitialText = "");

internal sealed record DesktopDocumentCreateResult(
    bool Verified,
    bool Opened,
    string State,
    string FileName,
    string FullPath);

internal interface IDesktopDocumentWorkflowService
{
    Task<DesktopDocumentCreateResult> CreateTextDocumentAsync(
        DesktopDocumentCreateCommand command,
        CancellationToken cancellationToken);
}

internal sealed class DesktopDocumentWorkflowException(
    string code,
    string message,
    bool retryable = false,
    bool fileCreated = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public bool FileCreated { get; } = fileCreated;
}

internal static partial class DesktopDocumentCreationIntentDetector
{
    private static readonly Regex NameAfterActionPattern = CreateNameAfterActionPattern();
    private static readonly Regex NameBeforeKindPattern = CreateNameBeforeKindPattern();
    private static readonly Regex MissingKindBeforeNamePattern = CreateMissingKindBeforeNamePattern();

    public static bool TryParseExplicitCommand(
        string request,
        out DesktopDocumentCreateCommand command)
    {
        command = null!;
        if (string.IsNullOrWhiteSpace(request)) return false;
        var normalized = request.Normalize(NormalizationForm.FormKC).Trim().TrimEnd('。', '！', '!', '？', '?');
        if ((!normalized.Contains("创建", StringComparison.Ordinal) &&
             !normalized.Contains("新建", StringComparison.Ordinal)) ||
            !LooksLikeTextDocument(normalized) ||
            IsDeniedOrInstructional(normalized))
            return false;

        var name = ExtractName(normalized);
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim(' ', '：', ':', '“', '”', '"', '‘', '’');
        if (name.EndsWith('的') && name.Length > 1)
            name = name[..^1].TrimEnd();
        if (name.Length == 0) return false;
        if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) name += ".txt";
        command = new DesktopDocumentCreateCommand(name);
        return true;
    }

    public static string GetCanonicalKey(DesktopDocumentCreateCommand command) =>
        $"document-create:text:{command.FileName.Normalize(NormalizationForm.FormKC).ToLowerInvariant()}";

    private static string? ExtractName(string request)
    {
        var after = NameAfterActionPattern.Match(request);
        if (after.Success) return after.Groups["name"].Value;
        var before = NameBeforeKindPattern.Match(request);
        return before.Success ? before.Groups["name"].Value : null;
    }

    private static bool LooksLikeTextDocument(string request)
    {
        // A bare spoken “document” defaults to the lightweight TXT workflow.
        // Keep explicitly typed office formats out so their dedicated detectors
        // remain authoritative. “完档” is a common ASR substitution for “文档”
        // and is accepted only inside this already-explicit create/new command.
        if (request.Contains("Word", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("沃德", StringComparison.Ordinal) ||
            request.Contains("WPS", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("Excel", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("XLS", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("表格", StringComparison.Ordinal) ||
            request.Contains("工作簿", StringComparison.Ordinal) ||
            request.Contains("PDF", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("PPT", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("演示文稿", StringComparison.Ordinal))
            return false;

        return request.Contains("TXT", StringComparison.OrdinalIgnoreCase) ||
               request.Contains("文本文档", StringComparison.Ordinal) ||
               request.Contains("文本文件", StringComparison.Ordinal) ||
               request.Contains("记事本", StringComparison.Ordinal) ||
               request.Contains("记事本文档", StringComparison.Ordinal) ||
               request.Contains("记事本文件", StringComparison.Ordinal) ||
               request.Contains("文档", StringComparison.Ordinal) ||
               request.Contains("完档", StringComparison.Ordinal) ||
               MissingKindBeforeNamePattern.IsMatch(request);
    }

    private static bool IsDeniedOrInstructional(string request)
    {
        var prefixLength = Math.Min(request.Length, 12);
        var prefix = request[..prefixLength];
        return prefix.Contains("不要", StringComparison.Ordinal) ||
               prefix.Contains('别') ||
               request.StartsWith("怎么", StringComparison.Ordinal) ||
               request.StartsWith("如何", StringComparison.Ordinal) ||
               request.StartsWith("能不能", StringComparison.Ordinal) ||
               request.StartsWith("是否", StringComparison.Ordinal);
    }

    [GeneratedRegex(
        @"(?:改名|更名|命名|取名|起名|文件名|名字)(?:叫做|叫|取为|为|是)?\s*[：:]?\s*(?<name>[^，。！？!?\r\n]{1,100}?)(?=\s*[，,]?\s*(?:然后|并且|并打开|再打开|$))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        50)]
    private static partial Regex CreateNameAfterActionPattern();

    [GeneratedRegex(
        """(?:名为|叫做)\s*[“"]?(?<name>[^“”"，。！？!?\r\n]{1,100}?)[”"]?\s*的(?:TXT|文本文档|文本文件|记事本文档|记事本文件)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        50)]
    private static partial Regex CreateNameBeforeKindPattern();

    // Local and provider ASR can occasionally drop a short artifact kind from
    // “创建一个记事本，命名为…”. Recover only when the slot between “一个” and
    // the explicit naming clause is empty; named folders, spreadsheets and
    // other concrete artifact types therefore remain with their own routes.
    [GeneratedRegex(
        @"(?:创建|新建)\s*(?:一个|1个)?\s*[，,]?\s*(?=(?:命名|取名|起名|文件名|名字)(?:叫做|叫|取为|为|是)?\s*[：:]?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        50)]
    private static partial Regex CreateMissingKindBeforeNamePattern();
}

internal sealed class DesktopDocumentWorkflowService(
    IDesktopFileOpenService fileOpener,
    string? desktopPath = null) : IDesktopDocumentWorkflowService
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false, true);
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
    private readonly string desktop = Path.GetFullPath(
        desktopPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

    public async Task<DesktopDocumentCreateResult> CreateTextDocumentAsync(
        DesktopDocumentCreateCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var fileName = ValidateFileName(command.FileName);
        if (!Directory.Exists(desktop))
            throw new DesktopDocumentWorkflowException(
                "desktop_unavailable",
                "当前用户桌面不可用。",
                true);

        var targetPath = Path.GetFullPath(Path.Combine(desktop, fileName));
        if (!string.Equals(Path.GetDirectoryName(targetPath), desktop, StringComparison.OrdinalIgnoreCase))
            throw InvalidName();
        if (File.Exists(targetPath))
            throw new DesktopDocumentWorkflowException(
                "document_already_exists",
                $"桌面上已经存在“{fileName}”，为避免覆盖没有创建新文件。" );

        var temporaryPath = Path.Combine(desktop, $".velia-{Guid.NewGuid():N}.tmp");
        var expectedBytes = Utf8WithoutBom.GetBytes(command.InitialText);
        var moved = false;
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4_096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(expectedBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, targetPath, overwrite: false);
            moved = true;
            if (!File.Exists(targetPath) ||
                !File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(expectedBytes))
                throw new DesktopDocumentWorkflowException(
                    "document_create_unverified",
                    "文本文档没有通过本机落盘验证。",
                    true,
                    fileCreated: File.Exists(targetPath));

            if (!command.OpenAfterCreate)
                return new DesktopDocumentCreateResult(true, false, "created", fileName, targetPath);

            try
            {
                var opened = await fileOpener.OpenAsync(
                    new DesktopFileOpenCommand(fileName),
                    cancellationToken).ConfigureAwait(false);
                if (!opened.Verified)
                    throw new WindowsControlException(
                        "desktop_file_open_unverified",
                        "Windows 没有确认新文档已经打开。",
                        true);
                return new DesktopDocumentCreateResult(true, true, "createdAndOpened", fileName, targetPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is WindowsControlException or IOException)
            {
                throw new DesktopDocumentWorkflowException(
                    "document_created_open_failed",
                    $"“{fileName}”已经安全创建，但没有验证到文档窗口。",
                    true,
                    fileCreated: true,
                    exception);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DesktopDocumentWorkflowException)
        {
            throw;
        }
        catch (IOException exception)
        {
            if (File.Exists(targetPath))
                throw new DesktopDocumentWorkflowException(
                    "document_already_exists",
                    $"桌面上已经存在“{fileName}”，为避免覆盖没有创建新文件。",
                    false,
                    fileCreated: moved,
                    exception);
            throw new DesktopDocumentWorkflowException(
                "document_create_failed",
                "文本文档创建失败。",
                true,
                fileCreated: moved,
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new DesktopDocumentWorkflowException(
                "document_create_denied",
                "Windows 没有允许在桌面创建文档。",
                false,
                fileCreated: moved,
                exception);
        }
        finally
        {
            if (!moved && File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string ValidateFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw InvalidName();
        if (value.EndsWith(' ')) throw InvalidName();
        var fileName = value.Normalize(NormalizationForm.FormKC).Trim();
        if (fileName.Length > 120 ||
            fileName != Path.GetFileName(fileName) ||
            fileName.EndsWith(' ') ||
            fileName.EndsWith('.') ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetExtension(fileName), ".txt", StringComparison.OrdinalIgnoreCase) ||
            ReservedWindowsNames.Contains(Path.GetFileNameWithoutExtension(fileName)))
            throw InvalidName();
        return fileName;
    }

    private static DesktopDocumentWorkflowException InvalidName() => new(
        "document_name_invalid",
        "文本文档名称无效，请使用普通文件名并保留 .txt 扩展名。" );
}
