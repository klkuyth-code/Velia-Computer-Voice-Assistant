using System.Text;
using System.Text.RegularExpressions;

namespace Velia.Core.WindowsControl;

internal sealed record DesktopFolderCreateCommand(
    string? FolderName,
    bool OpenAfterCreate = false);

internal sealed record DesktopFolderCreateResult(
    bool Verified,
    bool Opened,
    string State,
    string FolderName,
    string FullPath);

internal interface IDesktopFolderWorkflowService
{
    Task<DesktopFolderCreateResult> CreateAsync(
        DesktopFolderCreateCommand command,
        CancellationToken cancellationToken);
}

internal sealed class DesktopFolderWorkflowException(
    string code,
    string message,
    bool retryable = false,
    bool folderCreated = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public bool FolderCreated { get; } = folderCreated;
}

internal static partial class DesktopFolderCreationIntentDetector
{
    private static readonly string[] FolderKinds = ["文件夹", "目录"];

    public static bool TryParseExplicitCommand(
        string request,
        out DesktopFolderCreateCommand command)
    {
        command = null!;
        if (string.IsNullOrWhiteSpace(request)) return false;

        var normalized = request.Normalize(NormalizationForm.FormKC).Trim();
        var action = CreateActionPattern().Match(normalized);
        if (!action.Success || IsDeniedOrInstructional(normalized, action.Index)) return false;

        var openAfterCreate = OpenAfterCreatePattern().IsMatch(normalized);
        var target = normalized[(action.Index + action.Length)..];
        target = OpenAfterCreatePattern().Replace(target, "");
        target = StripPoliteEnding(target);
        target = StripLeadingQuantity(target);
        if (target.Length == 0) return false;

        string? name;
        var leadingKind = FolderKinds.FirstOrDefault(kind =>
            target.StartsWith(kind, StringComparison.Ordinal));
        if (leadingKind is not null)
        {
            var remainder = target[leadingKind.Length..].Trim();
            if (remainder.Length == 0)
            {
                command = new DesktopFolderCreateCommand(null, openAfterCreate);
                return true;
            }

            remainder = remainder.TrimStart(' ', '，', ',', '。', '：', ':');
            var naming = NamingPrefixPattern().Match(remainder);
            if (!naming.Success || naming.Index != 0) return false;
            name = remainder[naming.Length..];
        }
        else
        {
            var trailingKind = FolderKinds.FirstOrDefault(kind =>
                target.EndsWith(kind, StringComparison.Ordinal));
            if (trailingKind is null) return false;
            name = target[..^trailingKind.Length].TrimEnd();
            if (name.EndsWith("的新", StringComparison.Ordinal))
                name = name[..^2].TrimEnd();
            if (name.EndsWith('的')) name = name[..^1].TrimEnd();
            name = NamingPrefixPattern().Replace(name, "", 1);
        }

        name = CleanName(name);
        if (name.Length == 0) return false;
        command = new DesktopFolderCreateCommand(name, openAfterCreate);
        return true;
    }

    public static string GetCanonicalKey(DesktopFolderCreateCommand command)
    {
        var name = string.IsNullOrWhiteSpace(command.FolderName)
            ? "auto"
            : command.FolderName.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return $"folder-create:{name}:{command.OpenAfterCreate}";
    }

    private static string StripLeadingQuantity(string value)
    {
        var normalized = value.TrimStart(' ', '，', ',', '。', '：', ':');
        var prefix = LeadingQuantityPattern().Match(normalized);
        return prefix.Success && prefix.Index == 0
            ? normalized[prefix.Length..].TrimStart()
            : normalized;
    }

    private static string StripPoliteEnding(string value)
    {
        var normalized = value.Trim().TrimEnd('。', '！', '!', '？', '?', '，', ',', '：', ':');
        var ending = PoliteEndingPattern().Match(normalized);
        if (ending.Success && ending.Index + ending.Length == normalized.Length)
            normalized = normalized[..ending.Index].TrimEnd();
        return normalized.Trim().TrimEnd('。', '！', '!', '？', '?', '，', ',', '：', ':');
    }

    private static string CleanName(string value) =>
        value.Trim(' ', '，', ',', '。', '：', ':', '“', '”', '"', '‘', '’');

    private static bool IsDeniedOrInstructional(string request, int actionIndex)
    {
        var prefix = request[..Math.Min(request.Length, actionIndex + 2)];
        return prefix.Contains("不要", StringComparison.Ordinal) ||
               prefix.Contains("不用", StringComparison.Ordinal) ||
               prefix.Contains("无需", StringComparison.Ordinal) ||
               prefix.Contains('别') ||
               request.StartsWith("怎么", StringComparison.Ordinal) ||
               request.StartsWith("如何", StringComparison.Ordinal) ||
               request.StartsWith("为什么", StringComparison.Ordinal) ||
               request.StartsWith("是否", StringComparison.Ordinal) ||
               request.StartsWith("能否", StringComparison.Ordinal) ||
               request.Contains("创建文件夹的方法", StringComparison.Ordinal) ||
               request.Contains("新建文件夹的方法", StringComparison.Ordinal);
    }

    [GeneratedRegex(
        @"(?:创建|新建|建立|建)(?:一下|下)?",
        RegexOptions.CultureInvariant,
        50)]
    private static partial Regex CreateActionPattern();

    [GeneratedRegex(
        @"(?:(?:创建后|建好后|并且|并|然后|再)\s*打开|[，,]\s*打开)(?:它|这个文件夹|该文件夹|文件夹)?",
        RegexOptions.CultureInvariant,
        50)]
    private static partial Regex OpenAfterCreatePattern();

    [GeneratedRegex(
        @"^(?:(?:一个|1个|个)\s*)?(?:新的?|全新(?:的)?)?\s*",
        RegexOptions.CultureInvariant,
        50)]
    private static partial Regex LeadingQuantityPattern();

    [GeneratedRegex(
        @"^(?:并)?(?:命名|改名|更名|取名|起名|名称|名字)(?:叫做|叫|取为|为|是)?\s*[：:]?\s*|^(?:名为|叫做|叫)\s*[：:]?\s*",
        RegexOptions.CultureInvariant,
        50)]
    private static partial Regex NamingPrefixPattern();

    [GeneratedRegex(
        @"(?:吧|呀|啊|可以吗|好吗|行吗|一下)$",
        RegexOptions.CultureInvariant,
        50)]
    private static partial Regex PoliteEndingPattern();
}

internal sealed class DesktopFolderWorkflowService(
    IDesktopFileOpenService fileOpener,
    string? desktopPath = null) : IDesktopFolderWorkflowService
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly string desktop = Path.GetFullPath(
        desktopPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

    public async Task<DesktopFolderCreateResult> CreateAsync(
        DesktopFolderCreateCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(desktop))
            throw new DesktopFolderWorkflowException(
                "desktop_unavailable",
                "当前用户桌面不可用。",
                true);

        var folderName = string.IsNullOrWhiteSpace(command.FolderName)
            ? GetAvailableDefaultName()
            : ValidateFolderName(command.FolderName);
        var targetPath = Path.GetFullPath(Path.Combine(desktop, folderName));
        if (!string.Equals(Path.GetDirectoryName(targetPath), desktop, StringComparison.OrdinalIgnoreCase))
            throw InvalidName();
        if (Directory.Exists(targetPath) || File.Exists(targetPath))
            throw AlreadyExists(folderName);

        var temporaryPath = Path.Combine(desktop, $".velia-folder-{Guid.NewGuid():N}.tmp");
        var moved = false;
        try
        {
            Directory.CreateDirectory(temporaryPath);
            Directory.Move(temporaryPath, targetPath);
            moved = true;
            cancellationToken.ThrowIfCancellationRequested();

            var created = new DirectoryInfo(targetPath);
            if (!created.Exists ||
                !string.Equals(created.Name, folderName, StringComparison.Ordinal) ||
                (created.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new DesktopFolderWorkflowException(
                    "folder_create_unverified",
                    "文件夹没有通过本机落盘验证。",
                    true,
                    folderCreated: Directory.Exists(targetPath));

            if (!command.OpenAfterCreate)
                return new DesktopFolderCreateResult(true, false, "created", folderName, targetPath);

            try
            {
                var opened = await fileOpener.OpenAsync(
                    new DesktopFileOpenCommand($"{folderName}文件夹"),
                    cancellationToken).ConfigureAwait(false);
                if (!opened.Verified || !opened.IsFolder)
                    throw new WindowsControlException(
                        "desktop_folder_open_unverified",
                        "Windows 没有确认新文件夹已经打开。",
                        true);
                return new DesktopFolderCreateResult(true, true, "createdAndOpened", folderName, targetPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is WindowsControlException or IOException)
            {
                throw new DesktopFolderWorkflowException(
                    "folder_created_open_failed",
                    $"文件夹“{folderName}”已经安全创建，但没有验证到资源管理器窗口。",
                    true,
                    folderCreated: true,
                    exception);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DesktopFolderWorkflowException)
        {
            throw;
        }
        catch (IOException exception)
        {
            if (Directory.Exists(targetPath) || File.Exists(targetPath))
                throw AlreadyExists(folderName, moved, exception);
            throw new DesktopFolderWorkflowException(
                "folder_create_failed",
                "桌面文件夹创建失败。",
                true,
                folderCreated: moved,
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new DesktopFolderWorkflowException(
                "folder_create_denied",
                "Windows 没有允许在桌面创建文件夹。",
                false,
                folderCreated: moved,
                exception);
        }
        finally
        {
            if (!moved && Directory.Exists(temporaryPath))
            {
                try { Directory.Delete(temporaryPath, recursive: false); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private string GetAvailableDefaultName()
    {
        for (var index = 1; index <= 1_000; index++)
        {
            var candidate = index == 1 ? "新建文件夹" : $"新建文件夹 ({index})";
            var path = Path.Combine(desktop, candidate);
            if (!Directory.Exists(path) && !File.Exists(path)) return candidate;
        }
        throw new DesktopFolderWorkflowException(
            "folder_name_unavailable",
            "桌面上同名的新建文件夹过多，请说出一个具体名称。" );
    }

    private static string ValidateFolderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.EndsWith(' ')) throw InvalidName();
        var folderName = value.Normalize(NormalizationForm.FormKC).Trim();
        if (folderName.Length > 120 ||
            folderName is "." or ".." ||
            folderName != Path.GetFileName(folderName) ||
            folderName.EndsWith(' ') ||
            folderName.EndsWith('.') ||
            folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            ReservedWindowsNames.Contains(folderName) ||
            ReservedWindowsNames.Contains(Path.GetFileNameWithoutExtension(folderName)))
            throw InvalidName();
        return folderName;
    }

    private static DesktopFolderWorkflowException InvalidName() => new(
        "folder_name_invalid",
        "文件夹名称无效，请使用普通名称，不要包含路径或 Windows 保留字符。" );

    private static DesktopFolderWorkflowException AlreadyExists(
        string folderName,
        bool folderCreated = false,
        Exception? exception = null) => new(
            "folder_already_exists",
            $"桌面上已经存在“{folderName}”，为避免覆盖没有创建新文件夹。",
            false,
            folderCreated,
            exception);
}
