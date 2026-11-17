using System.Text.Json;
using System.Text.RegularExpressions;

namespace Velia.Core.WindowsControl;

internal enum BrowserTargetKind
{
    SearchResults,
    EntityProfile,
    CreatorCenter,
}

internal sealed record BrowserTargetExpectation(
    BrowserTargetKind Kind,
    string? Platform,
    string? Entity);

internal static class BrowserTargetEvidenceVerifier
{
    private static readonly string[] Platforms =
    [
        "抖音", "微博", "哔哩哔哩", "B站", "小红书", "知乎", "YouTube",
        "百度", "谷歌", "Google", "必应", "Bing",
    ];
    private static readonly string[] ProfileEvidenceMarkers = ["关注", "粉丝", "作品", "动态", "获赞", "简介"];

    private static readonly Regex SearchEntityPattern = new(
        @"(?:搜索|搜一下|搜一搜|搜)(?<entity>[^，。！？!?\r\n]{1,60})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    private static readonly Regex ProfileEntityPattern = new(
        @"(?:(?:进入|打开|点击|查看|访问)\s*)?(?<entity>[\p{L}\p{N}·_.-]{1,32})的(?:个人)?(?:主页|相册|作品)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    public static BrowserTargetExpectation? TryCreate(string request)
    {
        if (string.IsNullOrWhiteSpace(request)) return null;
        var platform = Platforms.LastOrDefault(item => request.Contains(item, StringComparison.OrdinalIgnoreCase));
        var entity = ExtractEntity(request);
        if (ContainsAny(request, ["个人主页", "个人中心"]) && entity is not null)
            return new BrowserTargetExpectation(BrowserTargetKind.EntityProfile, platform, entity);
        if (ContainsAny(request, ["创作者中心", "创作中心", "创作服务平台", "YouTube Studio"]))
            return new BrowserTargetExpectation(BrowserTargetKind.CreatorCenter, platform, null);
        // Only platform-scoped browser searches require DOM/page evidence. Generic
        // network research may be served by Firecrawl and must not be forced through
        // the browser verification pipeline.
        if (ContainsAny(request, ["搜索", "搜一下", "搜一搜"]) && entity is not null && platform is not null)
            return new BrowserTargetExpectation(BrowserTargetKind.SearchResults, platform, entity);
        return null;
    }

    public static bool IsVerified(string toolResult, BrowserTargetExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        try
        {
            using var document = JsonDocument.Parse(toolResult);
            if (!document.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                !document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                return false;

            return EnumeratePages(result).Any(page => Matches(page, expectation));
        }
        catch (JsonException) { return false; }
    }

    private static bool Matches(JsonElement page, BrowserTargetExpectation expectation)
    {
        var url = ReadString(page, "url");
        var title = ReadString(page, "title");
        var visibleText = ReadString(page, "visibleText");
        var evidence = Normalize($"{Uri.UnescapeDataString(url)}\n{title}\n{visibleText}");
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        var path = uri?.AbsolutePath ?? string.Empty;
        if (!PlatformMatches(expectation.Platform, host)) return false;

        return expectation.Kind switch
        {
            BrowserTargetKind.SearchResults =>
                expectation.Entity is not null && ContainsNormalized(evidence, expectation.Entity) &&
                LooksLikeSearchPage(host, path, url),
            BrowserTargetKind.EntityProfile =>
                expectation.Entity is not null && ContainsNormalized(evidence, expectation.Entity) &&
                !LooksLikeSearchPage(host, path, url) &&
                LooksLikeProfilePage(host, path, visibleText),
            BrowserTargetKind.CreatorCenter => LooksLikeCreatorCenter(host, path, title, visibleText),
            _ => false,
        };
    }

    private static IEnumerable<JsonElement> EnumeratePages(JsonElement result)
    {
        if (result.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Object) yield return page;
        if (result.TryGetProperty("finalPage", out var finalPage) && finalPage.ValueKind == JsonValueKind.Object)
        {
            if (finalPage.TryGetProperty("page", out var nestedPage) && nestedPage.ValueKind == JsonValueKind.Object)
                yield return nestedPage;
            else yield return finalPage;
        }
        if (result.TryGetProperty("tab", out var tab) && tab.ValueKind == JsonValueKind.Object) yield return tab;
    }

    private static string? ExtractEntity(string request)
    {
        var profileMatches = ProfileEntityPattern.Matches(request);
        if (profileMatches.Count > 0)
        {
            var profile = profileMatches[profileMatches.Count - 1].Groups["entity"].Value.Trim();
            if (IsUsefulEntity(profile)) return profile;
        }

        var searchMatches = SearchEntityPattern.Matches(request);
        if (searchMatches.Count == 0) return null;
        var entity = searchMatches[searchMatches.Count - 1].Groups["entity"].Value.Trim();
        foreach (var prefix in new[] { "并打开", "并进入", "并查看", "打开", "进入", "一下", "关于" })
        {
            if (entity.StartsWith(prefix, StringComparison.Ordinal) && entity.Length > prefix.Length)
                entity = entity[prefix.Length..].TrimStart();
        }
        return IsUsefulEntity(entity) ? entity : null;
    }

    private static bool IsUsefulEntity(string value) =>
        value.Length is >= 1 and <= 60 &&
        value is not ("我" or "我的" or "他" or "她" or "它" or "他们" or "她们" or "这个人" or "该用户");

    private static bool LooksLikeSearchPage(string host, string path, string url) =>
        host.StartsWith("s.", StringComparison.OrdinalIgnoreCase) ||
        host.StartsWith("search.", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("search", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/s", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("?q=", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("&q=", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("keyword=", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("wd=", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeProfilePage(string host, string path, string visibleText) =>
        host.StartsWith("space.", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(path, @"/(?:user|u|profile|people|n)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
        (path.Length > 1 && !path.Contains("search", StringComparison.OrdinalIgnoreCase) &&
         ProfileEvidenceMarkers.Count(marker => visibleText.Contains(marker, StringComparison.OrdinalIgnoreCase)) >= 2);

    private static bool LooksLikeCreatorCenter(string host, string path, string title, string visibleText) =>
        host.StartsWith("creator.", StringComparison.OrdinalIgnoreCase) ||
        host.StartsWith("studio.", StringComparison.OrdinalIgnoreCase) ||
        host.StartsWith("member.", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("creator", StringComparison.OrdinalIgnoreCase) ||
        ContainsAny($"{title}\n{visibleText}", ["创作者中心", "创作中心", "创作服务平台", "YouTube Studio"]);

    private static bool PlatformMatches(string? platform, string host)
    {
        if (platform is null) return true;
        var expectedHosts = platform.ToLowerInvariant() switch
        {
            "抖音" => new[] { "douyin.com" },
            "微博" => new[] { "weibo.com" },
            "哔哩哔哩" or "b站" => new[] { "bilibili.com" },
            "小红书" => new[] { "xiaohongshu.com" },
            "知乎" => new[] { "zhihu.com" },
            "youtube" => new[] { "youtube.com" },
            "百度" => new[] { "baidu.com" },
            "谷歌" or "google" => new[] { "google.com" },
            "必应" or "bing" => new[] { "bing.com" },
            _ => [],
        };
        return expectedHosts.Length == 0 || expectedHosts.Any(expected =>
            string.Equals(host, expected, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{expected}", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsNormalized(string source, string target) =>
        Normalize(source).Contains(Normalize(target), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        Regex.Replace(value, @"[\s\p{P}\p{S}]+", string.Empty, RegexOptions.CultureInvariant).Trim();

    private static string ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool ContainsAny(string value, IEnumerable<string> markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
