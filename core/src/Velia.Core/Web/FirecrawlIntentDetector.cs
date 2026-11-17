using System.Text;
using System.Text.RegularExpressions;

namespace Velia.Core.Web;

internal static class FirecrawlIntentDetector
{
    private static readonly string[] NetworkDenials =
    [
        "不要联网", "别联网", "无需联网", "不许联网", "不要上网", "别上网",
        "不要使用Firecrawl", "不用Firecrawl", "别用Firecrawl",
    ];
    private static readonly string[] SearchDenials = ["不要搜索", "别搜索", "不用搜索"];
    private static readonly string[] ScrapeDenials = ["不要抓取", "别抓取", "不用抓取"];
    private static readonly string[] InstructionalQueries =
    [
        "如何搜索", "怎么搜索", "怎样搜索", "搜索教程", "如何联网", "怎么联网", "如何抓取", "怎么抓取",
    ];
    private static readonly string[] SearchMarkers =
    [
        "搜索", "查找", "查询", "查一下", "搜一下", "找一下", "网上", "联网", "互联网", "最新",
    ];
    private static readonly string[] DirectUrlMarkers =
    [
        "抓取", "读取", "访问", "打开", "查看", "看一下", "总结", "分析", "网页", "网站", "网址", "链接",
    ];

    public static bool IsExplicitCommand(string request, FirecrawlCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentNullException.ThrowIfNull(command);
        if (ContainsAny(request, NetworkDenials) || ContainsAny(request, InstructionalQueries))
            return false;
        return command.Action switch
        {
            FirecrawlActions.Search =>
                !ContainsAny(request, SearchDenials) &&
                ContainsAny(request, SearchMarkers) &&
                command.Query is { Length: >= 1 and <= 500 } && command.Limit is >= 1 and <= 5 &&
                IsQueryGrounded(request, command.Query),
            FirecrawlActions.Scrape when command.ResultId is not null =>
                !ContainsAny(request, ScrapeDenials) &&
                ContainsAny(request, SearchMarkers) &&
                command.Query is null && command.Limit is null && command.Url is null,
            FirecrawlActions.Scrape when command.Url is { } url =>
                !ContainsAny(request, ScrapeDenials) &&
                ContainsAny(request, DirectUrlMarkers) &&
                command.Query is null && command.Limit is null && command.ResultId is null && MentionsUrl(request, url),
            _ => false,
        };
    }

    private static bool IsQueryGrounded(string request, string query)
    {
        var normalizedRequest = Normalize(request);
        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length < 2) return false;
        if (normalizedRequest.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)) return true;
        var terms = Regex.Split(query, @"[\s,，。；;、|]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
            .Select(Normalize)
            .Where(static term => term.Length >= 2)
            .ToArray();
        return terms.Length > 0 && terms.All(term => normalizedRequest.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MentionsUrl(string request, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) return false;
        foreach (Match match in Regex.Matches(
                     request,
                     @"https?://[^\s，。；;、]+",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(50)))
        {
            if (Uri.TryCreate(match.Value.TrimEnd('.', ',', ')', '）', ']', '】', '>', '》', '"', '\''), UriKind.Absolute, out var candidate) &&
                Uri.Compare(candidate, target, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0)
                return true;
        }
        return false;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static bool ContainsAny(string request, IEnumerable<string> markers) =>
        markers.Any(marker => request.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
