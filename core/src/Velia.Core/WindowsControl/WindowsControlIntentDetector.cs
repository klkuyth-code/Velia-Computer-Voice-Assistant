using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Velia.Core.WindowsControl;

internal static class WindowsControlIntentDetector
{
    private static readonly string[] Denials =
    [
        "不要", "不用", "无需", "不许", "禁止", "别打开", "别启动", "别运行", "别关闭", "别退出",
        "别关掉", "别调", "别设置", "别静音", "别最小化", "别缩小", "别恢复", "别最大化", "不想让你",
    ];

    private static readonly string[] InstructionalQueries = ["如何", "怎么", "怎样", "教程", "教我"];
    private static readonly string[] OpenActions = ["打开", "启动", "运行"];
    private static readonly string[] CloseActions = ["关闭", "退出", "关掉", "关了"];
    private static readonly string[] MinimizeActions = ["最小化", "缩小", "收起"];
    private static readonly string[] RestoreActions = ["恢复", "还原", "显示出来", "调出来", "切换到", "回到", "放到前面", "置前"];
    private static readonly string[] MaximizeActions = ["最大化", "放大"];
    private static readonly string[] VolumeReferences = ["音量", "声音"];
    private static readonly string[] SetVolumeActions = ["调到", "调成", "设为", "设置为", "设置成"];
    private static readonly string[] IncreaseVolumeActions = ["调高", "提高", "增大", "调大", "大一点"];
    private static readonly string[] DecreaseVolumeActions = ["调低", "降低", "减小", "调小", "小一点"];
    private static readonly string[] TimerReferences = ["计时器", "倒计时", "计时", "提醒我"];
    private static readonly string[] CancelTimerActions = ["取消", "停止", "关闭", "关掉", "不要了"];

    private static readonly Dictionary<string, string[]> ApplicationAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [WindowsApplicationIds.Notepad] = ["记事本", "Notepad"],
            [WindowsApplicationIds.Calculator] = ["计算器", "Calculator"],
            [WindowsApplicationIds.FileExplorer] = ["文件资源管理器", "资源管理器", "文件管理器", "Explorer"],
            [WindowsApplicationIds.Paint] = ["画图", "画板", "Paint"],
            [WindowsApplicationIds.SnippingTool] = ["截图工具", "截屏工具", "Snipping Tool"],
            [WindowsApplicationIds.Chrome] = ["谷歌浏览器", "Chrome"],
            [WindowsApplicationIds.Edge] = ["Edge", "微软浏览器"],
            [WindowsApplicationIds.WeChat] = ["微信", "WeChat", "Weixin"],
            [WindowsApplicationIds.Word] = ["Word", "Wod", "沃德", "Word 文档", "Wod 文档", "沃德文档"],
            [WindowsApplicationIds.Excel] = ["Excel", "Ex", "Excel 表格", "Ex 表格", "电子表格"],
            [WindowsApplicationIds.PowerPoint] = ["PowerPoint", "PPT", "幻灯片"],
            [WindowsApplicationIds.MicrosoftWord] = ["微软 Word", "微软Word", "Microsoft Word", "MicrosoftWord"],
            [WindowsApplicationIds.MicrosoftExcel] = ["微软 Excel", "微软Excel", "Microsoft Excel", "MicrosoftExcel"],
            [WindowsApplicationIds.MicrosoftPowerPoint] = ["微软 PowerPoint", "微软PowerPoint", "Microsoft PowerPoint", "MicrosoftPowerPoint"],
            [WindowsApplicationIds.Qq] = ["QQ", "腾讯 QQ"],
            [WindowsApplicationIds.WeCom] = ["企业微信", "WeCom", "WXWork"],
            [WindowsApplicationIds.DingTalk] = ["钉钉", "DingTalk"],
            [WindowsApplicationIds.Feishu] = ["飞书", "Feishu", "Lark"],
            [WindowsApplicationIds.TencentMeeting] = ["腾讯会议", "VooV Meeting", "WeMeet"],
            [WindowsApplicationIds.WpsWriter] = ["WPS 文字", "WPS文字", "WPS Writer", "WPS文档", "WPS Office", "WPS"],
            [WindowsApplicationIds.WpsSpreadsheets] = ["WPS 表格", "WPS表格", "WPS Spreadsheets"],
            [WindowsApplicationIds.WpsPresentation] = ["WPS 演示", "WPS演示", "WPS Presentation"],
            [WindowsApplicationIds.VsCode] = ["Visual Studio Code", "VS Code", "VSCODE"],
        };

    public static bool IsExplicitCommand(string request, WindowsControlCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentNullException.ThrowIfNull(command);
        if (IsBlocked(request)) return false;
        return command.Action switch
        {
            WindowsControlActions.OpenApplication =>
                command.ApplicationId is not null &&
                ContainsAny(request, OpenActions) &&
                MentionsApplication(request, command.ApplicationId) &&
                !HasConcreteDesktopFileTarget(request) &&
                !(IsBrowserApplication(command.ApplicationId) &&
                  BrowserNavigationIntentDetector.HasExplicitWebTarget(request)),
            WindowsControlActions.CloseApplication =>
                command.ApplicationId is not null &&
                ContainsAny(request, CloseActions) &&
                MentionsApplication(request, command.ApplicationId),
            WindowsControlActions.MinimizeApplication =>
                command.ApplicationId is not null &&
                ContainsAny(request, MinimizeActions) &&
                MentionsApplication(request, command.ApplicationId),
            WindowsControlActions.RestoreApplication =>
                command.ApplicationId is not null &&
                ContainsAny(request, RestoreActions) &&
                MentionsApplication(request, command.ApplicationId),
            WindowsControlActions.MaximizeApplication =>
                command.ApplicationId is not null &&
                ContainsAny(request, MaximizeActions) &&
                MentionsApplication(request, command.ApplicationId),
            WindowsControlActions.SetVolume =>
                command.Value is >= 0 and <= 100 &&
                ContainsAny(request, VolumeReferences) &&
                ContainsAny(request, SetVolumeActions) &&
                ContainsNumber(request, command.Value.Value),
            WindowsControlActions.AdjustVolume => IsExplicitVolumeAdjustment(request, command.Value),
            WindowsControlActions.Mute =>
                ContainsAny(request, VolumeReferences) && request.Contains("静音", StringComparison.OrdinalIgnoreCase) &&
                !ContainsAny(request, ["取消静音", "解除静音", "恢复声音"]),
            WindowsControlActions.Unmute => IsExplicitUnmute(request),
            WindowsControlActions.StartTimer =>
                command.DurationSeconds is >= 1 and <= 86_400 &&
                ContainsAny(request, TimerReferences) &&
                TryParseDurationSeconds(request, out var seconds) && seconds == command.DurationSeconds.Value,
            WindowsControlActions.CancelTimer =>
                ContainsAny(request, TimerReferences) && ContainsAny(request, CancelTimerActions),
            _ => false,
        };
    }

    public static bool TryParseExplicitCommand(string request, out WindowsControlCommand command)
        => TryParseExplicitCommand(request, null, out command);

    internal static bool TryParseExplicitCommand(
        string request,
        IInstalledApplicationCatalog? installedApplications,
        out WindowsControlCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        command = new WindowsControlCommand(string.Empty);
        if (IsBlocked(request)) return false;

        var mentionedApplications = ResolveMentionedApplicationIds(request);
        if (mentionedApplications.Length == 1)
        {
            var requestedActions = new List<string>(4);
            if (ContainsAny(request, OpenActions)) requestedActions.Add(WindowsControlActions.OpenApplication);
            if (ContainsAny(request, CloseActions)) requestedActions.Add(WindowsControlActions.CloseApplication);
            if (ContainsAny(request, MinimizeActions)) requestedActions.Add(WindowsControlActions.MinimizeApplication);
            if (ContainsAny(request, RestoreActions)) requestedActions.Add(WindowsControlActions.RestoreApplication);
            if (ContainsAny(request, MaximizeActions)) requestedActions.Add(WindowsControlActions.MaximizeApplication);
            var isBrowserApplication = IsBrowserApplication(mentionedApplications[0]);
            if (requestedActions.Contains(WindowsControlActions.OpenApplication, StringComparer.Ordinal) &&
                ((isBrowserApplication && BrowserNavigationIntentDetector.HasExplicitWebTarget(request)) ||
                 HasConcreteDesktopFileTarget(request)))
            {
                requestedActions.Remove(WindowsControlActions.OpenApplication);
            }
            if (requestedActions.Count == 1)
            {
                command = new WindowsControlCommand(
                    requestedActions[0],
                    ApplicationId: mentionedApplications[0]);
                return IsExplicitCommand(request, command);
            }
        }

        if (mentionedApplications.Length == 0 && installedApplications is not null &&
            TryParseInstalledApplicationCommand(request, installedApplications, out command))
            return true;

        if (IsExplicitUnmute(request))
        {
            command = new WindowsControlCommand(WindowsControlActions.Unmute);
            return true;
        }
        if (ContainsAny(request, VolumeReferences) &&
            request.Contains("静音", StringComparison.OrdinalIgnoreCase))
        {
            command = new WindowsControlCommand(WindowsControlActions.Mute);
            return IsExplicitCommand(request, command);
        }
        if (ContainsAny(request, VolumeReferences) && TryParseFirstNumber(request, out var volumeValue))
        {
            if (ContainsAny(request, SetVolumeActions) && volumeValue is >= 0 and <= 100)
            {
                command = new WindowsControlCommand(WindowsControlActions.SetVolume, Value: volumeValue);
                return IsExplicitCommand(request, command);
            }
            if (volumeValue is >= 1 and <= 100 &&
                (ContainsAny(request, IncreaseVolumeActions) || ContainsAny(request, DecreaseVolumeActions)))
            {
                command = new WindowsControlCommand(
                    WindowsControlActions.AdjustVolume,
                    Value: ContainsAny(request, IncreaseVolumeActions) ? volumeValue : -volumeValue);
                return IsExplicitCommand(request, command);
            }
        }
        if (ContainsAny(request, VolumeReferences) && request.Contains("一点", StringComparison.Ordinal))
        {
            if (ContainsAny(request, IncreaseVolumeActions))
                command = new WindowsControlCommand(WindowsControlActions.AdjustVolume, Value: 10);
            else if (ContainsAny(request, DecreaseVolumeActions))
                command = new WindowsControlCommand(WindowsControlActions.AdjustVolume, Value: -10);
            if (!string.IsNullOrEmpty(command.Action) && IsExplicitCommand(request, command)) return true;
        }

        if (ContainsAny(request, TimerReferences))
        {
            if (ContainsAny(request, CancelTimerActions) && !TryParseDurationSeconds(request, out _))
            {
                command = new WindowsControlCommand(WindowsControlActions.CancelTimer);
                return IsExplicitCommand(request, command);
            }
            if (TryParseDurationSeconds(request, out var durationSeconds))
            {
                command = new WindowsControlCommand(WindowsControlActions.StartTimer, DurationSeconds: durationSeconds);
                return IsExplicitCommand(request, command);
            }
        }

        command = new WindowsControlCommand(string.Empty);
        return false;
    }

    internal static bool TryParseInstalledApplicationCommand(
        string request,
        IInstalledApplicationCatalog installedApplications,
        out WindowsControlCommand command)
    {
        command = new WindowsControlCommand(string.Empty);
        if (HasConcreteDesktopFileTarget(request) || BrowserNavigationIntentDetector.HasExplicitWebTarget(request))
            return false;

        var requestedActions = new List<(string Action, string[] Markers)>(5);
        if (ContainsAny(request, OpenActions)) requestedActions.Add((WindowsControlActions.OpenApplication, OpenActions));
        if (ContainsAny(request, CloseActions)) requestedActions.Add((WindowsControlActions.CloseApplication, CloseActions));
        if (ContainsAny(request, MinimizeActions)) requestedActions.Add((WindowsControlActions.MinimizeApplication, MinimizeActions));
        if (ContainsAny(request, RestoreActions)) requestedActions.Add((WindowsControlActions.RestoreApplication, RestoreActions));
        if (ContainsAny(request, MaximizeActions)) requestedActions.Add((WindowsControlActions.MaximizeApplication, MaximizeActions));
        if (requestedActions.Count != 1) return false;

        var target = ExtractDynamicApplicationName(request, requestedActions[0].Markers);
        if (target is null) return false;
        var resolution = installedApplications.Resolve(target);
        if (resolution is not { State: InstalledApplicationResolutionState.Found, Entry: { } entry })
            return false;
        command = new WindowsControlCommand(requestedActions[0].Action, ApplicationId: entry.Id);
        return true;
    }

    private static string? ExtractDynamicApplicationName(string request, IReadOnlyList<string> actionMarkers)
    {
        var value = request.Normalize(NormalizationForm.FormKC).Trim();
        foreach (var prefix in new[] { "请", "麻烦", "帮我", "帮忙", "现在", "然后", "接着", "继续", "再" })
        {
            while (value.StartsWith(prefix, StringComparison.Ordinal) && value.Length > prefix.Length)
                value = value[prefix.Length..].TrimStart(' ', '，', ',');
        }
        foreach (var marker in actionMarkers.OrderByDescending(static marker => marker.Length))
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            value = string.Concat(value.AsSpan(0, index), value.AsSpan(index + marker.Length)).Trim();
            break;
        }
        value = Regex.Replace(
            value,
            "^(?:把|将)?(?:这个|那个|当前)?(?:名为|叫做)?",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();
        value = Regex.Replace(
            value,
            "^(?:当前)?桌面(?:上|里|中)?(?:的|上面的|里面的)?(?:这个|那个|一个)?\\s*",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();
        value = Regex.Replace(
            value,
            "(?:的)?(?:窗口|软件|应用|程序)?(?:一下|下)?(?:吧|好吗|可以吗)?[。！!？?，,；;：:]*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim(' ', '“', '”', '‘', '’', '\'', '"');
        return value.Length is >= 1 and <= 120 ? value : null;
    }

    private static bool HasConcreteDesktopFileTarget(string request)
    {
        if (!DesktopFileOpenIntentDetector.TryParseExplicitCommand(request, out var file)) return false;
        var target = file.RequestedName.Trim();
        if (Path.HasExtension(target) ||
            target.Contains("文件夹", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("文件", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("文档", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("表格", StringComparison.OrdinalIgnoreCase))
            return true;

        // “桌面的 WPS” is a legitimate shortcut launch, while “桌面的 WPS
        // 工作记录” names a desktop item whose title merely contains an app
        // word. A contained static alias must not consume the remaining title.
        // Dynamic applications still fall through and are accepted only when
        // the installed-app catalog resolves the complete target uniquely.
        foreach (var alias in ApplicationAliases.Values.SelectMany(static aliases => aliases)
                     .OrderByDescending(static alias => alias.Length))
        {
            if (target.Equals(alias, StringComparison.OrdinalIgnoreCase)) return false;
            if (!target.StartsWith(alias, StringComparison.OrdinalIgnoreCase)) continue;
            var remainder = target[alias.Length..].Trim();
            if (remainder is "窗口" or "软件" or "应用" or "程序" or "客户端") return false;
            if (remainder.Length > 0) return true;
        }
        return false;
    }

    internal static bool TryParseDurationSeconds(string request, out int seconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        long total = 0;
        var found = false;
        foreach (Match match in Regex.Matches(
                     request,
                     @"(?<value>\d{1,5})\s*(?<unit>小时|分钟|分|秒钟|秒)",
                     RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(50)))
        {
            if (!int.TryParse(match.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                continue;
            found = true;
            total += match.Groups["unit"].Value switch
            {
                "小时" => value * 3_600L,
                "分钟" or "分" => value * 60L,
                _ => value,
            };
        }
        if (request.Contains("半小时", StringComparison.Ordinal)) { total += 1_800; found = true; }
        if (request.Contains("半分钟", StringComparison.Ordinal)) { total += 30; found = true; }
        seconds = total is >= 1 and <= 86_400 ? (int)total : 0;
        return found && seconds > 0;
    }

    private static bool IsExplicitVolumeAdjustment(string request, int? value)
    {
        if (value is null or 0 or < -100 or > 100 || !ContainsAny(request, VolumeReferences)) return false;
        var increasing = value > 0;
        if (increasing != ContainsAny(request, IncreaseVolumeActions)) return false;
        if (!increasing && !ContainsAny(request, DecreaseVolumeActions)) return false;
        return ContainsNumber(request, Math.Abs(value.Value)) ||
            (Math.Abs(value.Value) == 10 && request.Contains("一点", StringComparison.Ordinal));
    }

    private static bool IsExplicitUnmute(string request) =>
        ((request.Contains("取消", StringComparison.Ordinal) || request.Contains("解除", StringComparison.Ordinal)) &&
         request.Contains("静音", StringComparison.Ordinal)) ||
        (request.Contains("恢复", StringComparison.Ordinal) && ContainsAny(request, VolumeReferences));

    private static bool MentionsApplication(string request, string applicationId) =>
        ResolveMentionedApplicationIds(request) is [var mentioned] &&
        string.Equals(mentioned, applicationId, StringComparison.Ordinal);

    private static string[] ResolveMentionedApplicationIds(string request)
    {
        var aliasMatches = ApplicationAliases
            .SelectMany(pair => pair.Value
                .Where(alias => MentionsAlias(request, alias))
                .Select(alias => new { ApplicationId = pair.Key, Alias = alias }))
            .ToArray();
        return aliasMatches
            .Where(match => !aliasMatches.Any(other =>
                !string.Equals(match.ApplicationId, other.ApplicationId, StringComparison.Ordinal) &&
                other.Alias.Length > match.Alias.Length &&
                other.Alias.Contains(match.Alias, StringComparison.OrdinalIgnoreCase)))
            .Select(static match => match.ApplicationId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool MentionsAlias(string request, string alias)
    {
        if (alias.Length > 3 || alias.Any(character => !char.IsAsciiLetterOrDigit(character)))
            return request.Contains(alias, StringComparison.OrdinalIgnoreCase);
        return Regex.IsMatch(
            request,
            $@"(?<![A-Za-z0-9]){Regex.Escape(alias)}(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));
    }

    private static bool IsBrowserApplication(string applicationId) =>
        applicationId is WindowsApplicationIds.Chrome or WindowsApplicationIds.Edge;

    private static bool IsBlocked(string request) =>
        ContainsAny(request, Denials) || ContainsAny(request, InstructionalQueries);

    private static bool ContainsNumber(string request, int value)
    {
        foreach (Match match in Regex.Matches(
                     request,
                     @"(?<!\d)\d{1,3}(?!\d)",
                     RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(50)))
        {
            if (int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed == value)
                return true;
        }
        return false;
    }

    private static bool TryParseFirstNumber(string request, out int value)
    {
        value = 0;
        var match = Regex.Match(
            request,
            @"(?<!\d)(?<value>\d{1,3})(?!\d)",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));
        return match.Success && int.TryParse(
            match.Groups["value"].Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool ContainsAny(string request, IEnumerable<string> markers) =>
        markers.Any(marker => request.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
