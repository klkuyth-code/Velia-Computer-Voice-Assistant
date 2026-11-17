using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Velia.Core.Documents;

internal sealed record BrowserArticleToWordCommand;

internal sealed record BrowserArticleContent(
    string Title,
    Uri Source,
    string Body,
    string FormattedText,
    int ParagraphCount);

internal static partial class BrowserArticleToWordIntentDetector
{
    private static readonly string[] DeniedTerms = ["不要", "别", "禁止", "无需", "不用"];
    private static readonly string[] InstructionalPrefixes = ["怎么", "如何", "为什么", "能不能", "是否"];
    private static readonly string[] BrowserTerms =
    [
        "当前网页", "当前页面", "这个网页", "这个页面", "网页正文", "页面正文", "网页文章", "页面文章",
        "当前文章", "这篇文章",
    ];
    private static readonly string[] ContentTerms = ["正文", "文章", "内容"];
    private static readonly string[] DestinationTerms = ["Word", "沃德", "文档", "文件"];
    private static readonly string[] ActionTerms = ["写入", "复制到", "粘贴到", "整理到", "保存到", "导入", "放到", "放进", "添加到"];

    public static bool TryParseExplicitCommand(string request, out BrowserArticleToWordCommand command)
    {
        command = null!;
        if (string.IsNullOrWhiteSpace(request)) return false;
        var value = request.Normalize(NormalizationForm.FormKC).Trim().TrimEnd('。', '！', '!', '？', '?');
        if (value.Length > 500 || DeniedTerms.Any(value.Contains) || InstructionalPrefixes.Any(value.StartsWith))
            return false;
        if (!BrowserTerms.Any(value.Contains) || !ContentTerms.Any(value.Contains) ||
            !DestinationTerms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
            !ActionTerms.Any(value.Contains))
            return false;
        command = new BrowserArticleToWordCommand();
        return true;
    }

    public static string GetCanonicalKey() => "browser-article-to-word:current";
}

internal static partial class BrowserArticleContentParser
{
    private const int MaximumArticleCharacters = 32_000;
    public static bool TryParseAndClean(
        string toolResult,
        out BrowserArticleContent content,
        out string failure)
    {
        content = null!;
        failure = "当前网页没有提取到可写入的正文。";
        if (string.IsNullOrWhiteSpace(toolResult) || toolResult.Length > 262_144) return false;
        try
        {
            using var document = JsonDocument.Parse(toolResult);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Object)
                root = result;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("article", out var article) || article.ValueKind != JsonValueKind.Object)
                return false;
            var title = ReadBoundedString(article, "title", 500);
            var url = ReadBoundedString(article, "url", 2_048);
            var rawText = ReadBoundedString(article, "text", MaximumArticleCharacters);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var source) ||
                source.Scheme is not ("http" or "https"))
            {
                failure = "当前页面地址没有通过本地校验。";
                return false;
            }
            var paragraphs = CleanParagraphs(rawText, title);
            var bodyCharacters = paragraphs.Sum(static paragraph => paragraph.Length);
            if (LooksLikeListingPage(paragraphs))
            {
                failure = "当前页面看起来是首页或内容列表，请先进入一篇具体文章后再写入 Word。";
                return false;
            }
            if (paragraphs.Count < 2 || bodyCharacters < 60)
            {
                failure = "当前页面没有找到足够完整的文章正文。";
                return false;
            }
            var safeTitle = CleanLine(title);
            if (safeTitle.Length > 200) safeTitle = safeTitle[..200].TrimEnd();
            var body = string.Join("\n\n", paragraphs);
            var formatted = string.IsNullOrWhiteSpace(safeTitle) ? body : safeTitle + "\n\n" + body;
            if (formatted.Length > MaximumArticleCharacters)
                formatted = formatted[..MaximumArticleCharacters].TrimEnd();
            content = new BrowserArticleContent(safeTitle, source, body, formatted, paragraphs.Count);
            failure = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            failure = "浏览器返回的文章正文格式无效。";
            return false;
        }
    }

    private static List<string> CleanParagraphs(string value, string title)
    {
        var result = new List<string>();
        foreach (var rawLine in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = DocumentContentCleaner.CleanInlineCredits(CleanLine(rawLine));
            if (line.Length == 0 || string.Equals(line, title.Trim(), StringComparison.Ordinal) || IsNoiseLine(line))
                continue;
            if (result.Count > 0 && string.Equals(result[^1], line, StringComparison.Ordinal)) continue;
            result.Add(line);
        }
        return result;
    }

    private static string CleanLine(string value) =>
        Regex.Replace(value.Trim(), @"[\t ]+", " ", RegexOptions.CultureInvariant).Trim();

    private static bool IsNoiseLine(string value)
    {
        return DocumentContentCleaner.IsNoiseParagraph(value);
    }

    internal static bool LooksLikeListingPage(IReadOnlyList<string> paragraphs)
    {
        if (paragraphs.Count < 9) return false;
        var repeatedCards = 0;
        for (var index = 0; index + 2 < paragraphs.Count; index++)
        {
            var combined = NormalizeForContainment(paragraphs[index]);
            var heading = NormalizeForContainment(paragraphs[index + 1]);
            var summary = NormalizeForContainment(paragraphs[index + 2]);
            if (heading.Length is < 2 or > 120 || summary.Length < 12) continue;
            if (combined.Length >= heading.Length + summary.Length - 4 &&
                combined.Contains(heading, StringComparison.Ordinal) &&
                combined.Contains(summary, StringComparison.Ordinal))
            {
                repeatedCards++;
                index += 2;
            }
        }
        return repeatedCards >= 3;
    }

    private static string NormalizeForContainment(string value) =>
        Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"[\s\p{P}\p{S}]", string.Empty, RegexOptions.CultureInvariant);

    private static string ReadBoundedString(JsonElement root, string property, int maximumLength)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            return string.Empty;
        var value = element.GetString() ?? string.Empty;
        if (value.Length > maximumLength || value.Any(character =>
                char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new JsonException();
        return value;
    }

}
