using System.Text;

namespace Velia.Core.Documents;

internal enum DesktopDocumentLifecycleTarget
{
    Generic,
    Text,
    Word,
    Excel,
}

internal sealed record DesktopDocumentLifecycleIntent(
    string Action,
    DesktopDocumentLifecycleTarget Target);

/// <summary>
/// One shared natural-language grammar for save/close commands across text,
/// Word and Excel documents. Format-specific detectors may use the target hint,
/// while generic commands are resolved against the verified foreground document.
/// </summary>
internal static class DesktopDocumentLifecycleIntentDetector
{
    private static readonly string[] DeniedTerms =
        ["不保存", "不要", "别", "禁止", "无需", "不用", "不许", "取消", "放弃修改", "放弃更改"];
    private static readonly string[] InstructionalPrefixes =
        ["怎么", "如何", "为什么", "能不能", "是否"];

    private static readonly string[] SaveTerms =
        ["保存修改", "保存更改", "保存起来", "保存一下", "保存好", "储存起来", "储存一下", "储存好", "存起来", "存一下", "存好", "保存", "储存"];
    private static readonly string[] CloseTerms =
        ["关闭一下", "退出一下", "退出来", "关掉", "关了", "关闭", "退出"];

    private static readonly string[] ExcelTargets =
        ["MicrosoftExcel", "微软Excel", "Excel表格", "Excel文档", "Excel文件", "易克赛尔表格", "埃克塞尔表格", "电子表格", "表格文件", "表格文档", "工作表", "工作簿", "XLSX文件", "XLSX", "Ex表格", "表格", "Excel"];
    private static readonly string[] WordTargets =
        ["MicrosoftWord", "微软Word", "Word文档", "Word文件", "Wod文档", "Wod文件", "WPS文档", "DOCX文档", "DOCX文件", "沃德文档", "Word", "Wod", "WPS", "DOCX", "沃德"];
    private static readonly string[] TextTargets =
        ["TXT记事本", "TXT文档", "TXT文件", "文本文档", "文本文件", "当前记事本", "这个记事本", "记事本", "TXT"];
    private static readonly string[] GenericTargets =
        ["正在打开的文档", "打开的文档", "当前的文档", "这个文档", "当前文档", "刚才的文档", "刚才文档", "文档", "当前的文件", "这个文件", "当前文件", "刚才的文件", "文件"];
    private static readonly string[] ExplicitDocumentTargets =
    [
        "Word文档", "Word文件", "Wod文档", "Wod文件", "WPS文档", "DOCX文档", "DOCX文件", "沃德文档",
        "Excel表格", "Excel文档", "Excel文件", "电子表格", "表格文件", "表格文档", "工作表", "工作簿", "XLSX文件", "Ex表格", "表格",
        "TXT记事本", "TXT文档", "TXT文件", "文本文档", "文本文件", "记事本",
    ];

    private static readonly string[] Fillers =
    [
        "麻烦帮我", "请帮我", "帮我", "麻烦", "帮忙", "请",
        "保存完成以后", "保存完成之后", "存完以后", "存完之后", "完成以后", "完成之后",
        "然后再", "接着再", "并且", "然后", "接着", "之后", "以后",
        "正在打开的", "打开的", "给我", "现在", "继续", "再把", "再将", "再",
        "把", "将", "先把", "先将", "当前的", "这个", "当前", "刚才的", "刚才",
        "完成以后", "完成之后", "完成后", "完以后", "完之后", "完成", "一下", "先", "就", "它", "并", "完", "后", "的", "好", "吧", "呀", "啊", "哦",
    ];

    public static bool TryParse(
        string request,
        out DesktopDocumentLifecycleIntent intent,
        bool allowImplicitCurrentDocument = false)
    {
        intent = null!;
        if (string.IsNullOrWhiteSpace(request)) return false;
        var normalized = request.Normalize(NormalizationForm.FormKC)
            .Trim()
            .TrimEnd('。', '！', '!', '？', '?', '，', ',', '；', ';');
        if (normalized.Length > 240 ||
            DeniedTerms.Any(normalized.Contains) ||
            InstructionalPrefixes.Any(normalized.StartsWith))
            return false;

        var compact = string.Concat(normalized.Where(static character =>
            !char.IsWhiteSpace(character) && !char.IsPunctuation(character) && !char.IsSymbol(character)));
        if (compact.Length == 0) return false;

        var hasSave = ContainsAny(compact, SaveTerms);
        var hasClose = ContainsAny(compact, CloseTerms);
        if (!hasSave && !hasClose) return false;

        var hasExcelTarget = ContainsAny(compact, ExcelTargets);
        var hasWordTarget = ContainsAny(compact, WordTargets);
        var hasTextTarget = ContainsAny(compact, TextTargets);
        if ((hasExcelTarget ? 1 : 0) + (hasWordTarget ? 1 : 0) + (hasTextTarget ? 1 : 0) > 1)
            return false;

        var hasGenericTarget = ContainsAny(compact, GenericTargets);
        var hasContextTarget = hasGenericTarget || ContainsAny(compact, ExplicitDocumentTargets) ||
            ContainsAny(compact, ["当前", "这个", "刚才"]);

        // A bare “关闭/退出”, or “退出 Word/Excel”, can mean closing an
        // application rather than saving a document. Require a document noun
        // or a current/this context word for close-only lifecycle commands. A
        // save+close pair is itself specific enough to identify document state.
        if (hasClose && !hasSave && !hasContextTarget && !allowImplicitCurrentDocument) return false;

        var remainder = compact;
        remainder = RemoveTokens(remainder, SaveTerms);
        remainder = RemoveTokens(remainder, CloseTerms);
        remainder = RemoveTokens(remainder, ExcelTargets);
        remainder = RemoveTokens(remainder, WordTargets);
        remainder = RemoveTokens(remainder, TextTargets);
        remainder = RemoveTokens(remainder, GenericTargets);
        remainder = RemoveTokens(remainder, Fillers);
        if (remainder.Length != 0) return false;

        var target = hasExcelTarget
            ? DesktopDocumentLifecycleTarget.Excel
            : hasWordTarget
                ? DesktopDocumentLifecycleTarget.Word
                : hasTextTarget
                    ? DesktopDocumentLifecycleTarget.Text
                    : DesktopDocumentLifecycleTarget.Generic;
        intent = new DesktopDocumentLifecycleIntent(
            hasClose ? DesktopTextDocumentEditActions.Close : DesktopTextDocumentEditActions.Save,
            target);
        return true;
    }

    private static bool ContainsAny(string value, IReadOnlyList<string> terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string RemoveTokens(string value, IReadOnlyList<string> tokens)
    {
        var result = value;
        foreach (var token in tokens.OrderByDescending(static item => item.Length))
        {
            int index;
            while ((index = result.IndexOf(token, StringComparison.OrdinalIgnoreCase)) >= 0)
                result = string.Concat(result.AsSpan(0, index), result.AsSpan(index + token.Length));
        }
        return result;
    }
}
