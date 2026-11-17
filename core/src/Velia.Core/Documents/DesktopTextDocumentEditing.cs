using System.Text;
using System.Text.RegularExpressions;

namespace Velia.Core.Documents;

internal static class DesktopTextDocumentEditActions
{
    public const string Append = "append";
    public const string SetLine = "setLine";
    public const string InsertLine = "insertLine";
    public const string DeleteLine = "deleteLine";
    public const string Replace = "replace";
    public const string Delete = "delete";
    public const string DeleteSelection = "deleteSelection";
    public const string Clear = "clear";
    public const string Clean = "clean";
    public const string Save = "save";
    public const string Close = "close";
}

internal static class DesktopTextDocumentBindings
{
    // This is an internal binding token, never a disk path. It lets the same
    // verified editor pipeline operate on a new unsaved Notepad window without
    // pretending that Ctrl+S persisted a file.
    public const string UntitledNotepad = "__velia_untitled_notepad__.txt";
    public const string UntitledNotepadDisplayName = "未保存的记事本";
}

internal sealed record DesktopTextDocumentEditCommand(
    string Action,
    string? Text = null,
    string? Match = null,
    int? LineNumber = null);

internal sealed record DesktopTextEditorResult(
    bool Verified,
    string Action,
    string Content,
    string WindowTitle,
    bool Closed = false,
    bool Foregrounded = true,
    bool Persisted = true);

internal sealed record DesktopTextDocumentEditResult(
    bool Verified,
    string Action,
    string FileName,
    int CharacterCount,
    bool Closed,
    bool Persisted = true);

internal interface IDesktopTextEditorService
{
    Task<string?> TryGetCurrentTextDocumentFileNameAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    Task<DesktopTextEditorResult> ExecuteTextDocumentAsync(
        DesktopTextDocumentEditCommand command,
        string expectedFileName,
        CancellationToken cancellationToken);
}

internal interface IDesktopTextDocumentWorkflowService
{
    Task<string?> TryBindCurrentAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    Task<DesktopTextDocumentEditResult> ExecuteAsync(
        DesktopTextDocumentEditCommand command,
        string fileName,
        CancellationToken cancellationToken);
}

internal static partial class DesktopTextDocumentEditIntentDetector
{
    private static readonly string[] DeniedTerms = ["不要", "别", "禁止", "无需", "不用"];
    private static readonly string[] InstructionalPrefixes = ["怎么", "如何", "为什么", "能不能", "是否"];
    private static readonly string[] DerivedContentTerms = ["题目", "正确答案", "计算式", "计算方式", "根据当前", "根据屏幕"];

    public static bool TryParseExplicitCommand(string request, out DesktopTextDocumentEditCommand command)
    {
        command = null!;
        if (string.IsNullOrWhiteSpace(request)) return false;
        var value = request.Normalize(NormalizationForm.FormKC).Trim().TrimEnd('。', '！', '!', '？', '?');
        if (IsDeniedOrInstructional(value) || DerivedContentTerms.Any(value.Contains) || value.Length > 1_200) return false;
        var compact = Regex.Replace(value, @"\s+", string.Empty, RegexOptions.CultureInvariant);

        if (CleanPattern().IsMatch(compact))
        {
            command = new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Clean);
            return true;
        }

        if (DeleteSelectionPattern().IsMatch(compact) || DeleteSelectionObjectPattern().IsMatch(compact))
        {
            command = new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.DeleteSelection);
            return true;
        }

        if (ClearPattern().IsMatch(compact))
        {
            command = new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Clear);
            return true;
        }

        var lineInsert = InsertLinePattern().Match(value);
        if (TryCreateLineCommand(
                lineInsert,
                DesktopTextDocumentEditActions.InsertLine,
                out command))
            return true;

        var lineSet = SetLinePattern().Match(value);
        if (!lineSet.Success) lineSet = ReplaceLinePattern().Match(value);
        if (TryCreateLineCommand(
                lineSet,
                DesktopTextDocumentEditActions.SetLine,
                out command))
            return true;

        var lineDelete = DeleteLinePattern().Match(value);
        if (!lineDelete.Success) lineDelete = DeleteLineObjectPattern().Match(value);
        if (lineDelete.Success &&
            TryParseLineNumber(lineDelete.Groups["line"].Value, out var deletedLine))
        {
            command = new DesktopTextDocumentEditCommand(
                DesktopTextDocumentEditActions.DeleteLine,
                LineNumber: deletedLine);
            return true;
        }

        var replace = ReplacePattern().Match(value);
        if (replace.Success)
        {
            var before = CleanText(replace.Groups["before"].Value);
            var after = CleanText(replace.Groups["after"].Value);
            if (ValidText(before) && ValidText(after))
            {
                command = new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Replace, after, before);
                return true;
            }
        }

        var delete = DeletePattern().Match(value);
        if (!delete.Success) delete = DeleteObjectPattern().Match(value);
        if (delete.Success)
        {
            var target = CleanText(delete.Groups["text"].Value);
            if (ValidText(target))
            {
                command = new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Delete, Match: target);
                return true;
            }
        }

        var append = AppendPattern().Match(value);
        if (!append.Success) append = AppendToEndPattern().Match(value);
        if (append.Success)
        {
            var text = CleanText(append.Groups["text"].Value);
            if (ValidText(text))
            {
                command = new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Append, text);
                return true;
            }
        }

        if (DesktopDocumentLifecycleIntentDetector.TryParse(value, out var lifecycle))
        {
            command = new DesktopTextDocumentEditCommand(lifecycle.Action);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Parses natural document continuations only after the caller has proved
    /// that the foreground/active workspace is a writable text or Word
    /// document. Keeping this separate from the explicit grammar prevents a
    /// bare “输入……” from being redirected away from chat/search boxes.
    /// </summary>
    public static bool TryParseContextualCommand(
        string request,
        out DesktopTextDocumentEditCommand command)
    {
        if (TryParseExplicitCommand(request, out command)) return true;

        command = null!;
        if (string.IsNullOrWhiteSpace(request)) return false;
        var rawValue = request.Normalize(NormalizationForm.FormC)
            .Trim()
            .TrimEnd('。', '！', '!', '？', '?');
        var value = rawValue.Normalize(NormalizationForm.FormKC);
        if (IsDeniedOrInstructional(value) ||
            DerivedContentTerms.Any(value.Contains) ||
            value.Length > 1_200)
            return false;

        foreach (var pattern in new[] { ContextualAppendPattern(), ContextualObjectAppendPattern() })
        {
            var match = pattern.Match(rawValue);
            if (!match.Success) continue;
            var text = CleanText(match.Groups["text"].Value);
            if (!ValidText(text)) return false;
            command = new DesktopTextDocumentEditCommand(
                DesktopTextDocumentEditActions.Append,
                text);
            return true;
        }

        if (DesktopDocumentLifecycleIntentDetector.TryParse(
                value,
                out var lifecycle,
                allowImplicitCurrentDocument: true))
        {
            command = new DesktopTextDocumentEditCommand(lifecycle.Action);
            return true;
        }
        return false;
    }

    public static string GetCanonicalKey(DesktopTextDocumentEditCommand command) =>
        $"document-edit:{command.Action}:{command.LineNumber}:{NormalizeKey(command.Match)}:{NormalizeKey(command.Text)}";

    private static bool TryCreateLineCommand(
        Match match,
        string action,
        out DesktopTextDocumentEditCommand command)
    {
        command = null!;
        if (!match.Success ||
            !TryParseLineNumber(match.Groups["line"].Value, out var lineNumber))
            return false;
        var text = CleanText(match.Groups["text"].Value);
        if (!ValidLineText(text)) return false;
        command = new DesktopTextDocumentEditCommand(
            action,
            text,
            LineNumber: lineNumber);
        return true;
    }

    private static bool IsDeniedOrInstructional(string value) =>
        DeniedTerms.Any(value.Contains) || InstructionalPrefixes.Any(value.StartsWith);

    private static bool ValidText(string value) => value.Length is >= 1 and <= 512 &&
        !value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));

    private static bool ValidLineText(string value) =>
        ValidText(value) && value.IndexOfAny(['\r', '\n']) < 0;

    private static bool TryParseLineNumber(string value, out int lineNumber)
    {
        var token = Regex.Replace(value, @"\s+", string.Empty, RegexOptions.CultureInvariant);
        if (int.TryParse(token, out lineNumber)) return lineNumber is >= 1 and <= 10_000;
        if (token.Length == 0)
        {
            lineNumber = 0;
            return false;
        }

        if (!token.Any(character => character is '十' or '百' or '千' or '万'))
        {
            long digits = 0;
            foreach (var character in token)
            {
                if (!TryGetChineseDigit(character, out var digit))
                {
                    lineNumber = 0;
                    return false;
                }
                digits = digits * 10 + digit;
                if (digits > 10_000)
                {
                    lineNumber = 0;
                    return false;
                }
            }
            lineNumber = (int)digits;
            return lineNumber >= 1;
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
                lineNumber = 0;
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
        if (parsed is < 1 or > 10_000)
        {
            lineNumber = 0;
            return false;
        }
        lineNumber = (int)parsed;
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

    private static string CleanText(string value) =>
        value.Trim(' ', '：', ':', '“', '”', '"', '‘', '’');

    private static string NormalizeKey(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"[\s\p{P}\p{S}]", string.Empty);

    [GeneratedRegex("""^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:把|将)\s*(?:当前)?\s*(?:(?:文档|记事本|Word\s*文档|Word|WPS\s*文档)\s*)?(?:(?:里的|里|中的|内的|的)\s*)?[“"]?(?<before>[^“”"，。]{1,200})[”"]?\s*(?:替换成|替换为|改成|改为)[“"]?(?<after>[^“”"，。]{1,200})[”"]?$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex ReplacePattern();

    [GeneratedRegex("""^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:删除|删掉|去掉|移除)(?:一下)?\s*(?:当前)?\s*(?:(?:文档|记事本|Word\s*文档|Word|WPS\s*文档)\s*)?(?:(?:里的|里|中的|内的|的)\s*)?[“"]?(?<text>[^“”"，。]{1,200})[”"]?$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex DeletePattern();

    [GeneratedRegex("""^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:把|将)\s*(?:当前)?\s*(?:(?:文档|记事本|Word\s*文档|Word|WPS\s*文档)\s*)?(?:(?:里的|里|中的|内的|的)\s*)?[“"]?(?<text>[^“”"，。]{1,200})[”"]?\s*(?:删除|删掉|去掉|移除)(?:一下)?$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex DeleteObjectPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:删除|删掉|去掉|移除)(?:一下)?(?:当前|刚才)?(?:选中|选定|选择)(?:的)?(?:文字|文本|内容|部分)?$", RegexOptions.CultureInvariant, 50)]
    private static partial Regex DeleteSelectionPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙)\s*)*(?:把|将)(?:当前|刚才)?(?:选中|选定|选择)(?:的)?(?:文字|文本|内容|部分)?(?:删除|删掉|去掉|移除)(?:一下)?$", RegexOptions.CultureInvariant, 50)]
    private static partial Regex DeleteSelectionObjectPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在)\s*)*(?:清空|清除|删除)(?:一下)?(?:当前)?(?:整个|全部|所有)?(?:文档|记事本|Word文档|Word|WPS文档)(?:里的|里|中的|内的|的)?(?:全部|所有|全部的|所有的)?(?:文字|文本|内容)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex ClearPattern();

    [GeneratedRegex(@"^(?=.*(?:作者|署名|广告|推广|推荐|页脚))(?=.*(?:只保留正文|清理文档|清理正文)).*$", RegexOptions.CultureInvariant, 50)]
    private static partial Regex CleanPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:在)?\s*(?:当前)?\s*(?:(?:文档|记事本|Word\s*文档|Word|WPS\s*文档)\s*(?:里的|里|中的|中|内的|内|的)?\s*)?第?\s*(?<line>\d{1,5}|[零〇一二两三四五六七八九十百千万]{1,16})\s*行\s*(?:之前|前|位置|处)?\s*(?:插入|新增|加一行)\s*[：:]?\s*(?<text>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex InsertLinePattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:在)?\s*(?:当前)?\s*(?:(?:文档|记事本|Word\s*文档|Word|WPS\s*文档)\s*(?:里的|里|中的|中|内的|内|的)?\s*)?第?\s*(?<line>\d{1,5}|[零〇一二两三四五六七八九十百千万]{1,16})\s*行\s*(?:中|里|上|的内容)?\s*(?:输入|写入|写上|填写|填入|添加|设为|设置为)\s*[：:]?\s*(?<text>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex SetLinePattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:把|将)?\s*(?:当前)?\s*(?:(?:文档|记事本|Word\s*文档|Word|WPS\s*文档)\s*(?:里的|里|中的|中|内的|内|的)?\s*)?第?\s*(?<line>\d{1,5}|[零〇一二两三四五六七八九十百千万]{1,16})\s*行\s*(?:的)?\s*(?:文字|文本|内容)?\s*(?:替换成|替换为|改成|改为|设为|设置为)\s*[：:]?\s*(?<text>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex ReplaceLinePattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:删除|删掉|去掉|移除)(?:一下)?\s*(?:当前)?\s*(?:(?:文档|记事本|Word\s*文档|Word|WPS\s*文档)\s*(?:里的|里|中的|中|内的|内|的)?\s*)?第?\s*(?<line>\d{1,5}|[零〇一二两三四五六七八九十百千万]{1,16})\s*行\s*(?:的)?\s*(?:文字|文本|内容|整行)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex DeleteLinePattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:把|将)\s*(?:当前)?\s*(?:(?:文档|记事本|Word\s*文档|Word|WPS\s*文档)\s*(?:里的|里|中的|中|内的|内|的)?\s*)?第?\s*(?<line>\d{1,5}|[零〇一二两三四五六七八九十百千万]{1,16})\s*行\s*(?:的)?\s*(?:文字|文本|内容|整行)?\s*(?:删除|删掉|去掉|移除)(?:一下)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex DeleteLineObjectPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:(?:在)?(?:当前)?(?:文档|记事本|Word文档|Word|WPS文档)?(?:末尾|未尾|最后(?:面)?|后面|中|里)|(?:在)?文末)\s*(?:输入|写入|写上|填写|添加|追加|加上)\s*[：:]?\s*(?<text>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex AppendPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\s*)*(?:写到|输入到|填写到|添加到|追加到|加到)\s*(?:当前)?\s*(?:文档|记事本|Word文档|Word|WPS文档)?\s*(?:末尾|未尾|文末|最后(?:面)?|后面)\s*[：:]?\s*(?<text>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex AppendToEndPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|给我|现在|然后|接着|继续|再)\s*)*(?:写入|写上|输入|填写|填入|添加|追加|记下)\s*(?:文字|文本|内容)?\s*[：:]?\s*(?<text>.+?)(?:\s*(?:到|进|进入|放到|放进)\s*(?:(?:当前|这个|刚才)(?:的)?)?(?:文档|记事本|里面|里|中))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex ContextualAppendPattern();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|给我|现在|然后|接着|继续|再)\s*)*(?:把|将)\s*(?<text>.+?)\s*(?:写进去|写进|写入|写上|输入|填写|填入|添加|追加|记到|放入|放进)(?:到|进)?\s*(?:(?:当前|这个|刚才)(?:的)?)?(?:文档|记事本|里面|里|中)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex ContextualObjectAppendPattern();

}

internal static class DesktopTextDocumentLineEditor
{
    public static string SetLine(string content, int lineNumber, string text)
    {
        var lines = Split(content);
        while (lines.Count < lineNumber) lines.Add(string.Empty);
        lines[lineNumber - 1] = text;
        return string.Join('\n', lines);
    }

    public static string InsertLine(string content, int lineNumber, string text)
    {
        var lines = Split(content);
        while (lines.Count < lineNumber - 1) lines.Add(string.Empty);
        lines.Insert(lineNumber - 1, text);
        return string.Join('\n', lines);
    }

    public static string DeleteLine(string content, int lineNumber)
    {
        var lines = Split(content);
        if (lineNumber > lines.Count)
            throw new DesktopDocumentWorkflowException(
                "document_line_not_found",
                $"当前文档没有第 {lineNumber} 行。" );
        lines.RemoveAt(lineNumber - 1);
        return lines.Count == 0 ? string.Empty : string.Join('\n', lines);
    }

    private static List<string> Split(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
}

internal sealed class DesktopTextDocumentWorkflowService(
    IDesktopTextEditorService editor,
    string? desktopPath = null) : IDesktopTextDocumentWorkflowService
{
    private readonly string desktop = Path.GetFullPath(
        desktopPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

    public async Task<string?> TryBindCurrentAsync(CancellationToken cancellationToken)
    {
        var candidate = await editor.TryGetCurrentTextDocumentFileNameAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        if (string.Equals(
                candidate,
                DesktopTextDocumentBindings.UntitledNotepad,
                StringComparison.Ordinal))
            return candidate;
        var safeName = Path.GetFileName(candidate);
        if (!string.Equals(candidate, safeName, StringComparison.Ordinal) ||
            !safeName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(desktop, safeName)))
            return null;
        return safeName;
    }

    public async Task<DesktopTextDocumentEditResult> ExecuteAsync(
        DesktopTextDocumentEditCommand command,
        string fileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var isUntitledNotepad = string.Equals(
            fileName,
            DesktopTextDocumentBindings.UntitledNotepad,
            StringComparison.Ordinal);
        var safeName = Path.GetFileName(fileName);
        if (!isUntitledNotepad &&
            (safeName != fileName || !safeName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            throw new DesktopDocumentWorkflowException("document_binding_invalid", "当前没有已绑定的桌面文本文档。" );
        var path = Path.Combine(desktop, safeName);
        if (!isUntitledNotepad && !File.Exists(path))
            throw new DesktopDocumentWorkflowException("document_not_found", $"桌面上没有找到“{safeName}”。", true);

        var result = await editor.ExecuteTextDocumentAsync(command, safeName, cancellationToken).ConfigureAwait(false);
        if (!result.Verified)
            throw new DesktopDocumentWorkflowException("document_edit_unverified", "Windows 没有确认文档操作结果。", true);

        if (isUntitledNotepad)
        {
            return new DesktopTextDocumentEditResult(
                true,
                command.Action,
                DesktopTextDocumentBindings.UntitledNotepadDisplayName,
                result.Content.Length,
                result.Closed,
                Persisted: false);
        }

        {
            var expected = Normalize(result.Content);
            var verified = false;
            for (var attempt = 0; attempt < 12; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Normalize(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) == expected)
                {
                    verified = true;
                    break;
                }
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            if (!verified)
                throw new DesktopDocumentWorkflowException("document_save_unverified", "文档内容没有通过磁盘保存验证。", true);
        }
        return new DesktopTextDocumentEditResult(
            true,
            command.Action,
            safeName,
            result.Content.Length,
            result.Closed,
            result.Persisted);
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
