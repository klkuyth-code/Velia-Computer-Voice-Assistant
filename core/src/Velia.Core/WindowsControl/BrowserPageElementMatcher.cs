using System.Text.Json;
using System.Text.RegularExpressions;

namespace Velia.Core.WindowsControl;

internal sealed record BrowserPageElementMatch(string Ref, string Label, string PageUrl, string Href);

internal static partial class BrowserPageElementMatcher
{
    public static bool IsOrderedSearchResultCommand(BrowserElementActionCommand command) =>
        command.Ordinal is not null && IsSearchResultTarget(NormalizeTarget(command.Target));

    public static bool IsPaginationCommand(BrowserElementActionCommand command) =>
        (command.Ordinal is not null && PaginationCommandPattern().IsMatch(Normalize(command.Target))) ||
        IsPageStepTarget(command.Target);

    public static bool TryFind(
        string toolResult,
        BrowserElementActionCommand command,
        string? contextualEntity,
        out BrowserPageElementMatch match)
    {
        match = default!;
        try
        {
            using var document = JsonDocument.Parse(toolResult);
            if (!TryReadPage(document.RootElement, out var page) ||
                !page.TryGetProperty("elements", out var elements) ||
                elements.ValueKind != JsonValueKind.Array)
                return false;

            var pageUrl = ReadString(page, "url");
            if (command.Ordinal is not null && IsPaginationCommand(command))
                return TryFindPagination(elements, pageUrl, command.Ordinal!.Value, out match);
            if (IsPageStepTarget(command.Target))
                return TryFindPageStep(elements, pageUrl, command.Target, out match);
            var candidates = elements.EnumerateArray()
                .Select((element, index) => Score(element, index, command.Target, contextualEntity))
                .Where(static candidate => candidate is not null)
                .Select(static candidate => candidate!)
                .OrderByDescending(static candidate => candidate.Score)
                .ThenBy(static candidate => candidate.Index)
                .ToArray();
            if (candidates.Length == 0) return false;

            ElementCandidate selected;
            if (command.Ordinal is { } ordinal)
            {
                var ordinalCandidates = candidates.Where(static candidate => candidate.Score > 0).ToArray();
                if (ordinal > ordinalCandidates.Length) return false;
                selected = ordinalCandidates[ordinal - 1];
            }
            else
            {
                selected = candidates[0];
                if (selected.Score <= 0 ||
                    (candidates.Length > 1 && candidates[1].Score == selected.Score))
                    return false;
            }

            match = new BrowserPageElementMatch(selected.Ref, selected.Label, pageUrl, selected.Href);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ElementCandidate? Score(
        JsonElement element,
        int index,
        string target,
        string? contextualEntity)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var reference = ReadString(element, "ref");
        if (!Regex.IsMatch(reference, "^v[a-f0-9]{12}$", RegexOptions.CultureInvariant)) return null;
        var label = ReadString(element, "text");
        var ariaLabel = ReadString(element, "ariaLabel");
        var title = ReadString(element, "title");
        var alt = ReadString(element, "alt");
        var placeholder = ReadString(element, "placeholder");
        var href = ReadString(element, "href");
        var kind = ReadString(element, "kind");
        var normalizedTarget = NormalizeTarget(target);
        var normalizedLabel = Normalize(label);
        var normalizedAriaLabel = Normalize(ariaLabel);
        var normalizedTitle = Normalize(title);
        var normalizedAlt = Normalize(alt);
        var normalizedPlaceholder = Normalize(placeholder);
        var normalizedHref = Normalize(Uri.UnescapeDataString(href));
        var score = new[]
        {
            TextScore(normalizedLabel, normalizedTarget),
            TextScore(normalizedAriaLabel, normalizedTarget) - 2,
            TextScore(normalizedTitle, normalizedTarget) - 4,
            TextScore(normalizedAlt, normalizedTarget) - 4,
            TextScore(normalizedPlaceholder, normalizedTarget) - 5,
        }.Max();

        if (IsSearchResultTarget(normalizedTarget) &&
            string.Equals(kind, "search-result", StringComparison.Ordinal))
            score = Math.Max(score, 100);

        if (ContainsAny(normalizedTarget, "个人主页", "主页"))
        {
            if (!string.IsNullOrWhiteSpace(contextualEntity) &&
                (ContainsNormalized(label, contextualEntity) || ContainsNormalized(href, contextualEntity)))
                score = Math.Max(score, 120);
            if (Regex.IsMatch(href, @"/(?:user|u|profile|people|n)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                score = Math.Max(score, string.IsNullOrWhiteSpace(contextualEntity) ? 35 : score + 20);
        }
        if (ContainsAny(normalizedTarget, "相册", "照片", "图片") &&
            ContainsAny($"{normalizedLabel}{normalizedHref}", "相册", "照片", "图片", "album", "photo", "pic"))
            score = Math.Max(score, 80);
        if (ContainsAny(normalizedTarget, "作品", "视频", "动态") &&
            ContainsAny($"{normalizedLabel}{normalizedHref}", "作品", "视频", "动态", "video", "post"))
            score = Math.Max(score, 75);

        return score > 0 ? new ElementCandidate(reference, label, href, score, index) : null;
    }

    private static int TextScore(string source, string target)
    {
        if (source.Length == 0 || target.Length == 0) return 0;
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return 100;
        if (source.StartsWith(target, StringComparison.OrdinalIgnoreCase) ||
            source.EndsWith(target, StringComparison.OrdinalIgnoreCase)) return 85;
        if (source.Contains(target, StringComparison.OrdinalIgnoreCase)) return 70;
        if (target.Length >= 2 && target.Contains(source, StringComparison.OrdinalIgnoreCase)) return 45;
        return 0;
    }

    private static bool TryReadPage(JsonElement root, out JsonElement page)
    {
        page = default;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind != JsonValueKind.True) return false;
        if (root.TryGetProperty("page", out page) && page.ValueKind == JsonValueKind.Object) return true;
        return root.TryGetProperty("result", out var result) &&
               result.ValueKind == JsonValueKind.Object &&
               TryReadPage(result, out page);
    }

    private static string NormalizeTarget(string value)
    {
        var normalized = Normalize(value);
        normalized = OrdinalPrefix().Replace(normalized, string.Empty);
        foreach (var suffix in new[] { "按钮", "链接", "选项", "标签页", "照片", "图片" })
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal) && normalized.Length > suffix.Length)
                normalized = normalized[..^suffix.Length];
        }
        return normalized;
    }

    private static string Normalize(string value) =>
        Regex.Replace(value, @"[\s\p{P}\p{S}]+", string.Empty, RegexOptions.CultureInvariant);

    private static bool ContainsNormalized(string source, string target) =>
        Normalize(source).Contains(Normalize(target), StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string value, params string[] markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsSearchResultTarget(string value) =>
        value is "搜索结果" or "搜索条目" or "结果" ||
        value.EndsWith("搜索结果", StringComparison.Ordinal);

    private static bool TryFindPagination(
        JsonElement elements,
        string pageUrl,
        int pageNumber,
        out BrowserPageElementMatch match)
    {
        match = default!;
        var number = pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var candidates = elements.EnumerateArray()
            .Select((element, index) => ScorePagination(element, index, number))
            .Where(static candidate => candidate is not null)
            .Select(static candidate => candidate!)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Index)
            .ToArray();
        if (candidates.Length == 0) return false;
        var selected = candidates[0];
        match = new BrowserPageElementMatch(selected.Ref, selected.Label, pageUrl, selected.Href);
        return true;
    }

    private static ElementCandidate? ScorePagination(JsonElement element, int index, string pageNumber)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var reference = ReadString(element, "ref");
        if (!Regex.IsMatch(reference, "^v[a-f0-9]{12}$", RegexOptions.CultureInvariant)) return null;
        var tag = ReadString(element, "tag");
        var role = ReadString(element, "role");
        var label = ReadString(element, "text");
        var ariaLabel = ReadString(element, "ariaLabel");
        var title = ReadString(element, "title");
        var variants = new[] { label, ariaLabel, title }
            .Select(value => Normalize(value).ToLowerInvariant())
            .Where(value => value.Length > 0)
            .ToArray();
        if (!variants.Any(value => IsPaginationLabel(value, pageNumber)))
            return null;
        if (tag is not ("a" or "button") && role is not ("link" or "button")) return null;
        var href = ReadString(element, "href");
        var score = variants.Contains(pageNumber, StringComparer.Ordinal) ? 100 : 80;
        if (Regex.IsMatch(
                href,
                @"(?:[?&](?:page|p|pn|start|offset)=|/(?:page|p)(?:/|=))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)))
            score += 20;
        return new ElementCandidate(reference, label, href, score, index);
    }

    private static bool IsPaginationLabel(string value, string pageNumber) =>
        value == pageNumber ||
        value == $"第{pageNumber}页" ||
        value == $"page{pageNumber}" ||
        value == $"page{pageNumber}{pageNumber}";

    private static bool TryFindPageStep(
        JsonElement elements,
        string pageUrl,
        string target,
        out BrowserPageElementMatch match)
    {
        match = default!;
        var next = Normalize(target) == "下一页";
        var candidates = elements.EnumerateArray()
            .Select((element, index) => ScorePageStep(element, index, next))
            .Where(static candidate => candidate is not null)
            .Select(static candidate => candidate!)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Index)
            .ToArray();
        if (candidates.Length == 0) return false;
        var selected = candidates[0];
        match = new BrowserPageElementMatch(selected.Ref, selected.Label, pageUrl, selected.Href);
        return true;
    }

    private static ElementCandidate? ScorePageStep(JsonElement element, int index, bool next)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var reference = ReadString(element, "ref");
        if (!Regex.IsMatch(reference, "^v[a-f0-9]{12}$", RegexOptions.CultureInvariant)) return null;
        var tag = ReadString(element, "tag");
        var role = ReadString(element, "role");
        if (tag is not ("a" or "button") && role is not ("link" or "button")) return null;
        var label = ReadString(element, "text");
        var values = new[]
        {
            label,
            ReadString(element, "ariaLabel"),
            ReadString(element, "title"),
            ReadString(element, "rel"),
        }.Select(value => Normalize(value).ToLowerInvariant()).Where(value => value.Length > 0).ToArray();
        var expected = next
            ? new[] { "下一页", "下页", "next", "nextpage" }
            : new[] { "上一页", "上页", "previous", "previouspage", "prev" };
        var score = values.Any(value => expected.Contains(value, StringComparer.Ordinal)) ? 120 : 0;
        if (score == 0 && values.Any(value => expected.Any(term => value.Contains(term, StringComparison.Ordinal))))
            score = 80;
        if (string.Equals(ReadString(element, "rel"), next ? "next" : "prev", StringComparison.OrdinalIgnoreCase))
            score = 140;
        return score > 0
            ? new ElementCandidate(reference, label, ReadString(element, "href"), score, index)
            : null;
    }

    private static bool IsPageStepTarget(string value) => Normalize(value) is "下一页" or "上一页";

    private static string ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    [GeneratedRegex(@"^第[0-9一二两三四五六七八九十]+(?:个|张|项|条|位|页)", RegexOptions.CultureInvariant)]
    private static partial Regex OrdinalPrefix();

    [GeneratedRegex(@"^第[0-9一二两三四五六七八九十]+(?:个)?(?:页面|页码|页)$", RegexOptions.CultureInvariant)]
    private static partial Regex PaginationCommandPattern();

    private sealed record ElementCandidate(string Ref, string Label, string Href, int Score, int Index);
}
