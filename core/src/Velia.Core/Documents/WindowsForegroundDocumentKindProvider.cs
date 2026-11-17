using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Velia.Core.Documents;

internal enum ForegroundDocumentKind
{
    Unknown,
    Other,
    Text,
    Word,
    Excel,
}

internal interface IForegroundDocumentKindProvider
{
    ForegroundDocumentKind GetCurrent();
}

internal sealed class WindowsForegroundDocumentKindProvider : IForegroundDocumentKindProvider
{
    public ForegroundDocumentKind GetCurrent()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return ForegroundDocumentKind.Unknown;
        if (!TryReadProcess(foreground, out var processId, out var processName))
            return ForegroundDocumentKind.Unknown;

        var direct = Classify(processName);
        if (direct is not ForegroundDocumentKind.Other) return direct;
        if (!IsVelia(processName)) return ForegroundDocumentKind.Other;

        // Voice commands can leave Velia's own panel in front. In that case,
        // use the first supported document window behind it in Windows z-order.
        // Do not activate or move any window while resolving the binding.
        var result = ForegroundDocumentKind.Other;
        _ = EnumWindows((candidate, _) =>
        {
            if (candidate == foreground || !IsWindowVisible(candidate) || IsIconic(candidate) ||
                !TryReadProcess(candidate, out var candidateProcessId, out var candidateProcessName) ||
                candidateProcessId == processId)
                return true;
            var kind = Classify(candidateProcessName);
            if (kind is not (ForegroundDocumentKind.Text or ForegroundDocumentKind.Word or ForegroundDocumentKind.Excel))
                return true;
            result = kind;
            return false;
        }, 0);
        return result;
    }

    private static ForegroundDocumentKind Classify(string processName) => processName.ToLowerInvariant() switch
    {
        "notepad" => ForegroundDocumentKind.Text,
        "wps" or "winword" => ForegroundDocumentKind.Word,
        "et" or "excel" => ForegroundDocumentKind.Excel,
        _ => ForegroundDocumentKind.Other,
    };

    private static bool IsVelia(string processName) =>
        processName.Equals("Velia", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("Velia.Core", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadProcess(nint window, out int processId, out string processName)
    {
        processId = 0;
        processName = string.Empty;
        if (GetWindowThreadProcessId(window, out var value) == 0 || value > int.MaxValue) return false;
        processId = (int)value;
        try
        {
            using var process = Process.GetProcessById(processId);
            processName = process.ProcessName;
            return processName.Length > 0;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
}
