using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Velia.Core.WindowsControl;

internal sealed class WindowsApplicationController(
    IInstalledApplicationCatalog? installedApplicationCatalog = null) : IWindowsApplicationController
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WindowStateTimeout = TimeSpan.FromSeconds(2);
    private static readonly Dictionary<string, ApplicationSpec> Applications = CreateApplications();
    private static readonly Dictionary<string, string> ResolvedTargets = new(StringComparer.Ordinal);
    private static readonly object ResolutionGate = new();
    private readonly IInstalledApplicationCatalog? installedApplications = installedApplicationCatalog;

    public async Task<WindowsControlResult> OpenAsync(string applicationId, CancellationToken cancellationToken)
    {
        var spec = GetSpecForOpen(applicationId);
        var existingWindows = GetVisibleWindows(spec);
        if (existingWindows.Count > 0)
        {
            var state = await RestoreAndActivateAsync(spec, existingWindows, cancellationToken).ConfigureAwait(false);
            return ApplicationResult(WindowsControlActions.OpenApplication, spec, state);
        }

        var launchTarget = ResolveLaunchTarget(spec);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = launchTarget,
                UseShellExecute = string.Equals(Path.GetExtension(launchTarget), ".lnk", StringComparison.OrdinalIgnoreCase),
                CreateNoWindow = false,
            };
            if (!startInfo.UseShellExecute)
                startInfo.WorkingDirectory = Path.GetDirectoryName(launchTarget)!;
            using var process = Process.Start(startInfo) ??
                throw new WindowsControlException("application_start_failed", $"{spec.DisplayName} 未能启动。", true);
        }
        catch (WindowsControlException) { throw; }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            ForgetResolvedTarget(spec.Id);
            throw new WindowsControlException("application_start_failed", $"无法启动 {spec.DisplayName}。", true, exception);
        }

        if (!await WaitUntilAsync(() => CountVisibleWindows(spec) > 0, ObservationTimeout, cancellationToken).ConfigureAwait(false))
            throw new WindowsControlException("application_start_unverified", $"已请求启动 {spec.DisplayName}，但没有验证到可见窗口。", true);

        var openedWindows = GetVisibleWindows(spec);
        var openedState = await RestoreAndActivateAsync(spec, openedWindows, cancellationToken).ConfigureAwait(false);
        return ApplicationResult(
            WindowsControlActions.OpenApplication,
            spec,
            openedState == "activated" ? "opened" : openedState);
    }

    public async Task<WindowsControlResult> CloseAsync(string applicationId, CancellationToken cancellationToken)
    {
        var spec = GetSpecForWindowOperation(applicationId);
        if (!spec.CanClose)
            throw new WindowsControlException("application_close_rejected", $"当前版本不关闭 {spec.DisplayName}。" );
        var windows = GetVisibleWindows(spec);
        if (windows.Count == 0)
            return ApplicationResult(WindowsControlActions.CloseApplication, spec, "notRunning");

        var requested = 0;
        foreach (var window in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PostMessage(window, WmClose, 0, 0)) requested++;
        }
        if (requested == 0)
            throw new WindowsControlException("application_close_failed", $"无法向 {spec.DisplayName} 发送正常关闭请求。", true);
        if (!await WaitUntilAsync(() => CountVisibleWindows(spec) == 0, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
        {
            throw new WindowsControlException(
                "application_close_incomplete",
                $"{spec.DisplayName} 仍有窗口，可能正在等待你保存内容；Velia 没有强制结束进程。" );
        }
        return ApplicationResult(WindowsControlActions.CloseApplication, spec, "closed");
    }

    public async Task<WindowsControlResult> MinimizeAsync(string applicationId, CancellationToken cancellationToken)
    {
        var spec = GetSpecForWindowOperation(applicationId);
        var windows = GetVisibleWindows(spec);
        if (windows.Count == 0)
            return ApplicationResult(WindowsControlActions.MinimizeApplication, spec, "notRunning");
        foreach (var window in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShowWindowAsync(window, SwMinimize);
        }
        if (!await WaitUntilAsync(
                () => GetVisibleWindows(spec) is { Count: > 0 } current && current.All(IsIconic),
                WindowStateTimeout,
                cancellationToken).ConfigureAwait(false))
            throw new WindowsControlException("application_minimize_unverified", $"没有确认 {spec.DisplayName} 已最小化。", true);
        return ApplicationResult(WindowsControlActions.MinimizeApplication, spec, "minimized");
    }

    public async Task<WindowsControlResult> RestoreAsync(string applicationId, CancellationToken cancellationToken)
    {
        var spec = GetSpecForWindowOperation(applicationId);
        var windows = GetVisibleWindows(spec);
        if (windows.Count == 0)
            return ApplicationResult(WindowsControlActions.RestoreApplication, spec, "notRunning");
        var state = await RestoreAndActivateAsync(spec, windows, cancellationToken).ConfigureAwait(false);
        return ApplicationResult(WindowsControlActions.RestoreApplication, spec, state);
    }

    public async Task<WindowsControlResult> MaximizeAsync(string applicationId, CancellationToken cancellationToken)
    {
        var spec = GetSpecForWindowOperation(applicationId);
        var windows = GetVisibleWindows(spec);
        if (windows.Count == 0)
            return ApplicationResult(WindowsControlActions.MaximizeApplication, spec, "notRunning");
        var target = ChoosePrimaryWindow(windows);
        ShowWindowAsync(target, SwMaximize);
        TryActivateWindow(target);
        if (!await WaitUntilAsync(
                () => IsWindowVisible(target) && IsZoomed(target),
                WindowStateTimeout,
                cancellationToken).ConfigureAwait(false))
            throw new WindowsControlException("application_maximize_unverified", $"没有确认 {spec.DisplayName} 已最大化。", true);
        return ApplicationResult(WindowsControlActions.MaximizeApplication, spec, "maximized");
    }

    private static async Task<string> RestoreAndActivateAsync(
        ApplicationSpec spec,
        IReadOnlyList<nint> windows,
        CancellationToken cancellationToken)
    {
        var target = ChoosePrimaryWindow(windows);
        var wasMinimized = IsIconic(target);
        ShowWindowAsync(target, wasMinimized ? SwRestore : SwShow);
        TryActivateWindow(target);

        var restored = await WaitUntilAsync(
            () => IsWindowVisible(target) && !IsIconic(target),
            WindowStateTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!restored)
            throw new WindowsControlException("application_restore_unverified", $"没有确认 {spec.DisplayName} 的窗口已恢复。", true);

        var foreground = await WaitUntilAsync(
            () => IsApplicationForeground(spec),
            TimeSpan.FromMilliseconds(900),
            cancellationToken).ConfigureAwait(false);
        return foreground
            ? wasMinimized ? "restored" : "activated"
            : "visibleInBackground";
    }

    private static nint ChoosePrimaryWindow(IReadOnlyList<nint> windows)
    {
        var foreground = GetForegroundWindow();
        if (foreground != 0 && windows.Contains(foreground)) return foreground;
        return windows.FirstOrDefault(window => !IsIconic(window), windows[0]);
    }

    private static void TryActivateWindow(nint window)
    {
        if (window == 0) return;
        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(window, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread &&
                                 AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread &&
                             AttachThreadInput(currentThread, targetThread, true);
        try
        {
            BringWindowToTop(window);
            SetWindowPos(window, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private ApplicationSpec GetSpecForOpen(string applicationId)
    {
        if (!OfficeApplicationResolver.TryGetFamily(applicationId, out var family))
            return GetConcreteSpec(applicationId);
        foreach (var concreteId in GetOrderedConcreteIds(family))
        {
            var spec = GetConcreteSpec(concreteId);
            if (TryResolveLaunchTarget(spec, out _)) return spec;
        }
        throw new WindowsControlException(
            "application_not_installed",
            $"未找到已安装的{family.DisplayName}。" );
    }

    private ApplicationSpec GetSpecForWindowOperation(string applicationId)
    {
        if (!OfficeApplicationResolver.TryGetFamily(applicationId, out var family))
            return GetConcreteSpec(applicationId);
        var specs = GetOrderedConcreteIds(family).Select(GetConcreteSpec).ToArray();
        return specs.FirstOrDefault(IsApplicationForeground) ??
               specs.FirstOrDefault(spec => CountVisibleWindows(spec) > 0) ??
               specs[0];
    }

    private static IReadOnlyList<string> GetOrderedConcreteIds(OfficeApplicationFamily family) =>
        OfficeApplicationResolver.OrderConcreteApplicationIds(
            family,
            OfficeApplicationResolver.ReadDefaultProgId(family.FileExtension));

    private ApplicationSpec GetConcreteSpec(string applicationId)
    {
        if (Applications.TryGetValue(applicationId, out var spec)) return spec;
        if (installedApplications?.TryGetById(applicationId, out var indexed) == true)
            return FromInstalledEntry(indexed);
        throw new WindowsControlException("application_not_allowlisted", "该应用不在 Velia 的本机应用目录中。" );
    }

    private static ApplicationSpec FromInstalledEntry(InstalledApplicationEntry entry)
    {
        var extension = Path.GetExtension(entry.LaunchTarget);
        var executableNames = string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
            ? new HashSet<string>([Path.GetFileName(entry.LaunchTarget)], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new ApplicationSpec(
            entry.Id,
            entry.DisplayName,
            entry.ProcessNames,
            executableNames,
            [entry.LaunchTarget],
            entry.Aliases.ToArray(),
            [entry.DisplayName],
            DirectLaunchTarget: entry.LaunchTarget,
            RequiredWindowTitleAliases: entry.Aliases.Append(entry.DisplayName).ToArray());
    }

    private static bool TryResolveLaunchTarget(ApplicationSpec spec, out string target)
    {
        try
        {
            target = ResolveLaunchTarget(spec);
            return true;
        }
        catch (WindowsControlException exception) when (exception.Code == "application_not_installed")
        {
            target = string.Empty;
            return false;
        }
    }

    private static string ResolveLaunchTarget(ApplicationSpec spec)
    {
        lock (ResolutionGate)
        {
            if (ResolvedTargets.TryGetValue(spec.Id, out var cached) && IsTrustedLaunchTarget(cached, spec))
                return cached;
        }

        var resolved = spec.DirectLaunchTarget is { } direct &&
                       WindowsInstalledApplicationCatalog.IsTrustedLaunchTarget(direct)
                ? direct
                : ResolveCandidatePaths(spec) ??
                       ResolveRunningProcessPath(spec) ??
                       ResolveAppPathRegistry(spec) ??
                       ResolveProductInstallRoots(spec) ??
                       ResolveStartMenuShortcut(spec) ??
                       ResolveUninstallRegistry(spec) ??
                       throw new WindowsControlException("application_not_installed", $"未找到已安装的 {spec.DisplayName}。" );
        lock (ResolutionGate) ResolvedTargets[spec.Id] = resolved;
        return resolved;
    }

    private static string? ResolveCandidatePaths(ApplicationSpec spec) =>
        spec.CandidatePaths.Select(TryNormalizePath).FirstOrDefault(path => path is not null && IsTrustedExecutable(path, spec.ExecutableFileNames));

    private static string? ResolveRunningProcessPath(ApplicationSpec spec)
    {
        foreach (var processName in spec.ProcessNames)
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    var path = TryNormalizePath(process.MainModule?.FileName);
                    if (path is not null && IsTrustedExecutable(path, spec.ExecutableFileNames)) return path;
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return null;
    }

    private static string? ResolveAppPathRegistry(ApplicationSpec spec)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var executableName in spec.ExecutableFileNames)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executableName}", false);
                var path = TryNormalizeExecutableValue(key?.GetValue(null) as string);
                if (path is not null && IsTrustedExecutable(path, spec.ExecutableFileNames)) return path;
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return null;
    }

    private static string? ResolveProductInstallRoots(ApplicationSpec spec)
    {
        foreach (var root in spec.DiscoveryRoots)
        {
            var normalized = TryNormalizeDirectory(root);
            if (normalized is null || !IsWithinTrustedRoot(normalized)) continue;
            var executable = FindExecutableBelow(normalized, spec.ExecutableFileNames, 3);
            if (executable is not null && IsTrustedExecutable(executable, spec.ExecutableFileNames)) return executable;
        }
        return null;
    }

    private static string? ResolveStartMenuShortcut(ApplicationSpec spec)
    {
        var names = spec.ShortcutNames.Select(static name => name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.lnk")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var root in TrustedStartMenuRoots())
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var shortcut in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    if (names.Contains(Path.GetFileName(shortcut)) && IsWithin(shortcut, root)) return Path.GetFullPath(shortcut);
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { }
        }
        return null;
    }

    private static string? ResolveUninstallRegistry(ApplicationSpec spec)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false);
                if (uninstall is null) continue;
                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(subKeyName, false);
                    if (key is null) continue;
                    var displayName = key.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName) ||
                        !spec.RegistryDisplayNames.Any(name => displayName.Contains(name, StringComparison.OrdinalIgnoreCase))) continue;

                    var displayIcon = TryNormalizeExecutableValue(key.GetValue("DisplayIcon") as string);
                    if (displayIcon is not null && IsTrustedExecutable(displayIcon, spec.ExecutableFileNames)) return displayIcon;

                    var installLocation = TryNormalizeDirectory(key.GetValue("InstallLocation") as string);
                    var discovered = installLocation is null ? null : FindExecutableBelow(installLocation, spec.ExecutableFileNames, 3);
                    if (discovered is not null && IsTrustedExecutable(discovered, spec.ExecutableFileNames)) return discovered;
                }
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return null;
    }

    private static string? FindExecutableBelow(string root, IReadOnlySet<string> executableNames, int maximumDepth)
    {
        if (!Directory.Exists(root) || !IsWithinTrustedRoot(root)) return null;
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var examinedDirectories = 0;
        while (pending.Count > 0 && examinedDirectories++ < 256)
        {
            var (directory, depth) = pending.Dequeue();
            foreach (var executableName in executableNames)
            {
                var candidate = TryNormalizePath(Path.Combine(directory, executableName));
                if (candidate is not null && File.Exists(candidate)) return candidate;
            }
            if (depth >= maximumDepth) continue;
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory).Take(128)) pending.Enqueue((child, depth + 1));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { }
        }
        return null;
    }

    private static string? TryNormalizeExecutableValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        string candidate;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            candidate = end > 1 ? trimmed[1..end] : trimmed.Trim('"');
        }
        else
        {
            var comma = trimmed.LastIndexOf(',');
            if (comma > 0 && int.TryParse(trimmed[(comma + 1)..], out _)) trimmed = trimmed[..comma];
            candidate = trimmed;
        }
        return TryNormalizePath(Environment.ExpandEnvironmentVariables(candidate));
    }

    private static string? TryNormalizeDirectory(string? value)
    {
        var path = TryNormalizePath(value?.Trim(' ', '"'));
        return path is not null && Directory.Exists(path) ? path : null;
    }

    private static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool IsTrustedLaunchTarget(string path, ApplicationSpec spec)
    {
        if (!File.Exists(path)) return false;
        if (spec.DirectLaunchTarget is { } direct &&
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(direct), StringComparison.OrdinalIgnoreCase))
            return WindowsInstalledApplicationCatalog.IsTrustedLaunchTarget(path);
        if (string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
            return spec.ShortcutNames.Any(name => string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase)) &&
                   TrustedStartMenuRoots().Any(root => !string.IsNullOrWhiteSpace(root) && IsWithin(path, root));
        return IsTrustedExecutable(path, spec.ExecutableFileNames);
    }

    internal static bool IsTrustedExecutable(string path, IReadOnlySet<string> executableNames) =>
        File.Exists(path) && executableNames.Contains(Path.GetFileName(path)) && IsWithinTrustedRoot(path);

    private static bool IsWithinTrustedRoot(string path) => TrustedExecutableRoots()
        .Where(static root => !string.IsNullOrWhiteSpace(root))
        .Any(root => IsWithin(path, root));

    private static IEnumerable<string> TrustedExecutableRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    private static IEnumerable<string> TrustedStartMenuRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
    }

    private static bool IsWithin(string path, string root)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(path);
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static void ForgetResolvedTarget(string applicationId)
    {
        lock (ResolutionGate) ResolvedTargets.Remove(applicationId);
    }

    private static int CountVisibleWindows(ApplicationSpec spec) => GetVisibleWindows(spec).Count;

    private static List<nint> GetVisibleWindows(ApplicationSpec spec)
    {
        var processIds = GetProcessIds(spec);
        var windows = new List<nint>();
        EnumWindows((window, parameter) =>
        {
            if (!IsWindowVisible(window)) return true;
            var belongsToKnownProcess = processIds.Count > 0 && WindowBelongsTo(window, processIds);
            if (!belongsToKnownProcess && !MatchesDynamicWindowTitle(window, spec)) return true;
            if (spec.WindowClasses.Count > 0)
            {
                var buffer = new char[128];
                var length = GetClassName(window, buffer, buffer.Length);
                if (length <= 0 || !spec.WindowClasses.Contains(new string(buffer, 0, length))) return true;
            }
            windows.Add(window);
            return true;
        }, 0);
        return windows;
    }

    private static bool MatchesDynamicWindowTitle(nint window, ApplicationSpec spec)
    {
        if (spec.DirectLaunchTarget is null || spec.WindowTitleAliases.Count == 0) return false;
        var length = Math.Min(512, Math.Max(0, GetWindowTextLength(window)));
        if (length == 0) return false;
        var buffer = new char[length + 1];
        var copied = GetWindowText(window, buffer, buffer.Length);
        if (copied <= 0) return false;
        var title = WindowsInstalledApplicationCatalog.NormalizeName(new string(buffer, 0, copied));
        return title.Length > 0 && spec.WindowTitleAliases
            .Select(WindowsInstalledApplicationCatalog.NormalizeName)
            .Where(static alias => alias.Length >= 2)
            .Any(alias => title.Contains(alias, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<uint> GetProcessIds(ApplicationSpec spec)
    {
        var processIds = new HashSet<uint>();
        foreach (var name in spec.ProcessNames)
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process) processIds.Add((uint)process.Id);
        }
        return processIds;
    }

    private static bool IsApplicationForeground(ApplicationSpec spec)
    {
        var foreground = GetForegroundWindow();
        return foreground != 0 && WindowBelongsTo(foreground, GetProcessIds(spec));
    }

    private static bool WindowBelongsTo(nint window, HashSet<uint> processIds)
    {
        if (GetWindowThreadProcessId(window, out var processId) != 0 && processIds.Contains(processId)) return true;
        return HasDescendantOwnedBy(window, processIds);
    }

    private static bool HasDescendantOwnedBy(nint parent, HashSet<uint> processIds)
    {
        var found = false;
        var enumerationCompleted = EnumChildWindows(parent, (child, parameter) =>
        {
            if (GetWindowThreadProcessId(child, out var processId) != 0 && processIds.Contains(processId))
            {
                found = true;
                return false;
            }
            return true;
        }, 0);
        return enumerationCompleted || found ? found : false;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate()) return true;
            await Task.Delay(75, cancellationToken).ConfigureAwait(false);
        }
        return predicate();
    }

    private static WindowsControlResult ApplicationResult(string action, ApplicationSpec spec, string state) =>
        new(action, true, state, spec.Id, spec.DisplayName);

    private static Dictionary<string, ApplicationSpec> CreateApplications()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string SystemPath(string name) => Path.Combine(windows, "System32", name);
        string P(string root, params string[] parts) => string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine([root, .. parts]);
        static HashSet<string> Names(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
        static ApplicationSpec App(
            string id, string display, string[] processes, string[] executables, string[] candidates,
            string[] shortcuts, string[] registryNames, bool canClose = true, string[]? windowClasses = null,
            string[]? discoveryRoots = null) =>
            new(id, display, Names(processes), Names(executables), candidates, shortcuts, registryNames, canClose,
                windowClasses is null ? null : Names(windowClasses), discoveryRoots ?? []);

        return new Dictionary<string, ApplicationSpec>(StringComparer.Ordinal)
        {
            [WindowsApplicationIds.Notepad] = App(WindowsApplicationIds.Notepad, "记事本", ["notepad"], ["notepad.exe"], [SystemPath("notepad.exe")], ["记事本", "Notepad"], ["Notepad"]),
            [WindowsApplicationIds.Calculator] = App(WindowsApplicationIds.Calculator, "计算器", ["CalculatorApp", "calc"], ["calc.exe"], [SystemPath("calc.exe")], ["计算器", "Calculator"], ["Windows Calculator"]),
            [WindowsApplicationIds.FileExplorer] = App(WindowsApplicationIds.FileExplorer, "文件资源管理器", ["explorer"], ["explorer.exe"], [P(windows, "explorer.exe")], ["文件资源管理器", "File Explorer"], ["File Explorer"], false, ["CabinetWClass", "ExploreWClass"]),
            [WindowsApplicationIds.Paint] = App(WindowsApplicationIds.Paint, "画图", ["mspaint"], ["mspaint.exe"], [SystemPath("mspaint.exe")], ["画图", "Paint"], ["Paint"]),
            [WindowsApplicationIds.SnippingTool] = App(WindowsApplicationIds.SnippingTool, "截图工具", ["SnippingTool"], ["SnippingTool.exe"], [SystemPath("SnippingTool.exe")], ["截图工具", "Snipping Tool"], ["Snipping Tool"]),
            [WindowsApplicationIds.Chrome] = App(WindowsApplicationIds.Chrome, "Google Chrome", ["chrome"], ["chrome.exe"], [P(programFiles, "Google", "Chrome", "Application", "chrome.exe"), P(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"), P(local, "Google", "Chrome", "Application", "chrome.exe")], ["Google Chrome"], ["Google Chrome"]),
            [WindowsApplicationIds.Edge] = App(WindowsApplicationIds.Edge, "Microsoft Edge", ["msedge"], ["msedge.exe"], [P(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"), P(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"), P(local, "Microsoft", "Edge", "Application", "msedge.exe")], ["Microsoft Edge"], ["Microsoft Edge"]),
            [WindowsApplicationIds.WeChat] = App(WindowsApplicationIds.WeChat, "微信", ["Weixin", "WeChat"], ["Weixin.exe", "WeChat.exe"], [P(programFiles, "Tencent", "Weixin", "Weixin.exe"), P(programFiles, "Tencent", "WeChat", "WeChat.exe"), P(programFilesX86, "Tencent", "WeChat", "WeChat.exe"), P(local, "Tencent", "WeChat", "WeChat.exe")], ["微信", "Weixin", "WeChat"], ["微信", "Weixin", "WeChat"]),
            [WindowsApplicationIds.MicrosoftWord] = Office(WindowsApplicationIds.MicrosoftWord, "Microsoft Word", "WINWORD", "Word"),
            [WindowsApplicationIds.MicrosoftExcel] = Office(WindowsApplicationIds.MicrosoftExcel, "Microsoft Excel", "EXCEL", "Excel"),
            [WindowsApplicationIds.MicrosoftPowerPoint] = Office(WindowsApplicationIds.MicrosoftPowerPoint, "Microsoft PowerPoint", "POWERPNT", "PowerPoint"),
            [WindowsApplicationIds.Qq] = App(WindowsApplicationIds.Qq, "QQ", ["QQ"], ["QQ.exe"], [P(programFiles, "Tencent", "QQNT", "QQ.exe"), P(programFilesX86, "Tencent", "QQNT", "QQ.exe"), P(local, "Programs", "QQ", "QQ.exe")], ["QQ"], ["QQ", "腾讯QQ"]),
            [WindowsApplicationIds.WeCom] = App(WindowsApplicationIds.WeCom, "企业微信", ["WXWork", "WeCom"], ["WXWork.exe", "WeCom.exe"], [P(programFiles, "WXWork", "WXWork.exe"), P(programFilesX86, "WXWork", "WXWork.exe"), P(local, "WXWork", "WXWork.exe")], ["企业微信", "WeCom", "WXWork"], ["企业微信", "WeCom", "WXWork"]),
            [WindowsApplicationIds.DingTalk] = App(WindowsApplicationIds.DingTalk, "钉钉", ["DingTalk"], ["DingTalk.exe"], [P(programFiles, "DingDing", "DingTalk.exe"), P(programFilesX86, "DingDing", "DingTalk.exe"), P(local, "DingTalk", "DingTalk.exe")], ["钉钉", "DingTalk"], ["钉钉", "DingTalk"]),
            [WindowsApplicationIds.Feishu] = App(WindowsApplicationIds.Feishu, "飞书", ["Feishu", "Lark"], ["Feishu.exe", "Lark.exe"], [P(local, "Feishu", "Feishu.exe"), P(local, "Programs", "Lark", "Lark.exe")], ["飞书", "Feishu", "Lark"], ["飞书", "Feishu", "Lark"]),
            [WindowsApplicationIds.TencentMeeting] = App(WindowsApplicationIds.TencentMeeting, "腾讯会议", ["wemeetapp", "TencentMeeting"], ["wemeetapp.exe", "TencentMeeting.exe"], [P(programFiles, "Tencent", "WeMeet", "wemeetapp.exe"), P(programFilesX86, "Tencent", "WeMeet", "wemeetapp.exe"), P(roaming, "Tencent", "WeMeet", "wemeetapp.exe")], ["腾讯会议", "VooV Meeting", "WeMeet"], ["腾讯会议", "VooV Meeting", "WeMeet"]),
            [WindowsApplicationIds.WpsWriter] = Wps(WindowsApplicationIds.WpsWriter, "WPS 文字", "wps", "WPS Office", "WPS 文字"),
            [WindowsApplicationIds.WpsSpreadsheets] = Wps(WindowsApplicationIds.WpsSpreadsheets, "WPS 表格", "et", "WPS 表格", "WPS Spreadsheets"),
            [WindowsApplicationIds.WpsPresentation] = Wps(WindowsApplicationIds.WpsPresentation, "WPS 演示", "wpp", "WPS 演示", "WPS Presentation"),
            [WindowsApplicationIds.VsCode] = App(WindowsApplicationIds.VsCode, "Visual Studio Code", ["Code"], ["Code.exe"], [P(local, "Programs", "Microsoft VS Code", "Code.exe"), P(programFiles, "Microsoft VS Code", "Code.exe")], ["Visual Studio Code"], ["Microsoft Visual Studio Code", "Visual Studio Code"]),
        };

        ApplicationSpec Office(string id, string displayName, string executableStem, string shortcutName) => App(
            id, displayName, [executableStem], [$"{executableStem}.EXE"],
            [P(programFiles, "Microsoft Office", "root", "Office16", $"{executableStem}.EXE"), P(programFilesX86, "Microsoft Office", "root", "Office16", $"{executableStem}.EXE")],
            [shortcutName, displayName], [displayName, "Microsoft 365"]);

        ApplicationSpec Wps(string id, string displayName, string executableStem, params string[] shortcutNames) => App(
            id, displayName, [executableStem], [$"{executableStem}.exe"],
            [P(programFiles, "Kingsoft", "WPS Office", "office6", $"{executableStem}.exe"), P(programFilesX86, "Kingsoft", "WPS Office", "office6", $"{executableStem}.exe")],
            shortcutNames, ["WPS Office", displayName],
            discoveryRoots: [P(local, "Kingsoft", "WPS Office"), P(roaming, "Kingsoft", "WPS Office")]);
    }

    private const uint WmClose = 0x0010;
    private const int SwShow = 5;
    private const int SwMinimize = 6;
    private const int SwMaximize = 3;
    private const int SwRestore = 9;
    private static readonly nint HwndTop = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;

    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, [Out] char[] className, int maximumCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, [Out] char[] text, int maximumCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    private sealed record ApplicationSpec(
        string Id,
        string DisplayName,
        IReadOnlySet<string> ProcessNames,
        IReadOnlySet<string> ExecutableFileNames,
        IReadOnlyList<string> CandidatePaths,
        IReadOnlyList<string> ShortcutNames,
        IReadOnlyList<string> RegistryDisplayNames,
        bool CanClose = true,
        IReadOnlySet<string>? RequiredWindowClasses = null,
        IReadOnlyList<string>? RequiredDiscoveryRoots = null,
        string? DirectLaunchTarget = null,
        IReadOnlyList<string>? RequiredWindowTitleAliases = null)
    {
        public IReadOnlySet<string> WindowClasses { get; } = RequiredWindowClasses ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> DiscoveryRoots { get; } = RequiredDiscoveryRoots ?? [];
        public IReadOnlyList<string> WindowTitleAliases { get; } = RequiredWindowTitleAliases ?? [];
    }
}
