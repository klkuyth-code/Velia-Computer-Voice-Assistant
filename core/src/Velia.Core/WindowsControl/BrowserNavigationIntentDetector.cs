namespace Velia.Core.WindowsControl;

using System.Text.RegularExpressions;

internal sealed record BrowserNavigationCommand(
    string TargetName,
    string Url,
    string ExpectedHost,
    string ExpectedPathPrefix,
    string SuccessMessage);

internal static class BrowserNavigationIntentDetector
{
    private static readonly string[] Denials =
    [
        "不要", "不用", "无需", "不许", "禁止", "别打开", "别访问", "别进入", "不想让你",
    ];

    private static readonly string[] InstructionalQueries = ["如何", "怎么", "怎样", "教程", "教我"];
    private static readonly string[] OpenActions = ["打开", "访问", "进入"];
    private static readonly Regex SearchPattern = new(
        @"(?:搜索(?!结果)|搜一下|搜一搜|搜(?!索结果))(?<query>[^，。！？!?]{1,80})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));
    private static readonly string[] WebTargetMarkers =
    [
        "首页", "主页", "网站", "网页", "网址", "搜索", "百度", "必应", "Bing",
        "抖音", "哔哩哔哩", "B站", "小红书", "知乎", "微博", "YouTube",
        "谷歌搜索", "Google 搜索", "创作者中心", "个人主页",
    ];

    private static readonly BrowserSite[] Sites =
    [
        new("百度首页", "https://www.baidu.com/", "baidu.com", "", "已打开百度首页。", ["百度首页", "百度主页"]),
        new("必应首页", "https://www.bing.com/", "bing.com", "", "已打开必应首页。", ["必应首页", "必应主页", "Bing 首页", "Bing主页"]),
        new("谷歌首页", "https://www.google.com/", "google.com", "", "已打开谷歌首页。", ["谷歌首页", "谷歌主页", "Google 首页", "Google主页"]),
        new("抖音首页", "https://www.douyin.com/", "douyin.com", "", "已打开抖音。", ["抖音首页", "抖音网站", "抖音"]),
        new("我的抖音个人主页", "https://www.douyin.com/user/self?from_tab_name=main", "douyin.com", "/user/self", "已进入你的抖音个人主页。", ["我的抖音个人主页", "抖音个人主页", "我的抖音主页", "我的抖音个人中心", "抖音个人中心"]),
        new("抖音创作者中心", "https://creator.douyin.com/creator-micro/home", "creator.douyin.com", "/creator-micro", "已进入抖音创作者中心。", ["我的抖音创作者中心", "抖音创作者中心"]),
        new("哔哩哔哩首页", "https://www.bilibili.com/", "bilibili.com", "", "已打开哔哩哔哩。", ["哔哩哔哩首页", "B站首页", "哔哩哔哩", "B站"]),
        new("哔哩哔哩创作中心", "https://member.bilibili.com/platform/home", "member.bilibili.com", "/platform", "已进入哔哩哔哩创作中心。", ["哔哩哔哩创作中心", "B站创作中心"]),
        new("小红书首页", "https://www.xiaohongshu.com/", "xiaohongshu.com", "", "已打开小红书。", ["小红书首页", "小红书网站", "小红书"]),
        new("小红书创作服务平台", "https://creator.xiaohongshu.com/", "creator.xiaohongshu.com", "", "已进入小红书创作服务平台。", ["小红书创作者中心", "小红书创作中心", "小红书创作服务平台"]),
        new("知乎首页", "https://www.zhihu.com/", "zhihu.com", "", "已打开知乎。", ["知乎首页", "知乎网站", "知乎"]),
        new("知乎创作中心", "https://www.zhihu.com/creator", "zhihu.com", "/creator", "已进入知乎创作中心。", ["知乎创作者中心", "知乎创作中心"]),
        new("微博首页", "https://weibo.com/", "weibo.com", "", "已打开微博。", ["微博首页", "微博网站", "微博"]),
        new("YouTube 首页", "https://www.youtube.com/", "youtube.com", "", "已打开 YouTube。", ["YouTube 首页", "YouTube主页", "YouTube"]),
        new("YouTube Studio", "https://studio.youtube.com/", "studio.youtube.com", "", "已进入 YouTube Studio。", ["YouTube Studio", "YouTube 创作者中心", "YouTube创作者中心"]),
    ];

    private static readonly Dictionary<string, string> BareHomepageTargets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["百度"] = "百度首页",
            ["必应"] = "必应首页",
            ["Bing"] = "必应首页",
            ["谷歌"] = "谷歌首页",
            ["Google"] = "谷歌首页",
            ["抖音"] = "抖音首页",
            ["哔哩哔哩"] = "哔哩哔哩首页",
            ["B站"] = "哔哩哔哩首页",
            ["小红书"] = "小红书首页",
            ["知乎"] = "知乎首页",
            ["微博"] = "微博首页",
            ["YouTube"] = "YouTube 首页",
        };

    private static readonly SearchProvider[] SearchProviders =
    [
        new("抖音", ["抖音"], query => $"https://www.douyin.com/search/{query}", "douyin.com", "/search/"),
        new("哔哩哔哩", ["哔哩哔哩", "B站"], query => $"https://search.bilibili.com/all?keyword={query}", "search.bilibili.com", "/all"),
        new("小红书", ["小红书"], query => $"https://www.xiaohongshu.com/search_result?keyword={query}", "xiaohongshu.com", "/search_result"),
        new("知乎", ["知乎"], query => $"https://www.zhihu.com/search?type=content&q={query}", "zhihu.com", "/search"),
        new("微博", ["微博"], query => $"https://s.weibo.com/weibo?q={query}", "s.weibo.com", "/weibo"),
        new("YouTube", ["YouTube"], query => $"https://www.youtube.com/results?search_query={query}", "youtube.com", "/results"),
        new("百度", ["百度"], query => $"https://www.baidu.com/s?wd={query}", "baidu.com", "/s"),
        new("必应", ["必应", "Bing"], query => $"https://www.bing.com/search?q={query}", "bing.com", "/search"),
        new("谷歌", ["谷歌", "Google"], query => $"https://www.google.com/search?q={query}", "google.com", "/search"),
    ];

    public static bool TryParseExplicitCommand(string request, out BrowserNavigationCommand command)
        => TryParseExplicitCommand(request, null, out command);

    public static bool TryParseExplicitCommand(
        string request,
        string? contextualPlatform,
        out BrowserNavigationCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        command = new BrowserNavigationCommand(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        if (ContainsAny(request, Denials) || ContainsAny(request, InstructionalQueries))
        {
            return false;
        }

        if (TryParseSearch(request, contextualPlatform, out command)) return true;
        if (!ContainsAny(request, OpenActions)) return false;

        var routeMatches = Sites
            .SelectMany(site => site.Aliases
                .Where(alias => request.Contains(alias, StringComparison.OrdinalIgnoreCase))
                .Select(alias => new { Site = site, Alias = alias }))
            .ToArray();
        var matches = routeMatches
            .Where(match => !routeMatches.Any(other =>
                !ReferenceEquals(match.Site, other.Site) &&
                other.Alias.Length > match.Alias.Length &&
                other.Alias.Contains(match.Alias, StringComparison.OrdinalIgnoreCase)))
            .Select(static match => match.Site)
            .Distinct()
            .Where(site => IsRouteCompatibleWithRequest(site, request))
            .ToArray();
        if (matches.Length == 0 && TryResolveBareHomepageDestination(request, out var bareHomepage))
            matches = [bareHomepage];
        if (matches.Length != 1) return false;

        var site = matches[0];
        command = new BrowserNavigationCommand(
            site.Name,
            site.Url,
            site.ExpectedHost,
            site.ExpectedPathPrefix,
            site.SuccessMessage);
        return true;
    }

    public static bool HasExplicitWebTarget(string request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        return ContainsAny(request, WebTargetMarkers) ||
            request.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("www.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string request, IEnumerable<string> markers) =>
        markers.Any(marker => request.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool TryResolveBareHomepageDestination(string request, out BrowserSite site)
    {
        site = Sites[0];
        var actionEnd = -1;
        foreach (var action in OpenActions)
        {
            var index = request.LastIndexOf(action, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) actionEnd = Math.Max(actionEnd, index + action.Length);
        }
        if (actionEnd < 0 || actionEnd >= request.Length) return false;

        var destination = request[actionEnd..]
            .Trim(' ', '\t', '\r', '\n', '，', ',', '。', '.', '！', '!', '？', '?');
        foreach (var suffix in new[] { "给我看一下", "让我看一下", "看一下", "一下", "网站", "网页", "首页", "主页" })
        {
            if (destination.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                destination = destination[..^suffix.Length].Trim();
        }
        if (!BareHomepageTargets.TryGetValue(destination, out var siteName)) return false;
        site = Sites.Single(candidate => string.Equals(candidate.Name, siteName, StringComparison.Ordinal));
        return true;
    }

    private static bool IsRouteCompatibleWithRequest(BrowserSite site, string request)
    {
        var specificDestination = ContainsAny(request,
            ["个人主页", "个人中心", "创作者中心", "创作中心", "相册", "作品", "照片", "视频", "数据中心"]);
        if (site.Name.EndsWith("首页", StringComparison.Ordinal) && specificDestination) return false;
        if (site.Name.StartsWith("我的", StringComparison.Ordinal) && HasThirdPartyProfileTarget(request)) return false;
        return true;
    }

    private static bool HasThirdPartyProfileTarget(string request)
    {
        if (!ContainsAny(request, ["个人主页", "个人中心"])) return false;
        if (Regex.IsMatch(
                request,
                @"我的(?:抖音|微博|哔哩哔哩|B站|小红书|知乎|YouTube)?(?:个人主页|个人中心)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)))
            return false;
        var destination = request.IndexOf("个人主页", StringComparison.Ordinal);
        if (destination < 0) destination = request.IndexOf("个人中心", StringComparison.Ordinal);
        return destination > 0 && request[..destination].Contains('的');
    }

    private static bool TryParseSearch(
        string request,
        string? contextualPlatform,
        out BrowserNavigationCommand command)
    {
        command = new BrowserNavigationCommand(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        var match = SearchPattern.Match(request);
        if (!match.Success) return false;
        var query = match.Groups["query"].Value.Trim();
        if (query.Length is < 1 or > 80 || query.Any(char.IsControl)) return false;
        // Provider names inside the query are search content, not routing
        // instructions. “用谷歌搜索抖音” must select Google rather than become
        // ambiguous between Google and Douyin. Only the phrase before the
        // search verb is allowed to name the provider.
        var providerScope = request[..match.Index];
        var matches = SearchProviders.Where(provider => ContainsAny(providerScope, provider.Markers)).ToList();
        if (matches.Count > 1 && providerScope.Contains("谷歌浏览器", StringComparison.OrdinalIgnoreCase))
            matches.RemoveAll(static provider => provider.Name == "谷歌");
        if (matches.Count == 0 && !string.IsNullOrWhiteSpace(contextualPlatform))
        {
            matches = SearchProviders
                .Where(provider => provider.Markers.Any(marker =>
                    string.Equals(marker, contextualPlatform, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        if (matches.Count != 1) return false;

        var provider = matches[0];
        var escaped = Uri.EscapeDataString(query);
        command = new BrowserNavigationCommand(
            $"{provider.Name}搜索结果",
            provider.BuildUrl(escaped),
            provider.ExpectedHost,
            provider.ExpectedPathPrefix,
            $"已在{provider.Name}搜索“{query}”。");
        return true;
    }

    private sealed record BrowserSite(
        string Name,
        string Url,
        string ExpectedHost,
        string ExpectedPathPrefix,
        string SuccessMessage,
        string[] Aliases);

    private sealed record SearchProvider(
        string Name,
        string[] Markers,
        Func<string, string> BuildUrl,
        string ExpectedHost,
        string ExpectedPathPrefix);
}
