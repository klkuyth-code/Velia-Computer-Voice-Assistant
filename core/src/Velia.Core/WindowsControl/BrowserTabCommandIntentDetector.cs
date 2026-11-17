using System.Text;
using System.Text.RegularExpressions;

namespace Velia.Core.WindowsControl;

internal static class BrowserTabCommandActions
{
    public const string GoBack = "goBack";
    public const string CloseTab = "closeTab";
    public const string Scroll = "scroll";
}

internal sealed record BrowserTabCommand(
    string Action,
    string? TargetTitle,
    string? ScrollPosition = null,
    int? ScrollAmount = null);

internal static partial class BrowserTabCommandIntentDetector
{
    private static readonly string[] Denials =
        ["不要", "别", "不许", "禁止", "无需", "不用"];
    private static readonly string[] Questions =
        ["怎么", "如何", "为什么", "能不能", "是否", "可不可以"];
    private static readonly HashSet<string> CurrentTabPhrases = new(StringComparer.Ordinal)
    {
        "关闭当前标签页", "关闭这个标签页", "关闭这一标签页", "关闭当前网页标签页",
        "关闭这个网页标签页", "关闭当前网页", "关闭这个网页", "关闭这一页",
        "关闭标签页", "关闭此标签页", "关闭当前页", "关闭这个页面", "关闭当前页面",
        "关闭浏览器当前标签页", "关闭谷歌浏览器当前标签页", "关闭当前浏览器标签页",
        "关掉当前标签页", "关掉这个标签页", "关掉当前网页", "关掉这个网页",
        "关掉标签页", "关掉当前页", "关掉这个页面", "把这个页面关掉", "把当前页面关掉",
        "把当前标签页关闭", "把这个标签页关闭", "把当前网页关闭", "把这个网页关闭",
    };
    private static readonly HashSet<string> BackPhrases = new(StringComparer.Ordinal)
    {
        "返回上一页", "回到上一页", "退回上一页", "后退到上一页", "后退一页",
        "浏览器返回上一页", "网页返回上一页", "当前页面返回上一页", "后退",
    };
    private static readonly HashSet<string> ScrollBottomPhrases = new(StringComparer.Ordinal)
    {
        "滑动到页面底部", "滚动到页面底部", "滑到页面底部", "滚到页面底部",
        "滑动到底部", "滚动到底部", "滑到底部", "滚到底部",
        "滑到最底部", "滚到最底部", "滑到页面最底部", "滚到页面最底部",
        "滑到网页最底部", "滚到网页最底部", "页面滑动到底部", "页面滚动到底部",
        "网页滑动到底部", "网页滚动到底部", "直接滑到底部", "一直滑到底部",
    };
    private static readonly HashSet<string> ScrollTopPhrases = new(StringComparer.Ordinal)
    {
        "滑动到页面顶部", "滚动到页面顶部", "滑到页面顶部", "滚到页面顶部",
        "滑动到顶部", "滚动到顶部", "滑到顶部", "滚到顶部",
        "返回页面顶部", "回到页面顶部", "回到顶部", "页面滑动到顶部", "网页滚动到顶部",
    };
    private static readonly HashSet<string> ScrollDownPhrases = new(StringComparer.Ordinal)
    {
        "向下滑", "向下滑动", "向下滚动", "往下滑", "往下滑动", "往下滚动",
        "页面向下滑", "页面向下滚动", "网页向下滑", "网页向下滚动",
    };
    private static readonly HashSet<string> ScrollUpPhrases = new(StringComparer.Ordinal)
    {
        "向上滑", "向上滑动", "向上滚动", "往上滑", "往上滑动", "往上滚动",
        "页面向上滑", "页面向上滚动", "网页向上滑", "网页向上滚动",
    };

    public static bool TryParseExplicitCommand(string request, out BrowserTabCommand command)
    {
        command = default!;
        if (string.IsNullOrWhiteSpace(request)) return false;
        var normalized = Normalize(request);
        if (normalized.Length is 0 or > 160 ||
            Denials.Any(term => normalized.Contains(term, StringComparison.Ordinal)) ||
            Questions.Any(term => normalized.StartsWith(term, StringComparison.Ordinal)))
            return false;

        normalized = PolitePrefix().Replace(normalized, string.Empty).Trim();
        if (ScrollBottomPhrases.Contains(normalized) || ScrollBottomPattern().IsMatch(normalized))
        {
            command = new BrowserTabCommand(BrowserTabCommandActions.Scroll, null, "bottom");
            return true;
        }
        if (ScrollTopPhrases.Contains(normalized) || ScrollTopPattern().IsMatch(normalized))
        {
            command = new BrowserTabCommand(BrowserTabCommandActions.Scroll, null, "top");
            return true;
        }
        if (ScrollDownPhrases.Contains(normalized))
        {
            command = new BrowserTabCommand(BrowserTabCommandActions.Scroll, null, null, 900);
            return true;
        }
        if (ScrollUpPhrases.Contains(normalized))
        {
            command = new BrowserTabCommand(BrowserTabCommandActions.Scroll, null, null, -900);
            return true;
        }
        var relativeScroll = RelativeScrollPattern().Match(normalized);
        if (relativeScroll.Success)
        {
            var amount = relativeScroll.Groups["distance"].Value is "一点" or "一些" or "一下" ? 520 : 900;
            if (relativeScroll.Groups["direction"].Value == "上") amount = -amount;
            command = new BrowserTabCommand(BrowserTabCommandActions.Scroll, null, null, amount);
            return true;
        }
        if (BackPhrases.Contains(normalized))
        {
            command = new BrowserTabCommand(BrowserTabCommandActions.GoBack, null);
            return true;
        }
        if (CurrentTabPhrases.Contains(normalized))
        {
            command = new BrowserTabCommand(BrowserTabCommandActions.CloseTab, null);
            return true;
        }

        var match = CloseNamedTab().Match(normalized);
        if (!match.Success) match = CloseNamedTabWithBa().Match(normalized);
        if (!match.Success) return false;
        var target = match.Groups["target"].Value.Trim(
            ' ', '\t', '“', '”', '‘', '’', '\'', '"', '，', ',', '。');
        target = BrowserPrefix().Replace(target, string.Empty).Trim();
        if (target.Length is < 1 or > 80 ||
            target is "当前" or "这个" or "这一" or "浏览器" or "谷歌浏览器" or "Chrome" ||
            target.Contains("所有", StringComparison.Ordinal) ||
            target.Contains("全部", StringComparison.Ordinal))
            return false;

        command = new BrowserTabCommand(BrowserTabCommandActions.CloseTab, target);
        return true;
    }

    public static string GetCanonicalKey(BrowserTabCommand command) =>
        $"browser-tab:{command.Action}:{NormalizeTitle(command.TargetTitle ?? "current")}:{command.ScrollPosition}:{command.ScrollAmount}";

    public static bool IsContextuallyAllowed(
        string request,
        BrowserTabCommand command,
        string? currentWebPlatform,
        bool foregroundBrowser = false) =>
        command.Action != BrowserTabCommandActions.Scroll ||
        !string.IsNullOrWhiteSpace(currentWebPlatform) ||
        foregroundBrowser ||
        HasExplicitBrowserScope(request);

    public static bool HasExplicitBrowserScope(string request) =>
        Regex.IsMatch(
            request,
            "浏览器|网页|页面|标签页|Chrome|谷歌",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Normalize(string value) =>
        value.Normalize(NormalizationForm.FormKC)
            .Trim()
            .TrimEnd('。', '！', '!', '？', '?', '，', ',', '；', ';');

    private static string NormalizeTitle(string value) =>
        Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"[\s\p{P}\p{S}]+", string.Empty)
            .ToLowerInvariant();

    [GeneratedRegex(@"^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|随后|再)\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex PolitePrefix();

    [GeneratedRegex(@"^(?:关闭|关掉)\s*(?<target>.+?)(?:这个|的)?(?:网页|浏览器)?(?:标签|标签页|标签页面)$", RegexOptions.CultureInvariant)]
    private static partial Regex CloseNamedTab();

    [GeneratedRegex(@"^把\s*(?<target>.+?)(?:这个|的)?(?:网页|浏览器)?(?:标签|标签页|标签页面)\s*(?:关闭|关掉)$", RegexOptions.CultureInvariant)]
    private static partial Regex CloseNamedTabWithBa();

    [GeneratedRegex(@"^(?:谷歌浏览器|Chrome浏览器|Chrome)(?:(?:里|中|内)(?:的)?)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BrowserPrefix();

    [GeneratedRegex(
        @"^(?:把|将)?(?:当前|这个|该)?(?:网页|页面)?(?:向下|往下)?(?:滑动|滚动|滑|滚)(?:到|至)?(?:当前|这个|该)?(?:网页|页面)?(?:到|至)?(?:最)?底部$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 50)]
    private static partial Regex ScrollBottomPattern();

    [GeneratedRegex(
        @"^(?:把|将)?(?:当前|这个|该)?(?:网页|页面)?(?:向上|往上)?(?:滑动|滚动|滑|滚)(?:到|至)?(?:当前|这个|该)?(?:网页|页面)?(?:到|至)?(?:最)?顶部$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 50)]
    private static partial Regex ScrollTopPattern();

    [GeneratedRegex(
        @"^(?:当前|这个|该)?(?:网页|页面)?(?:向|往)(?<direction>上|下)(?:滑动|滚动|滑|滚)(?<distance>一点|一些|一段|半页|一页|一下)$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 50)]
    private static partial Regex RelativeScrollPattern();
}
