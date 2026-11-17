using System.Text;
using Velia.Protocol;

namespace Velia.Core.DesktopContext;

internal sealed record DesktopNamedFileMoveCommand(
    string SourceName,
    string DestinationFolderName);

internal sealed record DesktopNamedFileMoveResult(
    bool Verified,
    string SourceName,
    string DestinationFolderName,
    string ResultName);

internal interface IDesktopNamedFileMoveService
{
    Task<DesktopNamedFileMoveResult> ExecuteAsync(
        DesktopNamedFileMoveCommand command,
        CancellationToken cancellationToken);
}

internal static class DesktopNamedFileMoveIntentDetector
{
    private static readonly string[] TransferMarkers =
    [
        "移动到", "移动进", "移到", "移入", "移进", "挪到", "放到", "放进", "进入",
    ];

    private static readonly string[] SourcePrefixes =
    [
        "请帮我把", "请帮我将", "麻烦帮我把", "麻烦帮我将", "帮我把", "帮我将",
        "请把", "请将", "麻烦把", "麻烦将", "把", "将", "让", "请", "帮我", "麻烦",
        "桌面上的", "桌面上面的", "桌面的", "桌面上", "桌面",
    ];

    private static readonly string[] DestinationPrefixes =
    [
        "桌面上的", "桌面上面的", "桌面的", "桌面上", "桌面",
    ];

    private static readonly string[] DestinationSuffixes =
    [
        "文件夹里面", "文件夹里边", "文件夹里", "文件夹内", "文件夹中", "文件夹",
        "目录里面", "目录里边", "目录里", "目录内", "目录中", "目录",
        "里面", "里边", "里", "内", "中",
    ];

    private static readonly string[] DeniedTerms = ["不要", "别", "不用", "无需", "禁止", "取消"];
    private static readonly string[] InstructionalPrefixes = ["怎么", "如何", "为什么", "能不能", "是否"];

    public static bool TryParseExplicitCommand(
        string request,
        out DesktopNamedFileMoveCommand command)
    {
        command = null!;
        if (string.IsNullOrWhiteSpace(request)) return false;
        var normalized = request.Normalize(NormalizationForm.FormKC)
            .Trim()
            .TrimEnd('。', '！', '!', '？', '?', '，', ',', '；', ';');
        if (!normalized.Contains("桌面", StringComparison.OrdinalIgnoreCase) ||
            !normalized.Contains("文件夹", StringComparison.OrdinalIgnoreCase) ||
            DeniedTerms.Any(normalized.Contains) ||
            InstructionalPrefixes.Any(normalized.StartsWith))
            return false;

        var marker = FindTransferMarker(normalized);
        if (marker.Index < 0) return false;
        var source = StripPrefixes(normalized[..marker.Index].Trim(), SourcePrefixes);
        var destination = StripPrefixes(
            normalized[(marker.Index + marker.Marker.Length)..].Trim(),
            DestinationPrefixes);
        destination = StripSuffixes(destination, DestinationSuffixes);
        source = TrimQuotedPhrase(source);
        destination = TrimQuotedPhrase(destination);
        if (!IsSafeSpokenName(source) || !IsSafeSpokenName(destination)) return false;

        command = new DesktopNamedFileMoveCommand(source, destination);
        return true;
    }

    public static string GetCanonicalKey(DesktopNamedFileMoveCommand command) =>
        $"desktop-file-move:{NormalizeKey(command.SourceName)}:{NormalizeKey(command.DestinationFolderName)}";

    private static (int Index, string Marker) FindTransferMarker(string value)
    {
        var bestIndex = -1;
        var bestMarker = string.Empty;
        foreach (var marker in TransferMarkers)
        {
            var index = value.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0 || bestIndex >= 0 && index > bestIndex) continue;
            if (index == bestIndex && marker.Length <= bestMarker.Length) continue;
            bestIndex = index;
            bestMarker = marker;
        }
        return (bestIndex, bestMarker);
    }

    private static string StripPrefixes(string value, IReadOnlyList<string> prefixes)
    {
        var result = value;
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var prefix in prefixes)
            {
                if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                result = result[prefix.Length..].TrimStart();
                changed = true;
                break;
            }
        }
        return result;
    }

    private static string StripSuffixes(string value, IReadOnlyList<string> suffixes)
    {
        var result = value.TrimEnd();
        foreach (var suffix in suffixes)
        {
            if (!result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            return result[..^suffix.Length].TrimEnd();
        }
        return result;
    }

    private static string TrimQuotedPhrase(string value) =>
        value.Trim().Trim('“', '”', '‘', '’', '\'', '"', ' ');

    private static bool IsSafeSpokenName(string value) =>
        value.Length is >= 1 and <= 240 &&
        value is not "." and not ".." &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !value.Contains(Path.DirectorySeparatorChar) &&
        !value.Contains(Path.AltDirectorySeparatorChar);

    private static string NormalizeKey(string value) =>
        string.Concat(value.Normalize(NormalizationForm.FormKC).Where(static character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();
}

internal sealed class DesktopNamedFileMoveService(
    string? desktopPath = null,
    ISelectedFileOperator? selectedFileOperator = null) : IDesktopNamedFileMoveService
{
    private static readonly string[] SpokenTypeSuffixes =
    [
        "记事本文档", "记事本", "TXT文档", "TXT文件", "文本文件",
        "Word文档", "Wod文档", "Excel表格", "Excel文档", "Ex表格",
        "文件夹", "文件", "文档", "目录",
    ];

    private readonly string desktop = Path.GetFullPath(
        desktopPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    private readonly ISelectedFileOperator fileOperator = selectedFileOperator ?? new WindowsSelectedFileOperator();

    public async Task<DesktopNamedFileMoveResult> ExecuteAsync(
        DesktopNamedFileMoveCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = ResolveDestinationFolder(command.DestinationFolderName);
        var source = ResolveSource(command.SourceName, destination);
        if (StringComparer.OrdinalIgnoreCase.Equals(source.Path, destination))
            throw new DesktopContextException("invalid_file_operation", "源项目和目标文件夹不能相同。" );
        if (source.IsDirectory && IsWithin(destination, source.Path))
            throw new DesktopContextException("invalid_file_operation", "不能把文件夹移动到自己的内部。" );

        var target = Path.Combine(destination, Path.GetFileName(source.Path));
        EnsureUserDesktopPath(target);
        if (File.Exists(target) || Directory.Exists(target))
            throw new DesktopContextException(
                "destination_conflict",
                $"目标文件夹中已经存在“{Path.GetFileName(source.Path)}”，未执行覆盖。" );

        if (!source.IsDirectory)
        {
            var observation = await fileOperator.ExecuteAsync(
                new SelectedFileOperationRequest(
                    DesktopContextFileOperations.MoveToSelectedFolder,
                    [source.Path, destination],
                    null),
                cancellationToken).ConfigureAwait(false);
            if (observation.ResultPaths.Count != 1 ||
                !string.Equals(observation.DestinationFolderPath, destination, StringComparison.OrdinalIgnoreCase))
                throw new DesktopContextException("file_verification_failed", "移动后的磁盘状态未通过验证。", true);
        }
        else
        {
            MoveDirectory(source.Path, target, cancellationToken);
        }

        return new DesktopNamedFileMoveResult(
            true,
            Path.GetFileName(source.Path),
            Path.GetFileName(destination),
            Path.GetFileName(target));
    }

    private DesktopEntry ResolveSource(string requestedName, string destination)
    {
        // Existing items in the destination folder are not source candidates;
        // otherwise same-name protection would make a valid source appear
        // ambiguous before the collision check can report the real reason.
        var entries = EnumerateDesktopEntries()
            .Where(entry => !IsWithin(entry.Path, destination))
            .ToArray();
        var matches = FindMatches(entries, requestedName, allowFiles: true, allowDirectories: true);
        return matches.Length switch
        {
            1 => matches[0],
            > 1 => throw AmbiguousSource(requestedName, matches),
            _ => throw MissingSource(requestedName),
        };
    }

    private string ResolveDestinationFolder(string requestedName)
    {
        var directories = EnumerateDesktopEntries()
            .Where(static entry => entry.IsDirectory)
            .ToArray();
        var matches = FindMatches(directories, requestedName, allowFiles: false, allowDirectories: true);
        return matches.Length switch
        {
            1 => matches[0].Path,
            > 1 => throw new DesktopContextException(
                "destination_ambiguous",
                $"桌面下有多个名为“{requestedName}”的目标文件夹，未执行移动。" ),
            _ => throw new DesktopContextException(
                "destination_not_found",
                $"桌面下没有找到文件夹“{requestedName}”。",
                true),
        };
    }

    private static DesktopEntry[] FindMatches(
        IReadOnlyList<DesktopEntry> entries,
        string requestedName,
        bool allowFiles,
        bool allowDirectories)
    {
        var normalizedRequested = NormalizeSpokenName(requestedName);
        var candidates = entries.Where(entry => (entry.IsDirectory ? allowDirectories : allowFiles)).ToArray();
        var exact = candidates.Where(entry =>
            string.Equals(Path.GetFileName(entry.Path), requestedName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length > 0) return exact;

        var normalized = candidates.Where(entry =>
            NormalizeSpokenName(Path.GetFileName(entry.Path)) == normalizedRequested ||
            !entry.IsDirectory && NormalizeSpokenName(Path.GetFileNameWithoutExtension(entry.Path)) == normalizedRequested ||
            entry.IsDirectory && NormalizeSpokenName(Path.GetFileName(entry.Path)) == NormalizeSpokenName(requestedName, stripFolderSuffix: true) ||
            // In natural speech users sometimes call a folder a “文件” or
            // “文档” (for example “把语音移动测试文件移到归档资料”).
            // Treat those generic type words as removable for directory
            // candidates too; the final unique-match check still prevents
            // guessing when multiple desktop items share the same stem.
            entry.IsDirectory && NormalizeSpokenName(Path.GetFileName(entry.Path)) == NormalizeSpokenName(requestedName, stripTypeSuffix: true) ||
            !entry.IsDirectory && NormalizeSpokenName(Path.GetFileNameWithoutExtension(entry.Path)) == NormalizeSpokenName(requestedName, stripTypeSuffix: true))
            .ToArray();
        if (normalized.Length > 0) return normalized;

        return FindConservativeFuzzyMatches(candidates, requestedName);
    }

    private static DesktopEntry[] FindConservativeFuzzyMatches(
        IReadOnlyList<DesktopEntry> candidates,
        string requestedName)
    {
        var requestedStem = NormalizeSpokenName(requestedName, stripTypeSuffix: true);
        if (requestedStem.Length < 4) return [];

        var matches = candidates
            .Select(entry =>
            {
                var candidateStem = NormalizeSpokenName(
                    entry.IsDirectory
                        ? Path.GetFileName(entry.Path)
                        : Path.GetFileNameWithoutExtension(entry.Path),
                    stripTypeSuffix: true);
                var distance = LevenshteinDistance(requestedStem, candidateStem);
                return new FuzzyMatch(entry, candidateStem, distance);
            })
            .Where(match => IsConservativeFuzzyMatch(
                requestedStem,
                match.CandidateStem,
                match.Distance))
            .ToArray();
        if (matches.Length == 0) return [];

        // Prefer the objectively closest name. If several candidates are tied,
        // return all of them so the caller reports ambiguity instead of moving
        // a guessed item.
        var bestDistance = matches.Min(static match => match.Distance);
        return matches
            .Where(match => match.Distance == bestDistance)
            .Select(static match => match.Entry)
            .ToArray();
    }

    private static bool IsConservativeFuzzyMatch(
        string requested,
        string candidate,
        int distance)
    {
        if (candidate.Length < 4) return false;
        var lengthDifference = Math.Abs(requested.Length - candidate.Length);
        if (lengthDifference > 2) return false;

        // One wrong, missing, or extra character is accepted for sufficiently
        // descriptive names. Two edits are accepted only when speech omitted
        // two characters and the rest of the name remains in order, such as
        // “语音测试” -> “语音移动测试”.
        if (distance <= 1) return true;
        return distance == 2 &&
            lengthDifference == 2 &&
            (IsSubsequence(requested, candidate) || IsSubsequence(candidate, requested));
    }

    private static bool IsSubsequence(string shorter, string longer)
    {
        if (shorter.Length > longer.Length) return false;
        var index = 0;
        foreach (var character in longer)
            if (index < shorter.Length && shorter[index] == character) index++;
        return index == shorter.Length;
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++) previous[column] = column;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = previous[column - 1] +
                    (left[row - 1] == right[column - 1] ? 0 : 1);
                current[column] = Math.Min(
                    Math.Min(previous[column] + 1, current[column - 1] + 1),
                    substitution);
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private static void MoveDirectory(string source, string target, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(source, target);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(source) || !Directory.Exists(target))
                throw new DesktopContextException("file_verification_failed", "文件夹移动后的磁盘状态未通过验证。", true);
        }
        catch (Exception exception) when (exception is not DesktopContextException)
        {
            if (Directory.Exists(target) && !Directory.Exists(source))
            {
                try { Directory.Move(target, source); }
                catch { throw new DesktopContextException("file_operation_partial", "文件夹移动失败且未能自动恢复。" ); }
            }
            throw exception switch
            {
                UnauthorizedAccessException => new DesktopContextException("file_access_denied", "Windows 拒绝访问源文件夹或目标文件夹。" ),
                IOException => new DesktopContextException("file_operation_failed", "Windows 文件夹操作失败，未覆盖同名项目。", true),
                _ => new DesktopContextException("file_operation_failed", "文件夹移动未完成。", true),
            };
        }
        catch
        {
            if (Directory.Exists(target) && !Directory.Exists(source))
            {
                try { Directory.Move(target, source); }
                catch { throw new DesktopContextException("file_operation_partial", "文件夹验证失败且未能自动恢复。" ); }
            }
            throw;
        }
    }

    private DesktopEntry[] EnumerateDesktopEntries()
    {
        var entries = new List<DesktopEntry>();
        var pending = new Stack<string>();
        pending.Push(desktop);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] children;
            try { children = Directory.GetFileSystemEntries(directory); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                entries.Add(new DesktopEntry(child, isDirectory));
                if (isDirectory) pending.Push(child);
            }
        }
        return entries.ToArray();
    }

    private static string NormalizeSpokenName(
        string value,
        bool stripFolderSuffix = false,
        bool stripTypeSuffix = false)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        if (stripFolderSuffix) normalized = StripSuffix(normalized, ["文件夹", "目录"]);
        if (stripTypeSuffix) normalized = StripSuffix(normalized, SpokenTypeSuffixes);
        normalized = string.Concat(normalized.Where(static character =>
            !char.IsWhiteSpace(character) &&
            character is not ('"' or '\'' or '“' or '”' or '‘' or '’' or
                '。' or '，' or ',' or '.' or '、')));
        return normalized.ToUpperInvariant();
    }

    private static string StripSuffix(string value, IReadOnlyList<string> suffixes)
    {
        foreach (var suffix in suffixes.OrderByDescending(static item => item.Length))
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return value[..^suffix.Length].TrimEnd();
        return value;
    }

    private void EnsureUserDesktopPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!IsWithin(full, desktop))
            throw new DesktopContextException("file_location_rejected", "当前版本只操作桌面及其子文件夹。" );
        if ((File.GetAttributes(Path.GetDirectoryName(full) ?? desktop) & FileAttributes.ReparsePoint) != 0)
            throw new DesktopContextException("reparse_point_rejected", "当前版本不操作快捷链接或重解析文件夹。" );
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static DesktopContextException MissingSource(string name) =>
        new("source_file_not_found", $"桌面及其子文件夹中没有唯一找到“{name}”。", true);

    private static DesktopContextException AmbiguousSource(string name, IReadOnlyList<DesktopEntry> matches) =>
        new("source_file_ambiguous", $"找到多个符合“{name}”的项目：{string.Join("、", matches.Take(3).Select(static item => Path.GetFileName(item.Path)))}。请说出更完整的名称。" );

    private sealed record DesktopEntry(string Path, bool IsDirectory);
    private sealed record FuzzyMatch(DesktopEntry Entry, string CandidateStem, int Distance);
}
