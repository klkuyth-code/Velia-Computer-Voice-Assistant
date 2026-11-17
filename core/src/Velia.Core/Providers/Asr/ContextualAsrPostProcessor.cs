using System.Text;
using System.Text.RegularExpressions;
using Velia.Core.WindowsControl;

namespace Velia.Core.Providers.Asr;

internal interface IAsrTranscriptPostProcessor
{
    string Process(string transcript);
}

/// <summary>
/// Corrects only a bounded command target against real local state. It never
/// rewrites free conversation or text payloads, so ASR post-processing cannot
/// silently change content that the user asked Velia to type into a document.
/// </summary>
internal sealed partial class ContextualAsrPostProcessor(
    IInstalledApplicationCatalog installedApplications) : IAsrTranscriptPostProcessor
{
    public string Process(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return transcript;
        var normalized = transcript.Normalize(NormalizationForm.FormC).Trim();
        var match = ApplicationCommandPattern().Match(normalized);
        if (!match.Success) match = ObjectFirstApplicationCommandPattern().Match(normalized);
        if (!match.Success) return normalized;

        var rawTarget = CleanTarget(match.Groups["target"].Value);
        var location = DesktopLocationPrefix().Match(rawTarget);
        var target = location.Success ? rawTarget[location.Length..].TrimStart() : rawTarget;
        if (target.Length == 0 || Path.HasExtension(target))
            return normalized;
        var resolution = installedApplications.Resolve(target);
        if (resolution is not { State: InstalledApplicationResolutionState.Found, Entry: { } entry })
            return normalized;
        if (StringComparer.Ordinal.Equals(
                WindowsInstalledApplicationCatalog.NormalizeName(target),
                WindowsInstalledApplicationCatalog.NormalizeName(entry.DisplayName)))
            return normalized;

        return string.Concat(
            match.Groups["prefix"].Value,
            location.Success ? location.Value : string.Empty,
            entry.DisplayName,
            match.Groups["suffix"].Value);
    }

    private static string CleanTarget(string value) => value
        .Trim(' ', '\t', '“', '”', '‘', '’', '\'', '"')
        .Normalize(NormalizationForm.FormKC);

    [GeneratedRegex(
        "^(?<prefix>(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\\s*)*" +
        "(?:把|将)?(?:这个|那个|当前)?(?:打开|启动|运行|关闭|退出|关掉|最小化|缩小|最大化|放大|恢复|还原|切换到|回到|置前)\\s*)" +
        "(?<target>[^，。！？!?]{1,120}?)" +
        "(?<suffix>(?:的)?(?:窗口|软件|应用|程序)?(?:一下|下)?(?:吧|好吗|可以吗)?[。！!？?，,；;：:]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationCommandPattern();

    [GeneratedRegex(
        "^(?<prefix>(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\\s*)*(?:把|将)\\s*)" +
        "(?<target>[^，。！？!?]{1,120}?)" +
        "(?<suffix>\\s*(?:打开|启动|运行|关闭|退出|关掉|最小化|缩小|最大化|放大|恢复|还原|置前)" +
        "(?:一下|下)?(?:吧|好吗|可以吗)?[。！!？?，,；;：:]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObjectFirstApplicationCommandPattern();

    [GeneratedRegex(
        "^(?:当前)?桌面(?:上|里|中)?(?:的|上面的|里面的)?(?:这个|那个|一个)?\\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DesktopLocationPrefix();
}
