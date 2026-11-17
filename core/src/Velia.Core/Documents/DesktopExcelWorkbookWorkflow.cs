using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Velia.Core.WindowsControl;

namespace Velia.Core.Documents;

internal static class DesktopExcelWorkbookEditActions
{
    public const string SetCell = "setCell";
    public const string ClearCell = "clearCell";
    public const string ClearRange = "clearRange";
    public const string ClearSelection = "clearSelection";
    public const string ClearSheet = "clearSheet";
    public const string DeleteText = "deleteText";
    public const string Save = "save";
    public const string Close = "close";
}

internal sealed record DesktopExcelWorkbookCreateCommand(
    string FileName,
    bool OpenAfterCreate = true);

internal sealed record DesktopExcelWorkbookCreateResult(
    bool Verified,
    bool Opened,
    string State,
    string FileName,
    string FullPath);

internal sealed record DesktopExcelCellCommand(
    string Action,
    string? CellReference = null,
    string? Value = null,
    bool IsFormula = false);

internal sealed record DesktopExcelAutomationResult(
    bool Verified,
    string Action,
    string? CellReference,
    string? StoredValue,
    bool Closed = false);

internal sealed record DesktopExcelCellResult(
    bool Verified,
    string Action,
    string FileName,
    string? CellReference,
    string? StoredValue,
    bool Closed);

internal interface IDesktopExcelAutomationService
{
    Task<string?> TryGetCurrentWorkbookPathAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    Task<bool> OpenAsync(string fullPath, CancellationToken cancellationToken);

    Task<DesktopExcelAutomationResult> ExecuteAsync(
        DesktopExcelCellCommand command,
        string fullPath,
        CancellationToken cancellationToken);
}

internal interface IDesktopExcelWorkbookWorkflowService
{
    Task<string?> TryBindCurrentAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    Task<DesktopFileOpenResult?> TryOpenExistingAsync(
        DesktopFileOpenCommand command,
        CancellationToken cancellationToken);

    Task<DesktopExcelWorkbookCreateResult> CreateAsync(
        DesktopExcelWorkbookCreateCommand command,
        CancellationToken cancellationToken);

    Task<DesktopExcelCellResult> ExecuteAsync(
        DesktopExcelCellCommand command,
        string fileName,
        CancellationToken cancellationToken);
}

internal static partial class DesktopExcelWorkbookCreationIntentDetector
{
    private static readonly string[] DeniedOrInstructionalTerms = ["不要", "别", "怎么", "如何", "能不能", "是否"];

    public static bool TryParseExplicitCommand(
        string request,
        out DesktopExcelWorkbookCreateCommand command)
    {
        command = null!;
        if (string.IsNullOrWhiteSpace(request)) return false;
        var value = request.Normalize(NormalizationForm.FormKC).Trim().TrimEnd('。', '！', '!', '？', '?');
        if ((!value.Contains("创建", StringComparison.Ordinal) && !value.Contains("新建", StringComparison.Ordinal)) ||
            !LooksLikeWorkbook(value) ||
            DeniedOrInstructionalTerms.Any(value.Contains))
            return false;

        var after = NameAfterActionPattern().Match(value);
        var before = NameBeforeKindPattern().Match(value);
        var name = after.Success ? after.Groups["name"].Value : before.Success ? before.Groups["name"].Value : string.Empty;
        name = name.Trim(' ', '：', ':', '“', '”', '"', '‘', '’');
        if (name.EndsWith('的')) name = name[..^1].TrimEnd();
        if (name.Length is < 1 or > 100) return false;
        if (!name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) name += ".xlsx";
        command = new DesktopExcelWorkbookCreateCommand(name);
        return true;
    }

    public static string GetCanonicalKey(DesktopExcelWorkbookCreateCommand command) =>
        $"document-create:excel:{Normalize(command.FileName)}";

    private static bool LooksLikeWorkbook(string value) =>
        value.Contains("Excel", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("XLSX", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("电子表格", StringComparison.Ordinal) ||
        value.Contains("工作簿", StringComparison.Ordinal) ||
        value.Contains("表格文件", StringComparison.Ordinal) ||
        value.Contains("表格文档", StringComparison.Ordinal) ||
        value.Contains("表格", StringComparison.Ordinal);

    private static string Normalize(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[\s\p{P}\p{S}]", string.Empty);

    [GeneratedRegex(@"(?:改名|更名|命名|取名|起名|文件名|名字)(?:叫做|叫|为|是)?\s*[：:]?\s*(?<name>[^，。！？!?\r\n]{1,100}?)(?=\s*[，,]?\s*(?:然后|并且|并打开|再打开|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex NameAfterActionPattern();

    [GeneratedRegex("""(?:名为|叫做)\s*[“"]?(?<name>[^“”"，。！？!?\r\n]{1,100}?)[”"]?\s*的(?:Excel|XLSX|电子表格|工作簿|表格文件|表格文档|表格)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex NameBeforeKindPattern();
}

internal static partial class DesktopExcelCellIntentDetector
{
    private static readonly string[] DeniedTerms = ["不要", "别", "禁止", "无需", "不用"];
    private static readonly string[] InstructionalPrefixes = ["怎么", "如何", "为什么", "能不能", "是否"];

    public static bool TryParseExplicitCommand(string request, out DesktopExcelCellCommand command)
    {
        command = null!;
        if (string.IsNullOrWhiteSpace(request)) return false;
        var value = request.Normalize(NormalizationForm.FormKC).Trim().TrimEnd('。', '！', '!', '？', '?');
        if (DeniedTerms.Any(value.Contains) || InstructionalPrefixes.Any(value.StartsWith) || value.Length > 1_200)
            return false;
        var compact = Regex.Replace(value, @"\s+", string.Empty, RegexOptions.CultureInvariant);

        var set = NaturalSetCellPattern().Match(value);
        if (!set.Success) set = SetCellPattern().Match(value);
        if (set.Success && TryNormalizeCell(set.Groups["cell"].Value, out var cell))
        {
            var text = CleanValue(set.Groups["value"].Value);
            if (!IsValidValue(text)) return false;
            var formula = NormalizeFormula(text, out var isFormula);
            command = new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.SetCell, cell, formula, isFormula);
            return true;
        }

        var clear = ClearCellPattern().Match(compact);
        if (!clear.Success) clear = ClearCellObjectPattern().Match(compact);
        var clearRange = ClearRangePattern().Match(compact);
        if (!clearRange.Success) clearRange = ClearRangeObjectPattern().Match(compact);
        if (!clearRange.Success) clearRange = NaturalClearRangePattern().Match(compact);
        if (clearRange.Success && TryNormalizeRange(
                clearRange.Groups["start"].Value,
                clearRange.Groups["end"].Value,
                out var range))
        {
            command = new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.ClearRange, range);
            return true;
        }

        if (ClearSelectionPattern().IsMatch(compact) || ClearSelectionObjectPattern().IsMatch(compact))
        {
            command = new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.ClearSelection);
            return true;
        }

        if (ClearSheetPattern().IsMatch(compact) || ClearSheetObjectPattern().IsMatch(compact))
        {
            command = new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.ClearSheet);
            return true;
        }

        if (!clear.Success) clear = NaturalClearCellPattern().Match(compact);

        if (clear.Success && TryNormalizeCell(clear.Groups["cell"].Value, out cell))
        {
            command = new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.ClearCell, cell);
            return true;
        }

        var deleteText = DeleteTextPattern().Match(value);
        if (!deleteText.Success) deleteText = DeleteTextObjectPattern().Match(value);
        if (deleteText.Success)
        {
            var target = CleanValue(deleteText.Groups["text"].Value);
            if (IsValidValue(target))
            {
                command = new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.DeleteText, Value: target);
                return true;
            }
        }

        if (!DesktopDocumentLifecycleIntentDetector.TryParse(value, out var lifecycle) ||
            lifecycle.Target != DesktopDocumentLifecycleTarget.Excel)
            return false;
        if (lifecycle.Action == DesktopTextDocumentEditActions.Close)
        {
            command = new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.Close);
            return true;
        }
        if (lifecycle.Action == DesktopTextDocumentEditActions.Save)
        {
            command = new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.Save);
            return true;
        }
        return false;
    }

    public static string GetCanonicalKey(DesktopExcelCellCommand command) =>
        $"document-edit:excel:{command.Action}:{command.CellReference}:{NormalizeKey(command.Value)}";

    internal static bool TryNormalizeCell(string value, out string cellReference)
    {
        cellReference = string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        var natural = NaturalCellReferencePattern().Match(normalized);
        if (natural.Success &&
            TryParseRowNumber(natural.Groups["row"].Value, out var naturalRow) &&
            TryParseRowNumber(natural.Groups["columnNumber"].Value, out var naturalColumn) &&
            naturalRow is >= 1 and <= 1_048_576 &&
            naturalColumn is >= 1 and <= 16_384)
        {
            cellReference = ColumnName(naturalColumn) + naturalRow.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        var match = CellReferencePattern().Match(normalized);
        if (!match.Success || !TryParseRowNumber(match.Groups["row"].Value, out var row) || row is < 1 or > 1_048_576)
            return false;
        var column = match.Groups["column"].Value.ToUpperInvariant();
        var number = 0;
        foreach (var character in column) number = checked(number * 26 + character - 'A' + 1);
        if (number > 16_384) return false;
        cellReference = column + row.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    internal static bool TryNormalizeRange(string start, string end, out string rangeReference)
    {
        rangeReference = string.Empty;
        if (!TryNormalizeCell(start, out var first) || !TryNormalizeCell(end, out var last) ||
            !TryReadCoordinates(first, out var firstColumn, out var firstRow) ||
            !TryReadCoordinates(last, out var lastColumn, out var lastRow))
            return false;
        var topLeft = ColumnName(Math.Min(firstColumn, lastColumn)) + Math.Min(firstRow, lastRow);
        var bottomRight = ColumnName(Math.Max(firstColumn, lastColumn)) + Math.Max(firstRow, lastRow);
        rangeReference = topLeft == bottomRight ? topLeft : topLeft + ":" + bottomRight;
        return true;
    }

    internal static bool TryReadCoordinates(string cellReference, out int column, out int row)
    {
        column = 0;
        row = 0;
        var match = CellReferencePattern().Match(cellReference);
        if (!match.Success || !int.TryParse(match.Groups["row"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out row))
            return false;
        foreach (var character in match.Groups["column"].Value.ToUpperInvariant())
            column = checked(column * 26 + character - 'A' + 1);
        return true;
    }

    private static string ColumnName(int column)
    {
        var builder = new StringBuilder();
        while (column > 0)
        {
            column--;
            builder.Insert(0, (char)('A' + column % 26));
            column /= 26;
        }
        return builder.ToString();
    }

    private static bool TryParseRowNumber(string value, out int row)
    {
        var token = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out row)) return true;
        if (token.Length == 0)
        {
            row = 0;
            return false;
        }

        if (!token.Any(character => character is '十' or '百' or '千' or '万'))
        {
            long digits = 0;
            foreach (var character in token)
            {
                if (!TryGetChineseDigit(character, out var digit))
                {
                    row = 0;
                    return false;
                }
                digits = digits * 10 + digit;
                if (digits > int.MaxValue)
                {
                    row = 0;
                    return false;
                }
            }
            row = (int)digits;
            return true;
        }

        long total = 0;
        long section = 0;
        var currentDigit = -1;
        foreach (var character in token)
        {
            if (TryGetChineseDigit(character, out var digit))
            {
                currentDigit = digit;
                continue;
            }
            var unit = character switch
            {
                '十' => 10,
                '百' => 100,
                '千' => 1_000,
                '万' => 10_000,
                _ => 0,
            };
            if (unit == 0)
            {
                row = 0;
                return false;
            }
            if (unit == 10_000)
            {
                section += currentDigit >= 0 ? currentDigit : 0;
                total += Math.Max(1, section) * unit;
                section = 0;
            }
            else
            {
                section += (currentDigit >= 0 ? currentDigit : 1) * unit;
            }
            currentDigit = -1;
        }
        var parsed = total + section + (currentDigit >= 0 ? currentDigit : 0);
        if (parsed > int.MaxValue)
        {
            row = 0;
            return false;
        }
        row = (int)parsed;
        return true;
    }

    private static bool TryGetChineseDigit(char value, out int digit)
    {
        digit = value switch
        {
            '零' or '〇' => 0,
            '一' => 1,
            '二' or '两' => 2,
            '三' => 3,
            '四' => 4,
            '五' => 5,
            '六' => 6,
            '七' => 7,
            '八' => 8,
            '九' => 9,
            _ => -1,
        };
        return digit >= 0;
    }

    private static string CleanValue(string value) =>
        value.Trim(' ', '：', ':', '“', '”', '"', '‘', '’');

    private static bool IsValidValue(string value) => value.Length is >= 1 and <= 2_000 &&
        !value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));

    private static string NormalizeFormula(string value, out bool isFormula)
    {
        isFormula = value.StartsWith('=');
        return value;
    }

    private static string NormalizeKey(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"[\s\p{P}\p{S}]", string.Empty);

    [GeneratedRegex(@"^(?:(?:在)?\s*(?<cell>[A-Za-z]{1,3}\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16}))(?:\s*单元格)?(?:中|里)?\s*(?:输入|写入|填写|填入|设为|设置为)|(?:把|将)\s*(?<cell>[A-Za-z]{1,3}\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16}))(?:\s*单元格)?\s*(?:改成|改为|设为|设置为))\s*[：:]?\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex SetCellPattern();

    [GeneratedRegex(@"^(?:(?:在)?\s*(?<cell>(?:第?\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})\s*行\s*第?\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})\s*列|第?\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})\s*列\s*第?\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})\s*行))(?:\s*单元格)?(?:中|里)?\s*(?:输入|写入|填写|填入|设为|设置为)|(?:把|将)\s*(?<cell>(?:第?\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})\s*行\s*第?\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})\s*列|第?\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})\s*列\s*第?\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})\s*行))(?:\s*单元格)?\s*(?:改成|改为|设为|设置为))\s*[：:]?\s*(?<value>.+)$", RegexOptions.CultureInvariant, 50)]
    private static partial Regex NaturalSetCellPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:(?:清空|清除|删除|删掉|去掉|移除)(?:一下)?(?:(?:当前)?(?:表格|工作表|工作簿|Excel)(?:里的|里|中的|内的|的)?)?(?<cell>(?:第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})行第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})列|第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})列第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})行))(?:单元格)?(?:里的|里|中的|内的|的)?(?:内容|数据)?|(?:把|将)(?:(?:当前)?(?:表格|工作表|工作簿|Excel)(?:里的|里|中的|内的|的)?)?(?<cell>(?:第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})行第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})列|第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})列第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})行))(?:单元格)?(?:里的|里|中的|内的|的)?(?:内容|数据)?(?:清空|清除|删除|删掉|去掉|移除)(?:一下)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex NaturalClearCellPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:(?:清空|清除|删除|删掉|去掉|移除)(?:一下)?(?:(?:当前)?(?:表格|工作表|工作簿|Excel)(?:里的|里|中的|内的|的)?)?(?<start>(?:第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})行第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})列|第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})列第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})行))(?:到|至)(?<end>(?:第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})行第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})列|第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})列第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})行))(?:单元格|区域)?(?:里的|里|中的|内的|的)?(?:内容|数据)?|(?:把|将)(?:(?:当前)?(?:表格|工作表|工作簿|Excel)(?:里的|里|中的|内的|的)?)?(?<start>(?:第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})行第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})列|第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})列第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})行))(?:到|至)(?<end>(?:第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})行第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})列|第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})列第?(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})行))(?:单元格|区域)?(?:里的|里|中的|内的|的)?(?:内容|数据)?(?:清空|清除|删除|删掉|去掉|移除)(?:一下)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex NaturalClearRangePattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:清空|清除|删除|删掉|去掉|移除)(?:一下)?(?:(?:当前)?(?:表格|工作表|工作簿|Excel)(?:里的|里|中的|内的|的)?)?(?<cell>[A-Za-z]{1,3}\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16}))(?:\s*单元格)?(?:里的|里|中的|内的|的)?(?:内容|数据)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex ClearCellPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:把|将)(?:(?:当前)?(?:表格|工作表|工作簿|Excel)(?:里的|里|中的|内的|的)?)?(?<cell>[A-Za-z]{1,3}\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16}))(?:\s*单元格)?(?:里的|里|中的|内的|的)?(?:内容|数据)?(?:清空|清除|删除|删掉|去掉|移除)(?:一下)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex ClearCellObjectPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:清空|清除|删除|删掉|去掉|移除)(?:一下)?(?:(?:当前)?(?:表格|工作表|工作簿|Excel)(?:里的|里|中的|内的|的)?)?(?<start>[A-Za-z]{1,3}\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16}))\s*(?:到|至|[-:：])\s*(?<end>[A-Za-z]{1,3}\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16}))(?:\s*单元格|\s*区域)?(?:里的|里|中的|内的|的)?(?:内容|数据)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex ClearRangePattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:把|将)(?:(?:当前)?(?:表格|工作表|工作簿|Excel)(?:里的|里|中的|内的|的)?)?(?<start>[A-Za-z]{1,3}\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16}))\s*(?:到|至|[-:：])\s*(?<end>[A-Za-z]{1,3}\s*(?:\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16}))(?:\s*单元格|\s*区域)?(?:里的|里|中的|内的|的)?(?:内容|数据)?(?:清空|清除|删除|删掉|去掉|移除)(?:一下)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex ClearRangeObjectPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:清空|清除|删除|删掉|去掉|移除)(?:一下)?(?:当前|刚才)?(?:选中|选定|选择)(?:的)?(?:单元格|区域|内容|数据)$", RegexOptions.CultureInvariant, 50)]
    private static partial Regex ClearSelectionPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:把|将)(?:当前|刚才)?(?:选中|选定|选择)(?:的)?(?:单元格|区域|内容|数据)(?:清空|清除|删除|删掉|去掉|移除)(?:一下)?$", RegexOptions.CultureInvariant, 50)]
    private static partial Regex ClearSelectionObjectPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:清空|清除|删除|删掉|去掉|移除)(?:一下)?(?:当前)?(?:整个|全部|所有)?(?:表格|工作表|工作簿|Excel)(?:里的|里|中的|内的|的)?(?:(?:全部|所有)(?:单元格|内容|数据)?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex ClearSheetPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:把|将)(?:当前)?(?:整个|全部|所有)?(?:表格|工作表|工作簿|Excel)(?:里的|里|中的|内的|的)?(?:全部|所有)?(?:单元格|内容|数据)?(?:清空|清除|删除|删掉|去掉|移除)(?:一下)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex ClearSheetObjectPattern();

    [GeneratedRegex("""^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:删除|删掉|去掉|移除)(?:一下)?(?:当前)?(?:表格|工作表|工作簿|Excel)(?:里的|里|中的|内的|的)[“"]?(?<text>[^“”"，。]{1,200})[”"]?$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex DeleteTextPattern();

    [GeneratedRegex("""^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:把|将)(?:当前)?(?:表格|工作表|工作簿|Excel)(?:里的|里|中的|内的|的)[“"]?(?<text>[^“”"，。]{1,200})[”"]?\s*(?:删除|删掉|去掉|移除)(?:一下)?$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex DeleteTextObjectPattern();

    [GeneratedRegex(@"^(?<column>[A-Za-z]{1,3})\s*(?<row>\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})$", RegexOptions.CultureInvariant, 50)]
    private static partial Regex CellReferencePattern();

    [GeneratedRegex(@"^(?:第?\s*(?<row>\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})\s*行\s*第?\s*(?<columnNumber>\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})\s*列|第?\s*(?<columnNumber>\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})\s*列\s*第?\s*(?<row>\d{1,7}|[零〇一二两三四五六七八九十百千万]{1,16})\s*行)$", RegexOptions.CultureInvariant, 50)]
    private static partial Regex NaturalCellReferencePattern();
}

internal sealed class DesktopExcelWorkbookWorkflowService(
    IDesktopExcelAutomationService automation,
    DesktopFileOpenService? fileResolver = null,
    string? desktopPath = null) : IDesktopExcelWorkbookWorkflowService
{
    private readonly string desktop = Path.GetFullPath(
        desktopPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

    public async Task<string?> TryBindCurrentAsync(CancellationToken cancellationToken)
    {
        var candidate = await automation.TryGetCurrentWorkbookPathAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        try
        {
            var fullPath = Path.GetFullPath(candidate);
            if (!string.Equals(Path.GetDirectoryName(fullPath), desktop, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath) ||
                !fullPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
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
        if (!file.Extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) return null;
        if (!await automation.OpenAsync(file.FullName, cancellationToken).ConfigureAwait(false))
            throw new WindowsControlException(
                "desktop_file_open_unverified", $"已请求打开桌面文件“{file.Name}”，但没有验证到对应表格窗口。", true);
        return new DesktopFileOpenResult(true, "opened", file.Name);
    }

    public async Task<DesktopExcelWorkbookCreateResult> CreateAsync(
        DesktopExcelWorkbookCreateCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var fileName = ValidateName(command.FileName);
        if (!Directory.Exists(desktop))
            throw new DesktopDocumentWorkflowException("desktop_unavailable", "当前用户桌面不可用。", true);
        var target = Path.GetFullPath(Path.Combine(desktop, fileName));
        if (!string.Equals(Path.GetDirectoryName(target), desktop, StringComparison.OrdinalIgnoreCase)) throw InvalidName();
        if (File.Exists(target))
            throw new DesktopDocumentWorkflowException(
                "document_already_exists", $"桌面上已经存在“{fileName}”，为避免覆盖没有创建新文件。" );
        var temporary = Path.Combine(desktop, $".velia-{Guid.NewGuid():N}.xlsx.tmp");
        var moved = false;
        try
        {
            CreateEmptyPackage(temporary);
            ValidatePackage(temporary);
            File.Move(temporary, target, false);
            moved = true;
            if (!command.OpenAfterCreate)
                return new DesktopExcelWorkbookCreateResult(true, false, "created", fileName, target);
            if (!await automation.OpenAsync(target, cancellationToken).ConfigureAwait(false))
                throw new DesktopDocumentWorkflowException(
                    "document_created_open_failed", $"“{fileName}”已经创建，但没有验证到表格窗口。", true, true);
            return new DesktopExcelWorkbookCreateResult(true, true, "createdAndOpened", fileName, target);
        }
        catch (DesktopDocumentWorkflowException) { throw; }
        catch (IOException exception)
        {
            throw new DesktopDocumentWorkflowException(
                File.Exists(target) ? "document_already_exists" : "document_create_failed",
                File.Exists(target) ? $"桌面上已经存在“{fileName}”，为避免覆盖没有创建新文件。" : "Excel 工作簿创建失败。",
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

    public async Task<DesktopExcelCellResult> ExecuteAsync(
        DesktopExcelCellCommand command,
        string fileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var safeName = ValidateName(fileName);
        var path = Path.Combine(desktop, safeName);
        if (!File.Exists(path))
            throw new DesktopDocumentWorkflowException("document_not_found", $"桌面上没有找到“{safeName}”。", true);
        var result = await automation.ExecuteAsync(command, path, cancellationToken).ConfigureAwait(false);
        if (!result.Verified)
            throw new DesktopDocumentWorkflowException("spreadsheet_edit_unverified", "表格没有确认操作结果。", true);
        ValidatePackage(path);
        if (command.Action == DesktopExcelWorkbookEditActions.SetCell)
        {
            var stored = ReadPackageCell(path, command.CellReference!, command.IsFormula);
            if (!EquivalentCellValues(stored, result.StoredValue))
                throw new DesktopDocumentWorkflowException("spreadsheet_save_unverified", "单元格内容没有通过磁盘保存验证。", true);
        }
        else if (command.Action is DesktopExcelWorkbookEditActions.ClearCell or
                 DesktopExcelWorkbookEditActions.ClearRange or
                 DesktopExcelWorkbookEditActions.ClearSelection or
                 DesktopExcelWorkbookEditActions.ClearSheet)
        {
            if (string.IsNullOrWhiteSpace(result.CellReference) ||
                !IsPackageRangeEmpty(path, result.CellReference))
                throw new DesktopDocumentWorkflowException("spreadsheet_save_unverified", "表格内容清空没有通过磁盘保存验证。", true);
        }
        else if (command.Action == DesktopExcelWorkbookEditActions.DeleteText)
        {
            if (string.IsNullOrWhiteSpace(command.Value) || PackageContainsText(path, command.Value))
                throw new DesktopDocumentWorkflowException("spreadsheet_save_unverified", "表格指定文字删除没有通过磁盘保存验证。", true);
        }
        return new DesktopExcelCellResult(
            true, command.Action, safeName, result.CellReference, result.StoredValue, result.Closed);
    }

    internal static string? ReadPackageCell(string path, string cellReference, bool formula)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("XLSX workbook missing.");
        var workbookRoot = workbook.Workbook ?? throw new InvalidDataException("XLSX workbook root missing.");
        var sheet = workbookRoot.Sheets?.Elements<Sheet>().FirstOrDefault()
            ?? throw new InvalidDataException("XLSX worksheet missing.");
        var worksheetPart = (WorksheetPart)workbook.GetPartById(sheet.Id!);
        var worksheet = worksheetPart.Worksheet ?? throw new InvalidDataException("XLSX worksheet root missing.");
        var cell = worksheet.Descendants<Cell>()
            .FirstOrDefault(item => string.Equals(item.CellReference?.Value, cellReference, StringComparison.OrdinalIgnoreCase));
        if (cell is null) return null;
        if (formula) return cell.CellFormula?.Text is { } expression ? "=" + expression : null;
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(cell.CellValue?.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            return workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().ElementAtOrDefault(index)?.InnerText;
        if (cell.DataType?.Value == CellValues.InlineString) return cell.InlineString?.InnerText;
        if (cell.DataType?.Value == CellValues.Boolean) return cell.CellValue?.Text == "1" ? "TRUE" : "FALSE";
        return cell.CellValue?.Text;
    }

    internal static bool IsPackageRangeEmpty(string path, string rangeReference)
    {
        var bounds = rangeReference.Replace("$", string.Empty, StringComparison.Ordinal).Split(':', 2);
        if (!DesktopExcelCellIntentDetector.TryReadCoordinates(bounds[0], out var firstColumn, out var firstRow) ||
            !DesktopExcelCellIntentDetector.TryReadCoordinates(bounds.Length == 2 ? bounds[1] : bounds[0], out var lastColumn, out var lastRow))
            return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = SpreadsheetDocument.Open(stream, false);
        var worksheet = document.WorkbookPart?.WorksheetParts.FirstOrDefault()?.Worksheet;
        if (worksheet is null) return false;
        foreach (var cell in worksheet.Descendants<Cell>())
        {
            if (cell.CellReference?.Value is not { } reference ||
                !DesktopExcelCellIntentDetector.TryReadCoordinates(reference, out var column, out var row) ||
                column < firstColumn || column > lastColumn || row < firstRow || row > lastRow)
                continue;
            if (cell.CellFormula is not null || cell.CellValue is not null ||
                !string.IsNullOrEmpty(cell.InlineString?.InnerText))
                return false;
        }
        return true;
    }

    internal static bool PackageContainsText(string path, string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("XLSX workbook missing.");
        var workbookRoot = workbook.Workbook ?? throw new InvalidDataException("XLSX workbook root missing.");
        var sheet = workbookRoot.Sheets?.Elements<Sheet>().FirstOrDefault()
            ?? throw new InvalidDataException("XLSX worksheet missing.");
        var worksheet = ((WorksheetPart)workbook.GetPartById(sheet.Id!)).Worksheet
            ?? throw new InvalidDataException("XLSX worksheet root missing.");
        foreach (var cell in worksheet.Descendants<Cell>())
        {
            string? value;
            if (cell.DataType?.Value == CellValues.SharedString &&
                int.TryParse(cell.CellValue?.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                value = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().ElementAtOrDefault(index)?.InnerText;
            else if (cell.DataType?.Value == CellValues.InlineString)
                value = cell.InlineString?.InnerText;
            else
                value = cell.CellValue?.Text;
            if (value?.Contains(text, StringComparison.Ordinal) == true) return true;
        }
        return false;
    }

    internal static void WritePackageCell(string path, string cellReference, string? value, bool formula = false)
    {
        using var document = SpreadsheetDocument.Open(path, true);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("XLSX workbook missing.");
        var workbookRoot = workbook.Workbook ?? throw new InvalidDataException("XLSX workbook root missing.");
        var sheet = workbookRoot.Sheets?.Elements<Sheet>().FirstOrDefault()
            ?? throw new InvalidDataException("XLSX worksheet missing.");
        var worksheetPart = (WorksheetPart)workbook.GetPartById(sheet.Id!);
        var worksheet = worksheetPart.Worksheet ?? throw new InvalidDataException("XLSX worksheet root missing.");
        var sheetData = worksheet.GetFirstChild<SheetData>()
            ?? throw new InvalidDataException("XLSX sheet data missing.");
        var rowNumber = uint.Parse(Regex.Match(cellReference, @"\d+$").Value, CultureInfo.InvariantCulture);
        var row = sheetData.Elements<Row>().FirstOrDefault(item => item.RowIndex?.Value == rowNumber);
        if (row is null)
        {
            row = new Row { RowIndex = rowNumber };
            sheetData.Append(row);
        }
        var cell = row.Elements<Cell>().FirstOrDefault(item => item.CellReference?.Value == cellReference);
        if (value is null)
        {
            cell?.Remove();
        }
        else
        {
            cell ??= row.AppendChild(new Cell { CellReference = cellReference });
            if (formula)
            {
                cell.DataType = null;
                cell.CellFormula = new CellFormula(value.TrimStart('='));
                cell.CellValue = null;
                cell.InlineString = null;
            }
            else
            {
                cell.DataType = CellValues.InlineString;
                cell.CellFormula = null;
                cell.CellValue = null;
                cell.InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve });
            }
        }
        worksheet.Save();
    }

    private static void CreateEmptyPackage(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData());
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "工作表1",
        });
        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.Save();
    }

    private static void ValidatePackage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbook = document.WorkbookPart?.Workbook ?? throw new InvalidDataException("XLSX workbook missing.");
        if (workbook.Sheets?.Elements<Sheet>().Any() != true) throw new InvalidDataException("XLSX worksheet missing.");
    }

    private static bool EquivalentCellValues(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;
        return decimal.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var leftNumber) &&
               decimal.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var rightNumber) &&
               leftNumber == rightNumber;
    }

    private static string ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.EndsWith(' ')) throw InvalidName();
        var name = value.Normalize(NormalizationForm.FormKC).Trim();
        if (name != Path.GetFileName(name) || name.Length > 120 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) throw InvalidName();
        return name;
    }

    private static DesktopDocumentWorkflowException InvalidName() =>
        new("document_name_invalid", "Excel 工作簿名称无效，请使用普通文件名和 .xlsx 扩展名。" );
}

internal sealed class WindowsExcelAutomationService : IDesktopExcelAutomationService, IDisposable
{
    private const int ShowMaximized = 3;
    private const int ShowRestored = 9;
    private const int ExcelWindowMaximized = -4137;
    private const uint SetWindowNoSize = 0x0001;
    private const uint SetWindowNoMove = 0x0002;
    private const uint SetWindowShow = 0x0040;
    private static readonly nint WindowTopMost = new(-1);
    private static readonly nint WindowNotTopMost = new(-2);
    private static readonly TimeSpan WorkbookReadyTimeout = TimeSpan.FromSeconds(6);
    private static readonly string[] SpreadsheetProgIds = ["KET.Application", "Excel.Application"];
    private readonly BlockingCollection<Action> work = new();
    private readonly Dictionary<string, RunningWorkbook> openWorkbooks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread worker;
    private int disposed;

    public WindowsExcelAutomationService()
    {
        worker = new Thread(RunWorker) { IsBackground = true, Name = "Velia Excel Automation" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public Task<bool> OpenAsync(string fullPath, CancellationToken cancellationToken) =>
        RunStaAsync(() => Open(fullPath, cancellationToken));

    public Task<string?> TryGetCurrentWorkbookPathAsync(CancellationToken cancellationToken) =>
        RunStaAsync(() => TryGetCurrentWorkbookPath(cancellationToken));

    public Task<DesktopExcelAutomationResult> ExecuteAsync(
        DesktopExcelCellCommand command,
        string fullPath,
        CancellationToken cancellationToken) => RunStaAsync(() => Execute(command, fullPath, cancellationToken));

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
        if (openWorkbooks.TryGetValue(key, out var existing))
        {
            if (BringSpreadsheetWindowToForeground(existing.Application, existing.Workbook, fullPath)) return true;
            RemoveOpenWorkbook(fullPath);
        }

        foreach (var progId in SpreadsheetProgIds)
        {
            var application = TryGetRunningApplication(progId);
            if (application is null) continue;
            var workbook = TryFindOpenWorkbook(application, fullPath);
            if (workbook is not null)
            {
                if (BringSpreadsheetWindowToForeground(application, workbook, fullPath))
                {
                    openWorkbooks[key] = new RunningWorkbook(application, workbook);
                    return true;
                }
                Release(workbook);
                Release(application);
                continue;
            }
            Release(application);
        }

        var visibleWindow = FindSpreadsheetWindow(fullPath);
        if (visibleWindow is not null and not 0)
        {
            _ = RestoreMaximizeAndForeground(visibleWindow.Value);
            if (IsWindowVisible(visibleWindow.Value) && !IsIconic(visibleWindow.Value)) return true;
        }

        foreach (var progId in SpreadsheetProgIds)
        {
            object? applicationObject = null;
            object? workbooksObject = null;
            object? workbookObject = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var applicationType = Type.GetTypeFromProgID(progId, throwOnError: false);
                if (applicationType is null) continue;
                applicationObject = Activator.CreateInstance(applicationType);
                if (applicationObject is null) continue;
                dynamic application = applicationObject;
                application.Visible = true;
                workbooksObject = application.Workbooks;
                dynamic workbooks = workbooksObject;
                workbookObject = workbooks.Open(fullPath);
                // The COM window handle is the strongest workbook identity.
                // Modern WPS can host spreadsheet tabs in a unified `wps`
                // process whose top-level title updates after the workbook is
                // already editable. Do not discard a verified COM binding just
                // because the title-based fallback has not caught up yet.
                var activated = BringSpreadsheetWindowToForeground(applicationObject, workbookObject, fullPath);
                if (!activated)
                {
                    if (FindSpreadsheetWindow(fullPath) is not { } openedWindow ||
                        !IsWindowVisible(openedWindow)) return false;
                }
                openWorkbooks[key] = new RunningWorkbook(applicationObject, workbookObject);
                applicationObject = null;
                workbookObject = null;
                return true;
            }
            catch (COMException) { }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            finally
            {
                Release(workbookObject);
                Release(workbooksObject);
                Release(applicationObject);
            }
        }
        return false;
    }

    private string? TryGetCurrentWorkbookPath(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var foregroundWindow = GetForegroundWindow();
        var requireForegroundMatch = IsSpreadsheetWindow(foregroundWindow);
        foreach (var opened in openWorkbooks.Values.Reverse())
        {
            if (TryReadEditableWorkbookPath(opened.Workbook, out var cachedPath) &&
                (!requireForegroundMatch || FindSpreadsheetWindow(cachedPath) == foregroundWindow))
                return cachedPath;
        }

        foreach (var progId in SpreadsheetProgIds)
        {
            object? applicationObject = null;
            object? workbookObject = null;
            try
            {
                applicationObject = TryGetRunningApplication(progId);
                if (applicationObject is null) continue;
                dynamic application = applicationObject;
                workbookObject = application.ActiveWorkbook;
                if (workbookObject is null || !TryReadEditableWorkbookPath(workbookObject, out var fullPath))
                    continue;
                if (requireForegroundMatch && FindSpreadsheetWindow(fullPath) != foregroundWindow)
                    continue;
                var key = NormalizePath(fullPath);
                if (!openWorkbooks.ContainsKey(key))
                {
                    openWorkbooks[key] = new RunningWorkbook(applicationObject, workbookObject);
                    applicationObject = null;
                    workbookObject = null;
                }
                return fullPath;
            }
            catch (COMException) { }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            finally
            {
                Release(workbookObject);
                Release(applicationObject);
            }
        }
        return null;
    }

    private static bool IsSpreadsheetWindow(nint window)
    {
        if (window == 0 || GetWindowThreadProcessId(window, out var processId) == 0) return false;
        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            return process.ProcessName.Equals("et", StringComparison.OrdinalIgnoreCase) ||
                   process.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) ||
                   process.ProcessName.Equals("wps", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool TryReadEditableWorkbookPath(object workbookObject, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            dynamic workbook = workbookObject;
            if ((bool)workbook.ReadOnly) return false;
            var candidate = (string)workbook.FullName;
            if (string.IsNullOrWhiteSpace(candidate) ||
                !candidate.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
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

    private DesktopExcelAutomationResult Execute(
        DesktopExcelCellCommand command,
        string fullPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var opened = WaitForOpenWorkbook(fullPath, cancellationToken);
        if (opened is null)
            throw new DesktopDocumentWorkflowException("spreadsheet_window_mismatch", $"表格程序中没有打开“{Path.GetFileName(fullPath)}”。", true);
        object? worksheetsObject = null;
        object? worksheetObject = null;
        object? cellObject = null;
        try
        {
            dynamic workbook = opened.Workbook;
            string? storedValue = null;
            if (command.Action is DesktopExcelWorkbookEditActions.SetCell or
                DesktopExcelWorkbookEditActions.ClearCell or
                DesktopExcelWorkbookEditActions.ClearRange or
                DesktopExcelWorkbookEditActions.ClearSelection or
                DesktopExcelWorkbookEditActions.ClearSheet or
                DesktopExcelWorkbookEditActions.DeleteText)
            {
                worksheetsObject = workbook.Worksheets;
                dynamic worksheets = worksheetsObject;
                worksheetObject = worksheets.Item(1);
                dynamic worksheet = worksheetObject;
                if (command.Action == DesktopExcelWorkbookEditActions.ClearSelection)
                {
                    dynamic application = opened.Application;
                    cellObject = application.Selection;
                }
                else if (command.Action is DesktopExcelWorkbookEditActions.ClearSheet or
                         DesktopExcelWorkbookEditActions.DeleteText)
                {
                    cellObject = worksheet.UsedRange;
                }
                else
                {
                    cellObject = worksheet.Range[command.CellReference!];
                }
                dynamic cell = cellObject;
                if (command.Action is DesktopExcelWorkbookEditActions.ClearCell or
                    DesktopExcelWorkbookEditActions.ClearRange or
                    DesktopExcelWorkbookEditActions.ClearSelection or
                    DesktopExcelWorkbookEditActions.ClearSheet)
                {
                    if (cellObject is null)
                        throw new DesktopDocumentWorkflowException("spreadsheet_selection_missing", "当前表格没有可清空的选中区域。", true);
                    if (command.Action == DesktopExcelWorkbookEditActions.ClearSelection)
                        EnsureRangeBelongsToWorkbook(cellObject, opened.Workbook);
                    cell.ClearContents();
                }
                else if (command.Action == DesktopExcelWorkbookEditActions.DeleteText)
                {
                    if (string.IsNullOrWhiteSpace(command.Value))
                        throw new DesktopDocumentWorkflowException("spreadsheet_text_invalid", "需要说明要删除的具体文字。" );
                    var replaced = Convert.ToBoolean(
                        cell.Replace(command.Value, string.Empty, 2, 1, false, false, false, false),
                        CultureInfo.InvariantCulture);
                    if (!replaced)
                        throw new DesktopDocumentWorkflowException("document_text_not_found", $"当前表格中没有找到“{command.Value}”。" );
                }
                else if (command.IsFormula)
                {
                    cell.Formula = command.Value;
                }
                else
                {
                    cell.Value2 = command.Value;
                }
                workbook.Save();
                var cleared = command.Action is DesktopExcelWorkbookEditActions.ClearCell or
                    DesktopExcelWorkbookEditActions.ClearRange or
                    DesktopExcelWorkbookEditActions.ClearSelection or
                    DesktopExcelWorkbookEditActions.ClearSheet;
                var reference = NormalizeRangeAddress(Convert.ToString(cell.Address, CultureInfo.InvariantCulture));
                storedValue = command.Action == DesktopExcelWorkbookEditActions.DeleteText
                    ? command.Value
                    : cleared
                    ? Convert.ToString(cell.Value2, CultureInfo.InvariantCulture)
                    : command.IsFormula
                        ? Convert.ToString(cell.Formula, CultureInfo.InvariantCulture)
                        : Convert.ToString(cell.Value2, CultureInfo.InvariantCulture);
                if (cleared && !IsEmptyRangeValue(cell.Value2))
                    throw new DesktopDocumentWorkflowException("spreadsheet_edit_unverified", "表格没有确认内容已经清空。", true);
                if (command.Action == DesktopExcelWorkbookEditActions.DeleteText && ContainsText(cell.Value2, command.Value!))
                    throw new DesktopDocumentWorkflowException("spreadsheet_edit_unverified", "表格没有确认指定文字已经删除。", true);
                if (cleared || command.Action == DesktopExcelWorkbookEditActions.DeleteText)
                    command = command with { CellReference = reference };
            }
            else
            {
                workbook.Save();
            }
            var closed = command.Action == DesktopExcelWorkbookEditActions.Close;
            if (closed) workbook.Close(true);
            var result = new DesktopExcelAutomationResult(
                true, command.Action, command.CellReference, storedValue, closed);
            if (closed) RemoveOpenWorkbook(fullPath);
            return result;
        }
        catch (COMException exception)
        {
            RemoveOpenWorkbook(fullPath);
            throw new DesktopDocumentWorkflowException(
                "spreadsheet_window_mismatch", $"表格程序中没有打开“{Path.GetFileName(fullPath)}”。", true, false, exception);
        }
        finally
        {
            Release(cellObject);
            Release(worksheetObject);
            Release(worksheetsObject);
        }
    }

    private static void EnsureRangeBelongsToWorkbook(object rangeObject, object workbookObject)
    {
        object? worksheetObject = null;
        object? parentWorkbookObject = null;
        try
        {
            dynamic range = rangeObject;
            worksheetObject = range.Worksheet;
            dynamic worksheet = worksheetObject;
            parentWorkbookObject = worksheet.Parent;
            dynamic parentWorkbook = parentWorkbookObject;
            dynamic workbook = workbookObject;
            if (!string.Equals(
                    NormalizePath((string)parentWorkbook.FullName),
                    NormalizePath((string)workbook.FullName),
                    StringComparison.OrdinalIgnoreCase))
                throw new DesktopDocumentWorkflowException("spreadsheet_window_mismatch", "当前选区不属于已绑定的 Excel 工作簿。", true);
        }
        finally
        {
            Release(parentWorkbookObject);
            Release(worksheetObject);
        }
    }

    private static string NormalizeRangeAddress(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("$", string.Empty, StringComparison.Ordinal).Replace(',', ':').Trim();

    private static bool IsEmptyRangeValue(object? value)
    {
        if (value is null) return true;
        if (value is Array values)
        {
            foreach (var item in values)
                if (!IsEmptyRangeValue(item)) return false;
            return true;
        }
        return string.IsNullOrEmpty(Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static bool ContainsText(object? value, string text)
    {
        if (value is null) return false;
        if (value is Array values)
        {
            foreach (var item in values)
                if (ContainsText(item, text)) return true;
            return false;
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture)?.Contains(text, StringComparison.Ordinal) == true;
    }

    private RunningWorkbook? WaitForOpenWorkbook(string fullPath, CancellationToken cancellationToken)
    {
        var key = NormalizePath(fullPath);
        if (openWorkbooks.TryGetValue(key, out var cached)) return cached;
        var deadline = DateTimeOffset.UtcNow + WorkbookReadyTimeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var progId in SpreadsheetProgIds)
            {
                var application = TryGetRunningApplication(progId);
                if (application is null) continue;
                var workbook = TryFindOpenWorkbook(application, fullPath);
                if (workbook is not null)
                {
                    var opened = new RunningWorkbook(application, workbook);
                    openWorkbooks[key] = opened;
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

    private static object? TryFindOpenWorkbook(object application, string fullPath)
    {
        object? workbooksObject = null;
        try
        {
            dynamic spreadsheet = application;
            workbooksObject = spreadsheet.Workbooks;
            dynamic workbooks = workbooksObject;
            for (var index = 1; index <= (int)workbooks.Count; index++)
            {
                object? candidateObject = null;
                try
                {
                    candidateObject = workbooks.Item(index);
                    dynamic candidate = candidateObject;
                    if (!string.Equals(NormalizePath((string)candidate.FullName), NormalizePath(fullPath), StringComparison.OrdinalIgnoreCase))
                        continue;
                    var result = candidateObject;
                    candidateObject = null;
                    return result;
                }
                finally
                {
                    Release(candidateObject);
                }
            }
        }
        catch (COMException) { }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally
        {
            Release(workbooksObject);
        }
        return null;
    }

    private static bool BringSpreadsheetWindowToForeground(object applicationObject, object workbookObject, string fullPath)
    {
        object? windowObject = null;
        nint directHandle = 0;
        var activated = false;
        try
        {
            dynamic application = applicationObject;
            dynamic workbook = workbookObject;
            application.Visible = true;
            workbook.Activate();
            windowObject = application.ActiveWindow;
            dynamic window = windowObject;
            window.WindowState = ExcelWindowMaximized;
            window.Activate();
            directHandle = TryGetComWindowHandle(windowObject, applicationObject);
            activated = true;
        }
        catch (COMException) { }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally
        {
            Release(windowObject);
        }
        if (!activated) return false;
        if (directHandle is not 0)
        {
            var root = GetAncestor(directHandle, 2);
            if (root is not 0) directHandle = root;
            if (RestoreMaximizeAndForeground(directHandle)) return true;
        }
        var deadline = DateTimeOffset.UtcNow + WorkbookReadyTimeout;
        do
        {
            var handle = FindSpreadsheetWindow(fullPath);
            if (handle is not null and not 0) return RestoreMaximizeAndForeground(handle.Value);
            if (DateTimeOffset.UtcNow >= deadline)
            {
                if (string.Equals(Environment.GetEnvironmentVariable("VELIA_RUN_EXCEL_SMOKE"), "1", StringComparison.Ordinal))
                    Console.Error.WriteLine($"Excel smoke window diagnostics: {DescribeSpreadsheetWindows()}" );
                return false;
            }
            Thread.Sleep(100);
        }
        while (true);
    }

    private static nint TryGetComWindowHandle(object windowObject, object applicationObject)
    {
        foreach (var candidate in new[] { windowObject, applicationObject })
        {
            try
            {
                dynamic value = candidate;
                var handle = Convert.ToInt64(value.Hwnd, CultureInfo.InvariantCulture);
                if (handle != 0) return new nint(handle);
            }
            catch (COMException) { }
            catch (OverflowException) { }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        }
        return 0;
    }

    private static nint? FindSpreadsheetWindow(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath).Normalize(NormalizationForm.FormKC);
        var stem = Path.GetFileNameWithoutExtension(fullPath).Normalize(NormalizationForm.FormKC);
        nint? found = null;
        _ = EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowTextLength(window) <= 0) return true;
            var length = GetWindowTextLength(window);
            var buffer = new char[Math.Min(1_024, length + 1)];
            var copied = GetWindowText(window, buffer, buffer.Length);
            if (copied <= 0) return true;
            var title = new string(buffer, 0, copied).Normalize(NormalizationForm.FormKC);
            if (!title.Contains(fileName, StringComparison.OrdinalIgnoreCase) &&
                !title.Contains(stem, StringComparison.OrdinalIgnoreCase)) return true;
            if (GetWindowThreadProcessId(window, out var processId) == 0) return true;
            try
            {
                using var process = Process.GetProcessById(unchecked((int)processId));
                if (!process.ProcessName.Equals("et", StringComparison.OrdinalIgnoreCase) &&
                    !process.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) &&
                    !process.ProcessName.Equals("wps", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
            found = window;
            return false;
        }, 0);
        return found;
    }

    private static string DescribeSpreadsheetWindows()
    {
        var descriptions = new List<string>();
        _ = EnumWindows((window, _) =>
        {
            if (GetWindowThreadProcessId(window, out var processId) == 0) return true;
            string processName;
            try
            {
                using var process = Process.GetProcessById(unchecked((int)processId));
                processName = process.ProcessName;
            }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
            if (!processName.Equals("et", StringComparison.OrdinalIgnoreCase) &&
                !processName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) &&
                !processName.Equals("wps", StringComparison.OrdinalIgnoreCase)) return true;
            var length = GetWindowTextLength(window);
            var buffer = new char[Math.Min(1_024, Math.Max(1, length + 1))];
            var copied = GetWindowText(window, buffer, buffer.Length);
            var title = copied > 0 ? new string(buffer, 0, copied) : string.Empty;
            descriptions.Add($"{processName}:{window}:visible={IsWindowVisible(window)}:title={title}");
            return true;
        }, 0);
        return string.Join(" | ", descriptions);
    }

    private static bool RestoreMaximizeAndForeground(nint window)
    {
        _ = ShowWindow(window, ShowRestored);
        _ = ShowWindow(window, ShowMaximized);
        _ = SetWindowPos(window, WindowTopMost, 0, 0, 0, 0, SetWindowNoSize | SetWindowNoMove | SetWindowShow);
        _ = SetWindowPos(window, WindowNotTopMost, 0, 0, 0, 0, SetWindowNoSize | SetWindowNoMove | SetWindowShow);
        _ = BringWindowToTop(window);
        _ = SetForegroundWindow(window);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (IsWindowVisible(window) && !IsIconic(window) && IsZoomed(window)) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private void RemoveOpenWorkbook(string fullPath)
    {
        if (!openWorkbooks.Remove(NormalizePath(fullPath), out var opened)) return;
        Release(opened.Workbook);
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
            foreach (var opened in openWorkbooks.Values)
            {
                Release(opened.Workbook);
                Release(opened.Application);
            }
            openWorkbooks.Clear();
        }
    }

    private Task<T> RunStaAsync<T>(Func<T> action)
    {
        if (Volatile.Read(ref disposed) != 0)
            return Task.FromException<T>(new ObjectDisposedException(nameof(WindowsExcelAutomationService)));
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
            completion.SetException(new ObjectDisposedException(nameof(WindowsExcelAutomationService)));
        }
        return completion.Task;
    }

    private static string NormalizePath(string value) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));

    private static object? TryGetRunningApplication(string progId)
    {
        if (CLSIDFromProgID(progId, out var classId) != 0 ||
            GetActiveObject(ref classId, 0, out var instance) != 0 || instance is null)
            return null;
        return instance;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

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
    private static extern nint GetForegroundWindow();

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
    private static extern nint GetAncestor(nint window, uint flags);

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

    private sealed record RunningWorkbook(object Application, object Workbook);
}
