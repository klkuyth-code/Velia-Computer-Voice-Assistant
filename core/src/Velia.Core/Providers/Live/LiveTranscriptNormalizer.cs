using System.Text.RegularExpressions;

namespace Velia.Core.Providers.Live;

internal static partial class LiveTranscriptNormalizer
{
    [GeneratedRegex(@"(?<=[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF])\s+(?=[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF])", RegexOptions.CultureInvariant)]
    private static partial Regex CjkCharacterGap();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedHorizontalWhitespace();

    public static string Normalize(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return "";
        var normalized = transcript.Replace('\u3000', ' ').Trim();
        normalized = CjkCharacterGap().Replace(normalized, "");
        return RepeatedHorizontalWhitespace().Replace(normalized, " ");
    }
}
