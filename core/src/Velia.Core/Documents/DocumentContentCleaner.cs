using System.Text.RegularExpressions;

namespace Velia.Core.Documents;

internal static partial class DocumentContentCleaner
{
    private static readonly string[] ExactNoiseLines =
    [
        "广告", "推广", "赞助", "相关推荐", "相关阅读", "推荐阅读", "热门推荐", "猜你喜欢",
        "点击关注", "扫码关注", "关注我们", "返回顶部", "上一篇", "下一篇",
    ];

    public static string CleanText(string value)
    {
        var paragraphs = new List<string>();
        foreach (var raw in Normalize(value).Split('\n'))
        {
            var paragraph = CleanInlineCredits(raw.Trim());
            if (paragraph.Length == 0 || IsNoiseParagraph(paragraph)) continue;
            paragraphs.Add(paragraph);
        }
        return string.Join("\n", paragraphs);
    }

    public static bool IsNoiseParagraph(string value)
    {
        var paragraph = value.Trim();
        if (paragraph.Length == 0) return true;
        if (ExactNoiseLines.Contains(paragraph, StringComparer.OrdinalIgnoreCase)) return true;
        if (paragraph.Length <= 200 && BylinePattern().IsMatch(paragraph)) return true;
        if (paragraph.Length <= 240 && LegalNoisePattern().IsMatch(paragraph)) return true;
        if (paragraph.Length <= 200 && ContactNoisePattern().IsMatch(paragraph)) return true;
        if (paragraph.Length <= 240 && AppPromotionPattern().IsMatch(paragraph)) return true;
        return paragraph.Length <= 160 && ExactNoiseLines.Any(marker =>
            paragraph.StartsWith(marker + "：", StringComparison.OrdinalIgnoreCase) ||
            paragraph.StartsWith(marker + ":", StringComparison.OrdinalIgnoreCase));
    }

    public static string CleanInlineCredits(string value) =>
        InlineEnglishPhotoCreditPattern().Replace(value, string.Empty).Trim();

    public static IReadOnlyList<string> FindInlineCredits(string value) =>
        InlineEnglishPhotoCreditPattern().Matches(value).Select(static match => match.Value).Distinct().ToArray();

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    [GeneratedRegex(@"^(?:作者|编辑|来源|记者|文|图|摄影|责任编辑|撰文|供稿)\s*[:：]", RegexOptions.CultureInvariant, 50)]
    private static partial Regex BylinePattern();

    [GeneratedRegex(@"^(?:版权声明|免责声明|转载声明|本文来源|文章来源)\s*[:：]?", RegexOptions.CultureInvariant, 50)]
    private static partial Regex LegalNoisePattern();

    [GeneratedRegex(@"(?:微信号|公众号)\s*[:：]", RegexOptions.CultureInvariant, 50)]
    private static partial Regex ContactNoisePattern();

    [GeneratedRegex(@"^(?:免费)?(?:下载|安装).*(?:iOS|Android|安卓|App|应用)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex AppPromotionPattern();

    [GeneratedRegex(@"[A-Z][A-Za-z .'-]{1,80}\s+for\s+The\s+New\s+York\s+Times", RegexOptions.CultureInvariant, 50)]
    private static partial Regex InlineEnglishPhotoCreditPattern();
}
