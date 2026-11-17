using Microsoft.Win32;

namespace Velia.Core.WindowsControl;

internal enum OfficeSuiteKind
{
    Microsoft,
    Wps,
}

internal sealed record OfficeApplicationFamily(
    string GenericApplicationId,
    string MicrosoftApplicationId,
    string WpsApplicationId,
    string FileExtension,
    string DisplayName);

internal static class OfficeApplicationResolver
{
    private static readonly Dictionary<string, OfficeApplicationFamily> Families =
        new Dictionary<string, OfficeApplicationFamily>(StringComparer.Ordinal)
        {
            [WindowsApplicationIds.Word] = new(
                WindowsApplicationIds.Word,
                WindowsApplicationIds.MicrosoftWord,
                WindowsApplicationIds.WpsWriter,
                ".docx",
                "文字处理程序"),
            [WindowsApplicationIds.Excel] = new(
                WindowsApplicationIds.Excel,
                WindowsApplicationIds.MicrosoftExcel,
                WindowsApplicationIds.WpsSpreadsheets,
                ".xlsx",
                "电子表格程序"),
            [WindowsApplicationIds.PowerPoint] = new(
                WindowsApplicationIds.PowerPoint,
                WindowsApplicationIds.MicrosoftPowerPoint,
                WindowsApplicationIds.WpsPresentation,
                ".pptx",
                "演示文稿程序"),
        };

    public static bool TryGetFamily(string applicationId, out OfficeApplicationFamily family) =>
        Families.TryGetValue(applicationId, out family!);

    public static IReadOnlyList<string> OrderConcreteApplicationIds(
        OfficeApplicationFamily family,
        string? defaultProgId)
    {
        var preferred = ClassifyProgId(defaultProgId);
        return preferred == OfficeSuiteKind.Microsoft
            ? [family.MicrosoftApplicationId, family.WpsApplicationId]
            : [family.WpsApplicationId, family.MicrosoftApplicationId];
    }

    internal static OfficeSuiteKind? ClassifyProgId(string? progId)
    {
        if (string.IsNullOrWhiteSpace(progId)) return null;
        var value = progId.Trim();
        if (value.Contains("WPS", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("KINGSOFT", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("ET.", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("KET.", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("WPP.", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("KWPP.", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith("ET.EXE", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith("WPP.EXE", StringComparison.OrdinalIgnoreCase))
            return OfficeSuiteKind.Wps;
        if (value.StartsWith("Word.", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("Excel.", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("PowerPoint.", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("WINWORD", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith("EXCEL.EXE", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("POWERPNT", StringComparison.OrdinalIgnoreCase))
            return OfficeSuiteKind.Microsoft;
        return null;
    }

    public static string? ReadDefaultProgId(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || extension[0] != '.') return null;
        return ReadCurrentUserValue(
                   $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice",
                   "ProgId") ??
               ReadCurrentUserValue($@"Software\Classes\{extension}", null) ??
               ReadClassesRootValue(extension);
    }

    private static string? ReadCurrentUserValue(string keyPath, string? valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, false);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? ReadClassesRootValue(string keyPath)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(keyPath, false);
            return key?.GetValue(null) as string;
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
