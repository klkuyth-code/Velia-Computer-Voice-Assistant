using System.Text.RegularExpressions;

namespace Velia.Core.WindowsControl;

internal sealed record BrowserElementActionCommand(string Target, int? Ordinal);

internal static partial class BrowserElementActionIntentDetector
{
    private static readonly string[] DeniedTerms =
    [
        "支付", "付款", "购买", "下单", "发送", "发布", "删除", "注销", "密码", "验证码",
        "授权", "登录", "注册", "上传",
    ];
    private static readonly string[] BrowserScopeTerms =
        ["网页", "页面", "浏览器", "网站", "搜索结果", "个人主页", "相册", "作品页"];
    private static readonly string[] ContextPronounTerms =
        ["她的", "他的", "它的", "他们的", "她们的", "这个人的", "该用户的"];

    public static bool TryParseExplicitCommand(string request, out BrowserElementActionCommand command)
    {
        command = default!;
        if (string.IsNullOrWhiteSpace(request)) return false;
        var normalized = request.Trim().TrimEnd('。', '！', '!', '？', '?');
        if (normalized.StartsWith("不要", StringComparison.Ordinal) ||
            normalized.StartsWith('别') ||
            normalized.StartsWith("怎么", StringComparison.Ordinal) ||
            normalized.StartsWith("如何", StringComparison.Ordinal) ||
            normalized.StartsWith("为什么", StringComparison.Ordinal) ||
            DeniedTerms.Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (TryNormalizePageStep(normalized, out var pageStep))
        {
            command = new BrowserElementActionCommand(pageStep, null);
            return true;
        }

        var match = ActionPattern().Match(normalized);
        if (!match.Success) return false;
        var target = match.Groups["target"].Value.Trim(' ', '，', ',', '。');
        foreach (var prefix in new[]
                 {
                     "当前网页上的", "当前页面上的", "网页上的", "页面上的", "当前网页的", "当前页面的",
                     "她的", "他的", "它的", "这个", "该",
                 })
        {
            if (target.StartsWith(prefix, StringComparison.Ordinal) && target.Length > prefix.Length)
                target = target[prefix.Length..].TrimStart();
        }
        target = NormalizePaginationTarget(target);
        if (target.Length is < 1 or > 80) return false;
        if (target.Contains('并') || target.Contains("以及", StringComparison.Ordinal) ||
            target is "网页" or "页面" or "浏览器" or "当前网页" or "当前页面")
            return false;

        int? ordinal = null;
        var ordinalMatch = OrdinalPattern().Match(target);
        if (ordinalMatch.Success && TryParseOrdinal(ordinalMatch.Groups["ordinal"].Value, out var parsed))
            ordinal = parsed;
        command = new BrowserElementActionCommand(target, ordinal);
        return true;
    }

    public static bool HasExplicitBrowserScope(string request) =>
        BrowserScopeTerms.Any(marker => request.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
        PaginationTargetPattern().IsMatch(request) || PageStepPattern().IsMatch(request.Trim());

    public static bool RequiresBoundBrowserContext(string request) =>
        ContextPronounTerms.Any(marker => request.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseOrdinal(string value, out int ordinal)
    {
        if (int.TryParse(value, out ordinal)) return ordinal is >= 1 and <= 50;
        ordinal = value switch
        {
            "一" => 1, "二" or "两" => 2, "三" => 3, "四" => 4, "五" => 5,
            "六" => 6, "七" => 7, "八" => 8, "九" => 9, "十" => 10,
            _ => 0,
        };
        return ordinal > 0;
    }

    private static string NormalizePaginationTarget(string target)
    {
        var match = PaginationTargetPattern().Match(target);
        if (!match.Success || match.Index != 0 || match.Length != target.Length) return target;
        return $"第{match.Groups["ordinal"].Value}页";
    }

    private static bool TryNormalizePageStep(string value, out string target)
    {
        target = string.Empty;
        var match = PageStepPattern().Match(value);
        if (!match.Success) return false;
        target = match.Groups["direction"].Value == "上" ? "上一页" : "下一页";
        return true;
    }

    [GeneratedRegex(
        @"^(?:(?:然后|接着|随后|之后|下一步|再|继续|请|帮我|麻烦)\s*)*(?:点击|点开|点一下|选择|进入|打开|查看)\s*(?<target>[^，。！？!?\r\n]{1,80})$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 50)]
    private static partial Regex ActionPattern();

    [GeneratedRegex(@"第\s*(?<ordinal>[0-9一二两三四五六七八九十]+)\s*(?:个|张|项|条|位|页)", RegexOptions.CultureInvariant)]
    private static partial Regex OrdinalPattern();

    [GeneratedRegex(
        @"第\s*(?<ordinal>[0-9一二两三四五六七八九十]+)\s*(?:个)?(?:页面|页码|页|业|叶)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 50)]
    private static partial Regex PaginationTargetPattern();

    [GeneratedRegex(
        @"^(?:(?:然后|接着|随后|之后|再|继续|请|帮我|麻烦)\s*)*(?:(?:点击|点开|打开|进入|查看|切换到)\s*)?(?<direction>上|下)(?:一|1)?(?:个)?(?:页|页面)$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 50)]
    private static partial Regex PageStepPattern();
}
