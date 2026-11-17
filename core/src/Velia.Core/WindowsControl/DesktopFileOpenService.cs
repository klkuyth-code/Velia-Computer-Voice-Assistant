using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Velia.Core.WindowsControl;

internal sealed class DesktopFileOpenService(string? desktopPath = null) : IDesktopFileOpenService
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ForegroundTimeout = TimeSpan.FromSeconds(1.2);
    private const int ShowWindowRestore = 9;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".log", ".pdf", ".doc", ".docx",
        ".xls", ".xlsx", ".ppt", ".pptx", ".rtf", ".json", ".xml", ".html", ".htm",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".mp3", ".wav", ".flac",
        ".mp4", ".m4a", ".mkv", ".mov", ".webm", ".avi", ".zip", ".7z", ".rar", ".yaml",
        ".yml", ".ini", ".psd", ".ai",
    };
    private readonly string desktop = Path.GetFullPath(
        desktopPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

    public async Task<DesktopFileOpenResult> OpenAsync(
        DesktopFileOpenCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var item = ResolveItem(command.RequestedName);
        if (TryFindVisibleDocumentWindow(item.Name, out var existingWindow))
        {
            if (!await RestoreAndVerifyForegroundAsync(
                    existingWindow,
                    item.Name,
                    cancellationToken).ConfigureAwait(false))
                throw new WindowsControlException(
                    "desktop_file_foreground_unverified",
                    $"已找到桌面项目“{item.Name}”，但 Windows 没有确认它已经切到前台。",
                    true);
            return new DesktopFileOpenResult(true, "alreadyOpen", item.Name, item is DirectoryInfo);
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = item.FullName,
                WorkingDirectory = desktop,
                UseShellExecute = true,
            }) ?? throw new WindowsControlException("desktop_file_open_failed", "桌面文件未能打开。", true);
        }
        catch (WindowsControlException) { throw; }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new WindowsControlException("desktop_file_open_failed", $"无法打开桌面项目“{item.Name}”。", true, exception);
        }

        var openedWindow = await WaitForVisibleDocumentWindowAsync(
            item.Name,
            ObservationTimeout,
            cancellationToken).ConfigureAwait(false);
        if (openedWindow == 0)
        {
            throw new WindowsControlException(
                "desktop_file_open_unverified",
                $"已请求打开桌面项目“{item.Name}”，但没有验证到对应窗口。",
                true);
        }
        if (!await RestoreAndVerifyForegroundAsync(
                openedWindow,
                item.Name,
                cancellationToken).ConfigureAwait(false))
            throw new WindowsControlException(
                "desktop_file_foreground_unverified",
                $"已打开桌面项目“{item.Name}”，但 Windows 没有确认它已经切到前台。",
                true);
        return new DesktopFileOpenResult(true, item is DirectoryInfo ? "folderOpened" : "opened", item.Name, item is DirectoryInfo);
    }

    internal FileSystemInfo ResolveItem(string requestedName)
    {
        if (!Directory.Exists(desktop))
            throw new WindowsControlException("desktop_unavailable", "当前用户桌面不可用。", true);
        var directory = new DirectoryInfo(desktop);
        var folderRequested = requestedName.Contains("文件夹", StringComparison.OrdinalIgnoreCase) ||
            requestedName.Contains("目录", StringComparison.OrdinalIgnoreCase);
        var files = directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(file => AllowedExtensions.Contains(file.Extension) && IsVisible(file))
            .Cast<FileSystemInfo>();
        var folders = directory.EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
            .Where(IsVisible)
            .Cast<FileSystemInfo>();
        var items = (folderRequested ? folders : folders.Concat(files)).Take(751).ToArray();
        if (items.Length > 750)
            throw new WindowsControlException("desktop_file_limit", "桌面项目过多，无法安全定位目标。" );
        if (items.Length == 0)
            throw new WindowsControlException("desktop_file_not_found", "桌面上没有可打开的普通文件或文件夹。" );

        var query = NormalizeName(StripFolderSuffix(requestedName));
        if (query.Length == 0)
            throw new WindowsControlException("desktop_file_name_invalid", "没有识别到要打开的桌面项目名称。" );
        var ranked = items.Select(item => RankItem(query, item)).OrderByDescending(static item => item.Score).ToArray();
        var best = ranked[0];
        var nextScore = ranked.Length > 1 ? ranked[1].Score : 0;
        var exactOrContained = best.MatchKind is MatchKind.Exact or MatchKind.Contained;
        var uniqueFuzzy = best.LongestCommonSubstring >= 2 && best.Score >= 0.30 && best.Score - nextScore >= 0.10;
        if (!exactOrContained && !uniqueFuzzy)
        {
            var message = best.Score >= 0.22
                ? "桌面上有多个相近项目，无法安全确定要打开哪一个。"
                : $"桌面上没有找到与“{requestedName}”匹配的普通文件或文件夹。";
            throw new WindowsControlException("desktop_file_not_found", message);
        }
        return best.Item;
    }

    internal FileInfo ResolveFile(string requestedName)
    {
        if (!Directory.Exists(desktop))
            throw new WindowsControlException("desktop_unavailable", "当前用户桌面不可用。", true);
        var files = new DirectoryInfo(desktop).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(file => AllowedExtensions.Contains(file.Extension) && IsVisible(file))
            .Take(501)
            .ToArray();
        if (files.Length > 500)
            throw new WindowsControlException("desktop_file_limit", "桌面文件过多，无法安全定位目标。" );
        if (files.Length == 0)
            throw new WindowsControlException("desktop_file_not_found", "桌面上没有可打开的普通文件。" );

        var query = NormalizeName(requestedName);
        if (query.Length == 0)
            throw new WindowsControlException("desktop_file_name_invalid", "没有识别到要打开的桌面文件名。" );
        var ranked = files.Select(file => Rank(query, file)).OrderByDescending(static item => item.Score).ToArray();
        var best = ranked[0];
        var nextScore = ranked.Length > 1 ? ranked[1].Score : 0;
        var exactOrContained = best.MatchKind is MatchKind.Exact or MatchKind.Contained;
        var uniqueFuzzy = best.LongestCommonSubstring >= 2 && best.Score >= 0.30 && best.Score - nextScore >= 0.10;
        if (!exactOrContained && !uniqueFuzzy)
        {
            var message = best.Score >= 0.22
                ? "桌面上有多个相近文件，无法安全确定要打开哪一个。"
                : $"桌面上没有找到与“{requestedName}”匹配的普通文件。";
            throw new WindowsControlException("desktop_file_not_found", message);
        }
        return best.File;
    }

    private static RankedFile Rank(string query, FileInfo file)
    {
        var ranked = RankItem(query, file);
        return new RankedFile(file, ranked.Score, ranked.LongestCommonSubstring, ranked.MatchKind);
    }

    private static RankedItem RankItem(string query, FileSystemInfo item)
    {
        var candidate = NormalizeName(item is FileInfo
            ? Path.GetFileNameWithoutExtension(item.Name)
            : item.Name);
        if (candidate == query) return new RankedItem(item, 1, candidate.Length, MatchKind.Exact);
        if (candidate.Contains(query, StringComparison.Ordinal) || query.Contains(candidate, StringComparison.Ordinal))
        {
            var containment = (double)Math.Min(query.Length, candidate.Length) / Math.Max(query.Length, candidate.Length);
            return new RankedItem(item, 0.88 + containment * 0.1, Math.Min(query.Length, candidate.Length), MatchKind.Contained);
        }
        var commonSubstring = LongestCommonSubstring(query, candidate);
        var commonSequence = LongestCommonSubsequence(query, candidate);
        var score = 0.65 * commonSequence / Math.Max(query.Length, candidate.Length) +
            0.35 * commonSubstring / Math.Min(query.Length, candidate.Length);
        return new RankedItem(item, score, commonSubstring, MatchKind.Fuzzy);
    }

    private static bool IsVisible(FileSystemInfo item) =>
        (item.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0;

    private static string StripFolderSuffix(string value)
    {
        var trimmed = value.Trim();
        foreach (var suffix in new[] { "文件夹", "目录" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && trimmed.Length > suffix.Length)
                return trimmed[..^suffix.Length].TrimEnd();
        }
        return trimmed;
    }

    private static string NormalizeName(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        var extension = Path.GetExtension(normalized);
        if (AllowedExtensions.Contains(extension)) normalized = Path.GetFileNameWithoutExtension(normalized);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static int LongestCommonSubstring(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var longest = 0;
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            var current = new int[right.Length + 1];
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                if (left[leftIndex - 1] != right[rightIndex - 1]) continue;
                current[rightIndex] = previous[rightIndex - 1] + 1;
                longest = Math.Max(longest, current[rightIndex]);
            }
            previous = current;
        }
        return longest;
    }

    private static int LongestCommonSubsequence(string left, string right)
    {
        var previous = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            var current = new int[right.Length + 1];
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                current[rightIndex] = left[leftIndex - 1] == right[rightIndex - 1]
                    ? previous[rightIndex - 1] + 1
                    : Math.Max(previous[rightIndex], current[rightIndex - 1]);
            }
            previous = current;
        }
        return previous[^1];
    }

    private static bool TryFindVisibleDocumentWindow(string fileName, out nint handle)
    {
        var stem = NormalizeName(Path.GetFileNameWithoutExtension(fileName));
        handle = 0;
        if (stem.Length == 0) return false;
        nint found = 0;
        EnumWindows((window, parameter) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLength(window) <= 0 ||
                GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0)
                return true;
            var buffer = new char[Math.Min(1_024, GetWindowTextLength(window) + 1)];
            var length = GetWindowText(window, buffer, buffer.Length);
            if (length <= 0 || !NormalizeName(new string(buffer, 0, length)).Contains(stem, StringComparison.Ordinal))
                return true;
            found = window;
            return false;
        }, 0);
        handle = found;
        return handle != 0;
    }

    private static async Task<nint> WaitForVisibleDocumentWindowAsync(
        string fileName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryFindVisibleDocumentWindow(fileName, out var handle)) return handle;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return TryFindVisibleDocumentWindow(fileName, out var finalHandle) ? finalHandle : 0;
    }

    private static async Task<bool> RestoreAndVerifyForegroundAsync(
        nint window,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (window == 0 || !IsWindow(window) || !IsWindowVisible(window)) return false;
        if (IsIconic(window)) _ = ShowWindowAsync(window, ShowWindowRestore);
        TryActivateWindow(window);

        var expectedStem = NormalizeName(Path.GetFileNameWithoutExtension(fileName));
        var deadline = DateTimeOffset.UtcNow + ForegroundTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var foreground = GetForegroundWindow();
            if (foreground == window) return true;
            if (foreground != 0 && GetWindowTextLength(foreground) > 0)
            {
                var buffer = new char[Math.Min(1_024, GetWindowTextLength(foreground) + 1)];
                var length = GetWindowText(foreground, buffer, buffer.Length);
                if (length > 0 && NormalizeName(new string(buffer, 0, length))
                    .Contains(expectedStem, StringComparison.Ordinal))
                    return true;
            }
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            TryActivateWindow(window);
        }
        return GetForegroundWindow() == window;
    }

    private static void TryActivateWindow(nint window)
    {
        var currentThread = GetCurrentThreadId();
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(window, out _);
        var attachedForeground = false;
        var attachedTarget = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);
            _ = BringWindowToTop(window);
            _ = SetActiveWindow(window);
            _ = SetForegroundWindow(window);
        }
        finally
        {
            if (attachedTarget) _ = AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) _ = AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private enum MatchKind { Fuzzy, Contained, Exact }
    private sealed record RankedFile(FileInfo File, double Score, int LongestCommonSubstring, MatchKind MatchKind);
    private sealed record RankedItem(FileSystemInfo Item, double Score, int LongestCommonSubstring, MatchKind MatchKind);
    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, [Out] char[] text, int maximumCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
}
