using System.Text;
using System.Text.RegularExpressions;

namespace Velia.Core.WindowsControl;

internal static class DesktopFileOpenIntentDetector
{
    private static readonly string[] Denials =
    [
        "不要", "不用", "无需", "不许", "禁止", "别打开", "别启动", "别运行", "不想让你",
    ];

    private static readonly string[] InstructionalQueries = ["如何", "怎么", "怎样", "教程", "教我"];
    private static readonly string[] OpenActions = ["打开", "启动", "运行"];

    public static bool TryParseExplicitCommand(string request, out DesktopFileOpenCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        command = new DesktopFileOpenCommand(string.Empty);
        if (ContainsAny(request, Denials) || ContainsAny(request, InstructionalQueries) ||
            !ContainsAny(request, OpenActions)) return false;

        var desktopIndex = request.IndexOf("桌面", StringComparison.OrdinalIgnoreCase);
        if (desktopIndex < 0 && TryParseRecentDocumentReference(request, out var recentTarget))
        {
            command = new DesktopFileOpenCommand(recentTarget);
            return true;
        }
        if (desktopIndex < 0) return false;
        var target = request[(desktopIndex + "桌面".Length)..].Trim();
        target = Regex.Replace(
            target,
            @"^(?:上|里|中)?(?:的|上面的|里面的)?(?:这个|那个|一个)?\s*",
            string.Empty,
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));
        target = Regex.Replace(
            target,
            @"\s*(?:打开|启动|运行)?(?:一下|下)?(?:吧|好吗|可以吗)?[。！!？?，,；;：:]*$",
            string.Empty,
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));
        target = target.Trim(' ', '\t', '\r', '\n', '“', '”', '‘', '’', '\'', '"', '。', '！', '？', '，', ',', '.', '：', ':');
        if (target.Length is < 1 or > 240 || target.Contains("..", StringComparison.Ordinal) ||
            target.IndexOfAny(['\\', '/', '*', '?', '<', '>', '|']) >= 0) return false;

        command = new DesktopFileOpenCommand(target.Normalize(NormalizationForm.FormKC));
        return true;
    }

    public static string GetCanonicalKey(DesktopFileOpenCommand command) =>
        $"desktop-file:{Normalize(command.RequestedName)}";

    private static bool TryParseRecentDocumentReference(string request, out string target)
    {
        target = string.Empty;
        var normalized = request.Normalize(NormalizationForm.FormKC).Trim()
            .TrimEnd('。', '！', '!', '？', '?', '，', ',');
        if (!ContainsAny(normalized, ["刚才", "之前", "上一个", "最近"]) ||
            !ContainsAny(normalized, ["文档", "文件", "表格", "Word", "Wod", "Excel", "记事本"]))
            return false;

        var match = Regex.Match(
            normalized,
            @"(?:重新|再次|再)?(?:打开|启动|运行)\s*(?<target>(?:刚才|之前|上一个|最近)(?:的|那个|那个的)?\s*(?:Word|Wod|Excel|记事本)?\s*(?:文档|文件|表格)?)\s*(?:一下|下)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));
        if (!match.Success) return false;
        target = match.Groups["target"].Value.Trim();
        return target.Length is >= 2 and <= 40;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static bool ContainsAny(string request, IEnumerable<string> markers) =>
        markers.Any(marker => request.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
