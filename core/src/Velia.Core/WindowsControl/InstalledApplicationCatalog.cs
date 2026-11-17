using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Velia.Core.WindowsControl;

internal enum InstalledApplicationResolutionState
{
    Found,
    NotFound,
    Ambiguous,
}

internal sealed record InstalledApplicationEntry(
    string Id,
    string DisplayName,
    string LaunchTarget,
    IReadOnlySet<string> ProcessNames,
    IReadOnlySet<string> Aliases);

internal sealed record InstalledApplicationResolution(
    InstalledApplicationResolutionState State,
    InstalledApplicationEntry? Entry = null,
    IReadOnlyList<string>? Candidates = null);

internal interface IInstalledApplicationCatalog
{
    void WarmUp() { }
    InstalledApplicationResolution Resolve(string spokenName);
    bool TryGetById(string id, out InstalledApplicationEntry entry);
    IReadOnlyList<string> GetHotwords(int maximumCount = 200);
}

/// <summary>
/// Builds a bounded, read-only catalogue from Windows' supported application
/// registration surfaces. Paths never leave Core; callers receive only an
/// opaque id and a display name selected from the user's own machine.
/// </summary>
internal sealed class WindowsInstalledApplicationCatalog(
    TimeProvider? timeProvider = null,
    Func<IReadOnlyList<InstalledApplicationEntry>>? entryProvider = null,
    bool validateSuppliedEntries = true) : IInstalledApplicationCatalog
{
    internal const string DynamicIdPrefix = "installed:";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private const int MaximumIndexedApplications = 2_000;
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Func<IReadOnlyList<InstalledApplicationEntry>>? suppliedEntries = entryProvider;
    private readonly bool validateEntries = entryProvider is null || validateSuppliedEntries;
    private CatalogSnapshot? snapshot;

    public void WarmUp() => _ = GetSnapshot();

    public InstalledApplicationResolution Resolve(string spokenName)
    {
        var query = NormalizeName(spokenName);
        if (query.Length == 0)
            return new InstalledApplicationResolution(InstalledApplicationResolutionState.NotFound);

        var current = GetSnapshot();
        var ranked = current.Entries
            .Select(entry => new RankedApplication(entry, Score(query, entry)))
            .Where(static candidate => candidate.Score >= 0.46)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Entry.DisplayName.Length)
            .Take(5)
            .ToArray();
        if (ranked.Length == 0)
            return new InstalledApplicationResolution(InstalledApplicationResolutionState.NotFound);

        var best = ranked[0];
        var runnerUp = ranked.Length > 1 ? ranked[1].Score : 0;
        var uniquelyResolved = best.Score >= 0.94 ||
            (best.Score >= 0.62 && best.Score - runnerUp >= 0.12);
        if (!uniquelyResolved)
        {
            return new InstalledApplicationResolution(
                InstalledApplicationResolutionState.Ambiguous,
                Candidates: ranked.Select(static value => value.Entry.DisplayName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray());
        }
        return new InstalledApplicationResolution(InstalledApplicationResolutionState.Found, best.Entry);
    }

    public bool TryGetById(string id, out InstalledApplicationEntry entry)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith(DynamicIdPrefix, StringComparison.Ordinal))
        {
            entry = null!;
            return false;
        }
        var current = GetSnapshot();
        entry = current.Entries.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, id))!;
        return entry is not null;
    }

    public IReadOnlyList<string> GetHotwords(int maximumCount = 200) => GetSnapshot().Entries
        .SelectMany(static entry => entry.Aliases.Append(entry.DisplayName))
        .Where(static value => value.Length is >= 2 and <= 40)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(static value => value.Any(character => character >= '\u3400' && character <= '\u9fff'))
        .ThenBy(static value => value.Length)
        .Take(Math.Clamp(maximumCount, 1, 500))
        .ToArray();

    private CatalogSnapshot GetSnapshot()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (snapshot is not null && now - snapshot.CreatedAt < CacheLifetime)
                return snapshot;
            var entries = suppliedEntries?.Invoke() ?? Discover();
            snapshot = new CatalogSnapshot(
                entries.Where(entry => !validateEntries || IsValidEntry(entry))
                    .GroupBy(static entry => entry.Id, StringComparer.Ordinal)
                    .Select(static group => group.First())
                    .Take(MaximumIndexedApplications)
                    .ToArray(),
                now);
            return snapshot;
        }
    }

    private static List<InstalledApplicationEntry> Discover()
    {
        var entries = new List<InstalledApplicationEntry>();
        DiscoverAppPaths(entries);
        DiscoverUninstallRegistrations(entries);
        DiscoverShortcuts(entries);
        DiscoverRunningWindowApplications(entries);
        return entries;
    }

    private static void DiscoverUninstallRegistrations(List<InstalledApplicationEntry> entries)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false);
                if (uninstall is null) continue;
                foreach (var subKeyName in uninstall.GetSubKeyNames().Take(MaximumIndexedApplications))
                {
                    using var key = uninstall.OpenSubKey(subKeyName, false);
                    var displayName = (key?.GetValue("DisplayName") as string)?.Trim();
                    if (displayName is not { Length: >= 1 and <= 120 } || IsAdministrativeShortcut(displayName))
                        continue;
                    var executable = NormalizeExecutableValue(key?.GetValue("DisplayIcon") as string);
                    if (executable is null || !IsTrustedExecutable(executable)) continue;
                    var stem = Path.GetFileNameWithoutExtension(executable);
                    entries.Add(CreateEntry(
                        displayName,
                        executable,
                        [stem],
                        [displayName, stem, ReadDisplayName(executable, stem)]));
                }
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
    }

    private static void DiscoverAppPaths(List<InstalledApplicationEntry> entries)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var appPaths = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", false);
                if (appPaths is null) continue;
                foreach (var subKeyName in appPaths.GetSubKeyNames().Take(MaximumIndexedApplications))
                {
                    using var key = appPaths.OpenSubKey(subKeyName, false);
                    var path = NormalizeExecutableValue(key?.GetValue(null) as string);
                    if (path is null || !IsTrustedExecutable(path)) continue;
                    var executableStem = Path.GetFileNameWithoutExtension(path);
                    var displayName = ReadDisplayName(path, executableStem);
                    entries.Add(CreateEntry(
                        displayName,
                        path,
                        [executableStem],
                        [displayName, executableStem]));
                }
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A damaged or locked registration entry must not block the rest of the catalogue.
            }
        }
    }

    private static void DiscoverShortcuts(List<InstalledApplicationEntry> entries)
    {
        foreach (var root in TrustedShortcutRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var shortcut in EnumerateShortcutFiles(root, MaximumIndexedApplications))
                {
                    var path = Path.GetFullPath(shortcut);
                    if (!IsWithin(path, root)) continue;
                    var displayName = Path.GetFileNameWithoutExtension(path).Trim();
                    if (displayName.Length is < 1 or > 120 || IsAdministrativeShortcut(displayName)) continue;
                    var resolvedExecutable = TryResolveShortcutExecutable(path);
                    var processNames = resolvedExecutable is null
                        ? Array.Empty<string>()
                        : [Path.GetFileNameWithoutExtension(resolvedExecutable)];
                    var aliases = resolvedExecutable is null
                        ? new[] { displayName }
                        : new[]
                        {
                            displayName,
                            Path.GetFileNameWithoutExtension(resolvedExecutable),
                            ReadDisplayName(resolvedExecutable, displayName),
                        };
                    entries.Add(CreateEntry(displayName, path, processNames, aliases));
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
            }
        }
    }

    private static IEnumerable<string> EnumerateShortcutFiles(string root, int maximum)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var yielded = 0;
        var visitedDirectories = 0;
        while (pending.Count > 0 && yielded < maximum && visitedDirectories < 4_000)
        {
            var current = pending.Pop();
            visitedDirectories++;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(current, "*.lnk", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { continue; }
            foreach (var file in files)
            {
                yield return file;
                yielded++;
                if (yielded >= maximum) yield break;
            }
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { continue; }
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { }
            }
        }
    }

    private static string? TryResolveShortcutExecutable(string shortcutPath)
    {
        object? shellObject = null;
        object? shortcutObject = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            if (shellType is null) return null;
            shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null) return null;
            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;
            var target = NormalizeExecutableValue(Convert.ToString(shortcut.TargetPath));
            return target is not null && IsTrustedExecutable(target) ? target : null;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
        finally
        {
            ReleaseCom(shortcutObject);
            ReleaseCom(shellObject);
        }
    }

    private static void DiscoverRunningWindowApplications(List<InstalledApplicationEntry> entries)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle == 0 || process.MainModule?.FileName is not { } executable ||
                        !IsTrustedExecutable(executable))
                        continue;
                    var stem = Path.GetFileNameWithoutExtension(executable);
                    var displayName = ReadDisplayName(executable, stem);
                    if (displayName.Length is < 1 or > 120 || LooksLikeGenericExecutableName(displayName)) continue;
                    entries.Add(CreateEntry(displayName, executable, [stem], [displayName, stem]));
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
            }
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch (COMException) { }
    }

    private static InstalledApplicationEntry CreateEntry(
        string displayName,
        string launchTarget,
        IEnumerable<string> processNames,
        IEnumerable<string> aliases)
    {
        var normalizedPath = Path.GetFullPath(launchTarget);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath.ToUpperInvariant()));
        return new InstalledApplicationEntry(
            DynamicIdPrefix + Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant(),
            displayName.Trim(),
            normalizedPath,
            processNames.Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            aliases.Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static string ReadDisplayName(string executablePath, string fallback)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(executablePath);
            foreach (var candidate in new[] { version.ProductName, version.FileDescription })
            {
                var value = candidate?.Trim();
                if (value is { Length: >= 2 and <= 120 } && !LooksLikeGenericExecutableName(value))
                    return value;
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or UnauthorizedAccessException or Win32Exception)
        {
        }
        return fallback;
    }

    private static string? NormalizeExecutableValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        string candidate;
        if (expanded.StartsWith('"'))
        {
            var end = expanded.IndexOf('"', 1);
            candidate = end > 1 ? expanded[1..end] : expanded.Trim('"');
        }
        else
        {
            var comma = expanded.LastIndexOf(',');
            if (comma > 0 && int.TryParse(expanded[(comma + 1)..], out _)) expanded = expanded[..comma];
            candidate = expanded;
        }
        try { return Path.GetFullPath(candidate); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsValidEntry(InstalledApplicationEntry entry) =>
        entry.Id.StartsWith(DynamicIdPrefix, StringComparison.Ordinal) &&
        entry.DisplayName.Length is >= 1 and <= 120 &&
        IsTrustedLaunchTarget(entry.LaunchTarget);

    internal static bool IsTrustedLaunchTarget(string path)
    {
        if (!File.Exists(path)) return false;
        if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return IsTrustedExecutable(path);
        if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) return false;
        return TrustedShortcutRoots()
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Any(root => IsWithin(path, root));
    }

    private static bool IsTrustedExecutable(string path)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        return TrustedExecutableRoots()
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Any(root => IsWithin(path, root));
    }

    private static IEnumerable<string> TrustedExecutableRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    internal static IEnumerable<string> TrustedShortcutRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static bool IsWithin(string path, string root)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(path);
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static double Score(string query, InstalledApplicationEntry entry)
    {
        var best = 0d;
        foreach (var rawCandidate in entry.Aliases.Append(entry.DisplayName))
        {
            var candidate = NormalizeName(rawCandidate);
            if (candidate.Length == 0) continue;
            if (StringComparer.Ordinal.Equals(query, candidate)) return 1;
            if (candidate.Contains(query, StringComparison.Ordinal) || query.Contains(candidate, StringComparison.Ordinal))
            {
                var containment = (double)Math.Min(query.Length, candidate.Length) / Math.Max(query.Length, candidate.Length);
                best = Math.Max(best, 0.78 + 0.18 * containment);
                continue;
            }
            var substring = LongestCommonSubstring(query, candidate);
            var sequence = LongestCommonSubsequence(query, candidate);
            var fuzzy = 0.62 * sequence / Math.Max(query.Length, candidate.Length) +
                0.38 * substring / Math.Min(query.Length, candidate.Length);
            best = Math.Max(best, fuzzy);
        }
        return best;
    }

    internal static string NormalizeName(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        foreach (var suffix in new[] { "桌面版", "电脑版", "客户端", "应用程序", "软件", "应用", "程序" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && normalized.Length > suffix.Length)
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }
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

    private static bool IsAdministrativeShortcut(string displayName) =>
        displayName.Contains("卸载", StringComparison.OrdinalIgnoreCase) ||
        displayName.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) ||
        displayName.Contains("更新程序", StringComparison.OrdinalIgnoreCase) ||
        displayName.Contains("Update", StringComparison.OrdinalIgnoreCase) &&
        displayName.Contains("Installer", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeGenericExecutableName(string value) => value is
        "Application" or "Windows Application" or "Microsoft Windows Operating System";

    private sealed record CatalogSnapshot(IReadOnlyList<InstalledApplicationEntry> Entries, DateTimeOffset CreatedAt);
    private sealed record RankedApplication(InstalledApplicationEntry Entry, double Score);
}
