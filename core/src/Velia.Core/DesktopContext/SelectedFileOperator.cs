using Velia.Protocol;

namespace Velia.Core.DesktopContext;

internal sealed record SelectedFileOperationRequest(
    string Action,
    IReadOnlyList<string> SelectedPaths,
    string? NewName);

internal sealed record SelectedFileOperationObservation(
    IReadOnlyList<string> ResultPaths,
    string? DestinationFolderPath = null);

internal interface ISelectedFileOperator
{
    Task<SelectedFileOperationObservation> ExecuteAsync(
        SelectedFileOperationRequest request,
        CancellationToken cancellationToken);
}

internal sealed class WindowsSelectedFileOperator : ISelectedFileOperator
{
    private const int MaximumSourceFiles = 31;
    private const long MaximumTotalBytes = 512L * 1024 * 1024;

    public Task<SelectedFileOperationObservation> ExecuteAsync(
        SelectedFileOperationRequest request,
        CancellationToken cancellationToken) =>
        Task.Run(() => Execute(request, cancellationToken), cancellationToken);

    private static SelectedFileOperationObservation Execute(
        SelectedFileOperationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = NormalizeAndValidateSelectedPaths(request.SelectedPaths);
        return request.Action switch
        {
            DesktopContextFileOperations.CopyToSelectedFolder =>
                ExecuteTransfer(selected, copy: true, request.NewName, cancellationToken),
            DesktopContextFileOperations.MoveToSelectedFolder =>
                ExecuteTransfer(selected, copy: false, request.NewName, cancellationToken),
            DesktopContextFileOperations.RenameSelectedFile =>
                ExecuteRename(selected, request.NewName, cancellationToken),
            _ => throw new DesktopContextException("invalid_file_operation", "选中文件操作不受支持。"),
        };
    }

    private static SelectedFileOperationObservation ExecuteTransfer(
        string[] selected,
        bool copy,
        string? newName,
        CancellationToken cancellationToken)
    {
        if (newName is not null)
            throw new DesktopContextException("invalid_file_operation", "复制或移动操作不能包含新文件名。" );
        var directories = selected.Where(Directory.Exists).ToArray();
        var files = selected.Where(File.Exists).ToArray();
        if (directories.Length != 1 || files.Length is < 1 or > MaximumSourceFiles ||
            directories.Length + files.Length != selected.Length)
        {
            throw new DesktopContextException(
                "invalid_file_selection",
                "复制或移动需要同时选中一个目标文件夹和至少一个普通文件。" );
        }

        var destination = directories[0];
        var pairs = CreateTransferPlan(files, destination, requireSameVolume: !copy);
        return copy
            ? CopyFiles(pairs, destination, cancellationToken)
            : MoveFiles(pairs, destination, cancellationToken);
    }

    private static SelectedFileOperationObservation ExecuteRename(
        string[] selected,
        string? requestedName,
        CancellationToken cancellationToken)
    {
        if (selected.Length != 1 || !File.Exists(selected[0]))
            throw new DesktopContextException("invalid_file_selection", "重命名需要只选中一个普通文件。" );
        var source = selected[0];
        var name = NormalizeNewFileName(requestedName, Path.GetExtension(source));
        var directory = Path.GetDirectoryName(source)
            ?? throw new DesktopContextException("file_operation_rejected", "无法确定选中文件所在文件夹。" );
        var target = Path.GetFullPath(Path.Combine(directory, name));
        EnsureDirectChild(target, directory);
        if (StringComparer.OrdinalIgnoreCase.Equals(source, target))
            throw new DesktopContextException("file_name_unchanged", "新文件名与原文件名相同。" );
        EnsureTargetAvailable(target);

        var length = ReadFileLength(source);
        var moved = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(source, target);
            moved = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(target) || File.Exists(source) || new FileInfo(target).Length != length)
                throw new DesktopContextException("file_verification_failed", "重命名后的磁盘状态未通过验证。" );
            return new SelectedFileOperationObservation([target]);
        }
        catch (Exception exception) when (exception is not DesktopContextException)
        {
            if (moved && !TryRollbackMoves([new FileOperationPair(source, target, length)]))
                throw new DesktopContextException("file_operation_partial", "重命名失败且未能自动恢复，请检查测试文件。" );
            throw ToFileOperationException(exception);
        }
        catch
        {
            if (moved && !TryRollbackMoves([new FileOperationPair(source, target, length)]))
                throw new DesktopContextException("file_operation_partial", "重命名验证失败且未能自动恢复，请检查测试文件。" );
            throw;
        }
    }

    private static SelectedFileOperationObservation CopyFiles(
        List<FileOperationPair> pairs,
        string destination,
        CancellationToken cancellationToken)
    {
        var completed = new List<FileOperationPair>();
        try
        {
            foreach (var pair in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(pair.Source, pair.Target, overwrite: false);
                completed.Add(pair);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (pairs.Any(pair => !File.Exists(pair.Source) || !File.Exists(pair.Target) ||
                    new FileInfo(pair.Target).Length != pair.Length))
            {
                throw new DesktopContextException("file_verification_failed", "复制后的磁盘状态未通过验证。" );
            }
            return new SelectedFileOperationObservation(pairs.Select(static pair => pair.Target).ToArray(), destination);
        }
        catch (Exception exception)
        {
            if (!TryRollbackCopies(completed))
                throw new DesktopContextException("file_operation_partial", "复制失败且未能清理全部新文件，请检查测试文件夹。" );
            if (exception is DesktopContextException) throw;
            throw ToFileOperationException(exception);
        }
    }

    private static SelectedFileOperationObservation MoveFiles(
        List<FileOperationPair> pairs,
        string destination,
        CancellationToken cancellationToken)
    {
        var completed = new List<FileOperationPair>();
        try
        {
            foreach (var pair in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(pair.Source, pair.Target);
                completed.Add(pair);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (pairs.Any(pair => File.Exists(pair.Source) || !File.Exists(pair.Target) ||
                    new FileInfo(pair.Target).Length != pair.Length))
            {
                throw new DesktopContextException("file_verification_failed", "移动后的磁盘状态未通过验证。" );
            }
            return new SelectedFileOperationObservation(pairs.Select(static pair => pair.Target).ToArray(), destination);
        }
        catch (Exception exception)
        {
            if (!TryRollbackMoves(completed))
                throw new DesktopContextException("file_operation_partial", "移动失败且未能恢复全部文件，请检查测试文件夹。" );
            if (exception is DesktopContextException) throw;
            throw ToFileOperationException(exception);
        }
    }

    private static List<FileOperationPair> CreateTransferPlan(
        string[] files,
        string destination,
        bool requireSameVolume)
    {
        long totalBytes = 0;
        var pairs = new List<FileOperationPair>(files.Length);
        foreach (var source in files)
        {
            var length = ReadFileLength(source);
            try { totalBytes = checked(totalBytes + length); }
            catch (OverflowException) { throw FileSelectionTooLarge(); }
            if (totalBytes > MaximumTotalBytes) throw FileSelectionTooLarge();
            var target = Path.GetFullPath(Path.Combine(destination, Path.GetFileName(source)));
            EnsureDirectChild(target, destination);
            if (requireSameVolume && !StringComparer.OrdinalIgnoreCase.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target)))
                throw new DesktopContextException("cross_volume_move_rejected", "当前版本只支持同一磁盘内移动选中文件。" );
            EnsureTargetAvailable(target);
            pairs.Add(new FileOperationPair(source, target, length));
        }
        if (pairs.Select(static pair => pair.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pairs.Count)
            throw new DesktopContextException("destination_conflict", "多个选中文件会产生相同的目标文件名。" );
        return pairs;
    }

    private static long ReadFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ToFileOperationException(exception);
        }
    }

    private static string[] NormalizeAndValidateSelectedPaths(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count is < 1 or > 32)
            throw new DesktopContextException("invalid_file_selection", "选中文件数量无效。" );
        var normalized = paths.Select(NormalizeAndValidatePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Length != paths.Count)
            throw new DesktopContextException("invalid_file_selection", "选中文件列表包含重复项目。" );
        return normalized;
    }

    internal static string NormalizeAndValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new DesktopContextException("invalid_file_selection", "选中文件路径无效。" );
        var fullPath = Path.GetFullPath(path);
        EnsureUserDocumentPath(fullPath);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            throw new DesktopContextException("selected_file_missing", "选中的文件或文件夹已经不存在，请重新选择。", true);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new DesktopContextException("reparse_point_rejected", "当前版本不操作快捷链接或重解析文件。" );
        return fullPath;
    }

    private static void EnsureUserDocumentPath(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile) || !IsWithin(path, profile))
            throw new DesktopContextException("file_location_rejected", "当前版本只操作当前用户目录内明确选中的文件。" );
        string[] blockedRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(profile, ".ssh"),
            Path.Combine(profile, ".gnupg"),
            Path.Combine(profile, ".codex"),
        ];
        if (blockedRoots.Where(static root => !string.IsNullOrWhiteSpace(root)).Any(root => IsWithin(path, root)))
            throw new DesktopContextException("file_location_rejected", "当前版本不操作应用数据或安全配置目录。" );
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return StringComparer.OrdinalIgnoreCase.Equals(normalizedPath, normalizedRoot) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeNewFileName(string? requestedName, string originalExtension)
    {
        var name = requestedName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name is "." or ".." ||
            name.EndsWith(' ') || name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !StringComparer.Ordinal.Equals(Path.GetFileName(name), name))
        {
            throw new DesktopContextException("invalid_file_name", "新文件名无效，请只提供文件名，不要包含路径。" );
        }
        if (string.IsNullOrEmpty(Path.GetExtension(name)) && !string.IsNullOrEmpty(originalExtension))
            name += originalExtension;
        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        // Speech recognition commonly transcribes individually pronounced letters as
        // "C O N" or "C　O　N". Treat whitespace-separated reserved device names as
        // reserved too, instead of silently creating a different legal-looking name.
        var reservedNameProbe = string.Concat(stem.Where(static character => !char.IsWhiteSpace(character)));
        if (reservedNameProbe is "CON" or "PRN" or "AUX" or "NUL" ||
            (reservedNameProbe.Length == 4 &&
             (reservedNameProbe.StartsWith("COM", StringComparison.Ordinal) || reservedNameProbe.StartsWith("LPT", StringComparison.Ordinal)) &&
             reservedNameProbe[3] is >= '1' and <= '9'))
        {
            throw new DesktopContextException("invalid_file_name", "新文件名是 Windows 保留名称。" );
        }
        return name;
    }

    private static void EnsureDirectChild(string target, string directory)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(target), Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)))
            throw new DesktopContextException("file_location_rejected", "目标文件必须直接位于已选择的文件夹内。" );
    }

    private static void EnsureTargetAvailable(string target)
    {
        if (File.Exists(target) || Directory.Exists(target))
            throw new DesktopContextException("destination_conflict", "目标文件夹中已经存在同名项目，未执行覆盖。" );
    }

    private static bool TryRollbackCopies(IEnumerable<FileOperationPair> pairs)
    {
        var success = true;
        foreach (var pair in pairs.Reverse())
        {
            try
            {
                if (File.Exists(pair.Target))
                {
                    if (new FileInfo(pair.Target).Length != pair.Length) success = false;
                    else File.Delete(pair.Target);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { success = false; }
            if (File.Exists(pair.Target)) success = false;
        }
        return success;
    }

    private static bool TryRollbackMoves(IEnumerable<FileOperationPair> pairs)
    {
        var success = true;
        foreach (var pair in pairs.Reverse())
        {
            try
            {
                if (File.Exists(pair.Target) && !File.Exists(pair.Source))
                {
                    if (new FileInfo(pair.Target).Length != pair.Length) success = false;
                    else File.Move(pair.Target, pair.Source);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { success = false; }
            if (!File.Exists(pair.Source) || File.Exists(pair.Target)) success = false;
        }
        return success;
    }

    private static DesktopContextException ToFileOperationException(Exception exception) => exception switch
    {
        OperationCanceledException => new DesktopContextException("file_operation_cancelled", "文件操作已取消并恢复。", true),
        UnauthorizedAccessException => new DesktopContextException("file_access_denied", "Windows 拒绝访问选中的文件或目标文件夹。" ),
        ArgumentException or NotSupportedException or PathTooLongException =>
            new DesktopContextException("invalid_file_selection", "选中的文件或文件夹名称无效。" ),
        IOException => new DesktopContextException("file_operation_failed", "Windows 文件操作失败，未覆盖任何同名文件。", true),
        _ => new DesktopContextException("file_operation_failed", "文件操作未完成。", true),
    };

    private static DesktopContextException FileSelectionTooLarge() =>
        new("file_selection_too_large", "单次文件操作总大小不能超过 512 MB。" );

    private sealed record FileOperationPair(string Source, string Target, long Length);
}
