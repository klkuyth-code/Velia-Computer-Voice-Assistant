using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Velia.Core.Tasks;

internal enum MissionLifecycleState
{
    Accepted,
    Running,
    AwaitingInput,
    Correcting,
    Completed,
    Failed,
    Cancelled,
}

internal enum MissionRelationship
{
    New,
    Append,
    Replace,
    Progress,
    Stop,
}

internal sealed record MissionSnapshot(
    Guid Id,
    int Revision,
    string Origin,
    string CurrentRequest,
    ImmutableArray<string> Requirements,
    MissionContextBindings Context,
    MissionLifecycleState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Message);

/// <summary>
/// Short-lived references that are valid while the owning mission is active
/// and during its bounded completion grace period. These are descriptive
/// anchors for the model, not authority to act.
/// </summary>
internal sealed record MissionContextBindings(
    string? TargetApplication,
    string? TargetWindow,
    string? ActiveMenu,
    string? SelectedFile,
    string? LastTarget,
    string? WebPlatform,
    string? WebEntity)
{
    private static readonly string[] KnownApplications =
    [
        "文件资源管理器", "谷歌浏览器", "Microsoft Edge", "PowerPoint", "资源管理器",
        "网易云音乐", "Chrome", "Edge", "浏览器", "记事本", "计算器", "画图",
        "Word", "Excel", "PPT", "微信",
    ];

    private static readonly Regex NamedWindowPattern = new(
        @"(?:(?:在|切换到|转到|回到|进入|打开)\s*|^)(?<name>(?!(?:当前|这个|该|前台|目标|窗口|点击|点开|选择))[\p{L}\p{N}_ .-]{1,32}?)(?:的)?窗口",
        RegexOptions.CultureInvariant);

    private static readonly Regex MenuPattern = new(
        @"(?<menu>文件|编辑|查看|视图|格式|帮助|工具|插入|开始|主页)(?:菜单)?",
        RegexOptions.CultureInvariant);

    private static readonly Regex FilePattern = new(
        @"(?<file>[\p{L}\p{N}_（）()\-]{1,80}\.(?:txt|md|pdf|docx?|xlsx?|pptx?|csv|json|xml|html?|png|jpe?g|gif|webp|mp3|wav|mp4|mkv|zip|7z))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ActionTargetPattern = new(
        @"^(?:(?:改成|改为|不对[，,]?|纠正|更正|换成)\s*)?(?:(?:然后|接着|随后|之后|下一步|再|继续|请|帮我)\s*)?(?:点一下|点击|点开|点|选择|打开|关闭|输入|保存为|保存|滚动到|滚动)\s*(?<target>[^，。！？!?]{1,40})",
        RegexOptions.CultureInvariant);

    private static readonly string[] KnownWebPlatforms =
    [
        "抖音", "微博", "哔哩哔哩", "B站", "小红书", "知乎", "YouTube",
        "百度", "谷歌", "Google", "必应", "Bing",
    ];

    private static readonly Regex WebSearchEntityPattern = new(
        @"(?:搜索|搜一下|搜一搜|搜)(?<entity>[^，。！？!?]{1,60})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    private static readonly Regex ProfileEntityPattern = new(
        @"(?:(?:进入|打开|点击|查看|访问)\s*)?(?<entity>[\p{L}\p{N}·_.-]{1,32})的(?:个人)?(?:主页|相册|作品)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    public static MissionContextBindings Empty { get; } = new(null, null, null, null, null, null, null);

    public static MissionContextBindings FromRequest(string request) => Empty.Merge(request);

    public MissionContextBindings RestoreMissingFrom(MissionContextBindings previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return this with
        {
            TargetApplication = TargetApplication ?? previous.TargetApplication,
            TargetWindow = TargetWindow ?? previous.TargetWindow,
            ActiveMenu = ActiveMenu ?? previous.ActiveMenu,
            SelectedFile = SelectedFile ?? previous.SelectedFile,
            LastTarget = LastTarget ?? previous.LastTarget,
            WebPlatform = WebPlatform ?? previous.WebPlatform,
            WebEntity = WebEntity ?? previous.WebEntity,
        };
    }

    public MissionContextBindings Merge(string request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        var normalized = request.Trim();
        var application = ExtractApplication(normalized);
        var window = ExtractWindow(normalized, application);
        var menu = ExtractMenu(normalized);
        var file = ExtractFile(normalized);
        var lastTarget = ExtractLastTarget(normalized, menu, file);
        var webPlatform = ExtractWebPlatform(normalized);
        var webEntity = ExtractWebEntity(normalized);

        return this with
        {
            TargetApplication = application ?? TargetApplication,
            TargetWindow = window ?? TargetWindow,
            ActiveMenu = menu ?? ActiveMenu,
            SelectedFile = file ?? SelectedFile,
            LastTarget = lastTarget ?? LastTarget,
            WebPlatform = webPlatform ?? WebPlatform,
            WebEntity = webEntity ?? WebEntity,
        };
    }

    private static string? ExtractApplication(string request)
    {
        foreach (var application in KnownApplications)
        {
            if (request.Contains(application, StringComparison.OrdinalIgnoreCase))
                return application;
        }

        var match = NamedWindowPattern.Match(request);
        return match.Success ? CleanTarget(match.Groups["name"].Value) : null;
    }

    private static string? ExtractWindow(string request, string? application)
    {
        // A known application name is a stronger anchor than the generic
        // "...窗口" pattern. Prefer it so words such as "当前窗口" cannot
        // accidentally replace the real target with a generic label.
        if (application is not null) return application;

        var match = NamedWindowPattern.Match(request);
        if (match.Success)
        {
            var value = CleanTarget(match.Groups["name"].Value);
            if (value is not null) return value;
        }

        if (request.Contains("当前窗口", StringComparison.Ordinal)) return application ?? "当前窗口";
        return application;
    }

    private static string? ExtractMenu(string request)
    {
        var match = MenuPattern.Match(request);
        if (!match.Success) return null;

        var menu = match.Groups["menu"].Value;
        var file = ExtractFile(request);
        return StringComparer.Ordinal.Equals(menu, "文件") && file is not null ? null : menu;
    }

    private static string? ExtractFile(string request)
    {
        var match = FilePattern.Match(request);
        if (match.Success)
        {
            var file = match.Groups["file"].Value.Trim();
            var markerIndex = -1;
            var markerLength = 0;
            foreach (var marker in new[]
                     {
                         "文件名是", "文件名为", "重命名为", "更名为", "改名为", "命名为",
                         "名字叫", "名为", "叫做", "创建", "新建", "打开", "选择", "选中",
                     })
            {
                var index = file.LastIndexOf(marker, StringComparison.Ordinal);
                if (index < markerIndex) continue;
                markerIndex = index;
                markerLength = marker.Length;
            }
            if (markerIndex >= 0) file = file[(markerIndex + markerLength)..].TrimStart();
            foreach (var prefix in new[] { "桌面上的", "桌面的", "桌面上", "这个", "名为" })
            {
                if (!file.StartsWith(prefix, StringComparison.Ordinal) || file.Length == prefix.Length) continue;
                file = file[prefix.Length..].TrimStart();
                break;
            }
            file = file.Trim(' ', '“', '”', '"', '‘', '’');
            return file.Length is > 0 and <= 96 ? file : null;
        }
        if (request.Contains("选中的文件", StringComparison.Ordinal)) return "选中的文件";
        if (request.Contains("当前文件", StringComparison.Ordinal)) return "当前文件";
        if (request.Contains("这个文件", StringComparison.Ordinal)) return "这个文件";
        return null;
    }

    private static string? ExtractWebPlatform(string request) =>
        KnownWebPlatforms.FirstOrDefault(platform => request.Contains(platform, StringComparison.OrdinalIgnoreCase));

    private static string? ExtractWebEntity(string request)
    {
        var search = WebSearchEntityPattern.Match(request);
        if (search.Success)
        {
            var entity = search.Groups["entity"].Value.Trim();
            foreach (var prefix in new[] { "并打开", "并进入", "并查看", "打开", "进入", "一下", "关于" })
            {
                if (entity.StartsWith(prefix, StringComparison.Ordinal) && entity.Length > prefix.Length)
                    entity = entity[prefix.Length..].TrimStart();
            }
            return IsUsefulWebEntity(entity) ? entity : null;
        }

        var profile = ProfileEntityPattern.Match(request);
        if (!profile.Success) return null;
        var profileEntity = profile.Groups["entity"].Value.Trim();
        return IsUsefulWebEntity(profileEntity) ? profileEntity : null;
    }

    private static bool IsUsefulWebEntity(string value) =>
        value.Length is >= 1 and <= 60 &&
        value is not ("我" or "我的" or "他" or "她" or "它" or "他们" or "她们" or "这个人" or "该用户");

    private static string? ExtractLastTarget(string request, string? menu, string? file)
    {
        var match = ActionTargetPattern.Match(request);
        if (match.Success)
        {
            var value = CleanTarget(match.Groups["target"].Value);
            if (value is not null) return value.EndsWith("菜单", StringComparison.Ordinal)
                ? value[..^2]
                : value;
        }
        return menu ?? file;
    }

    private static string? CleanTarget(string value)
    {
        var cleaned = value.Trim();
        foreach (var prefix in new[] { "切换到", "转到", "回到", "进入", "打开", "在" })
        {
            if (!cleaned.StartsWith(prefix, StringComparison.Ordinal) || cleaned.Length == prefix.Length)
                continue;
            cleaned = cleaned[prefix.Length..].TrimStart();
            break;
        }
        if (cleaned.EndsWith("窗口", StringComparison.Ordinal))
            cleaned = cleaned[..^2].TrimEnd();
        if (cleaned.EndsWith('的')) cleaned = cleaned[..^1].TrimEnd();
        if (cleaned is "当前" or "这个" or "该" or "前台" or "目标" or "窗口") return null;
        return cleaned.Length is > 0 and <= 64 ? cleaned : null;
    }
}

internal static class MissionContinuationPolicy
{
    public static TimeSpan CompletedGracePeriod { get; } = TimeSpan.FromMinutes(2);

    public static bool CanReuseCompleted(MissionSnapshot mission, DateTimeOffset now)
    {
        if (mission.State != MissionLifecycleState.Completed || now < mission.UpdatedAt) return false;
        return now - mission.UpdatedAt <= CompletedGracePeriod;
    }
}

/// <summary>
/// Owns the lifecycle of the current user mission. A future semantic router
/// decides the relationship of a new utterance; this class only applies the
/// already-decided transition and never guesses from keywords.
/// </summary>
internal sealed class MissionCoordinator(
    TimeProvider? timeProvider = null,
    WorkspaceContextRegistry? workspaceContextRegistry = null)
{
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly WorkspaceContextRegistry workspaces =
        workspaceContextRegistry ?? new WorkspaceContextRegistry(timeProvider);
    private MissionSnapshot? current;

    public MissionSnapshot? GetCurrent()
    {
        lock (gate)
        {
            return current;
        }
    }

    public WorkspaceContextSnapshot? GetActiveWorkspace() => workspaces.GetActiveSnapshot();

    public WorkspaceContextSnapshot? GetLatestWorkspace(WorkspaceResourceKind kind) =>
        workspaces.GetLatestSnapshot(kind);

    public void InvalidateActiveWorkspace(WorkspaceResourceKind kind) => workspaces.InvalidateActive(kind);

    public MissionContextBindings ObserveVerifiedWorkspaceAction(string request) =>
        workspaces.CompleteVerifiedRequest(request);

    public void ObserveVerifiedDocument(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        workspaces.Observe(CreateVerifiedDocumentContext(fileName));
    }

    public void ObserveVerifiedApplication(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var normalized = displayName.Trim();
        workspaces.Observe(new MissionContextBindings(
            normalized,
            normalized,
            null,
            null,
            normalized,
            null,
            null));
    }

    public MissionSnapshot BindVerifiedDocument(Guid missionId, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        lock (gate)
        {
            if (current is null || current.Id != missionId || IsTerminal(current.State))
                throw new InvalidOperationException("The mission identifier is stale or unknown.");
            var context = CreateVerifiedDocumentContext(fileName);
            current = current with
            {
                Context = context,
                UpdatedAt = clock.GetUtcNow(),
            };
            workspaces.Observe(context);
            return current;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            current = null;
        }
    }

    public MissionSnapshot Submit(
        string request,
        string origin,
        MissionRelationship relationship = MissionRelationship.New)
    {
        request = RequireText(request, nameof(request));
        origin = RequireText(origin, nameof(origin));

        lock (gate)
        {
            if (current is null || current.State is MissionLifecycleState.Failed or MissionLifecycleState.Cancelled)
            {
                if (relationship is not MissionRelationship.New and not MissionRelationship.Replace)
                {
                    throw new InvalidOperationException("No active mission can accept this relationship.");
                }

                current = CreateMission(request, origin);
                return current;
            }

            var now = clock.GetUtcNow();
            if (current.State == MissionLifecycleState.Completed)
            {
                var startsDifferentMission = MissionIntentRouter.IsNewMissionSignal(request) ||
                    MissionIntentRouter.IsExplicitTargetSwitch(request, current);
                if (relationship == MissionRelationship.New || startsDifferentMission ||
                    !MissionContinuationPolicy.CanReuseCompleted(current, now))
                {
                    if (relationship is not MissionRelationship.New and not MissionRelationship.Replace)
                    {
                        throw new InvalidOperationException(
                            "The completed mission continuation window has expired.");
                    }

                    current = CreateMission(request, origin);
                    return current;
                }

                if (relationship is not MissionRelationship.Append and not MissionRelationship.Replace)
                {
                    throw new InvalidOperationException(
                        "A completed mission can only accept a recent low-risk continuation or correction.");
                }
            }

            if (relationship is MissionRelationship.Append or MissionRelationship.Replace &&
                MissionIntentRouter.HasHighRiskContinuation(request))
            {
                throw new InvalidOperationException(
                    "A high-risk action cannot inherit authority from the current mission.");
            }

            current = relationship switch
            {
                MissionRelationship.Append => current with
                {
                    Revision = current.Revision + 1,
                    CurrentRequest = $"{current.CurrentRequest}\n补充要求：{request}",
                    Requirements = current.Requirements.Add(request),
                    Context = workspaces.BindForRequest(request, current.Context.Merge(request)),
                    State = MissionLifecycleState.Correcting,
                    UpdatedAt = now,
                    Message = null,
                },
                MissionRelationship.Replace => Replace(current, request, now),
                MissionRelationship.Progress => current,
                MissionRelationship.Stop => current with
                {
                    Context = MissionContextBindings.Empty,
                    State = MissionLifecycleState.Cancelled,
                    UpdatedAt = now,
                    Message = request,
                },
                MissionRelationship.New => throw new InvalidOperationException(
                    "An active mission requires an explicit append, replace, progress, or stop relationship."),
                _ => throw new ArgumentOutOfRangeException(nameof(relationship)),
            };

            return current;
        }
    }

    public MissionSnapshot MarkRunning(Guid id) => Transition(id, MissionLifecycleState.Running, null);

    public MissionSnapshot AwaitInput(Guid id, string message) =>
        Transition(id, MissionLifecycleState.AwaitingInput, RequireText(message, nameof(message)));

    public MissionSnapshot Complete(Guid id, string message) =>
        Transition(id, MissionLifecycleState.Completed, RequireText(message, nameof(message)));

    public MissionSnapshot Fail(Guid id, string message) =>
        Transition(id, MissionLifecycleState.Failed, RequireText(message, nameof(message)));

    public MissionSnapshot Cancel(string reason)
    {
        reason = RequireText(reason, nameof(reason));
        lock (gate)
        {
            if (current is null || IsTerminal(current.State))
            {
                throw new InvalidOperationException("There is no active mission to cancel.");
            }

            current = current with
            {
                Context = MissionContextBindings.Empty,
                State = MissionLifecycleState.Cancelled,
                UpdatedAt = clock.GetUtcNow(),
                Message = reason,
            };
            return current;
        }
    }

    private MissionSnapshot Transition(Guid id, MissionLifecycleState state, string? message)
    {
        lock (gate)
        {
            if (current is null || current.Id != id)
            {
                throw new InvalidOperationException("The mission identifier is stale or unknown.");
            }

            if (IsTerminal(current.State))
            {
                throw new InvalidOperationException("A terminal mission cannot transition again.");
            }

            current = current with
            {
                // Keep descriptive bindings briefly after successful completion
                // so a natural follow-up such as "然后点文件" can resume the
                // same bounded mission. Failure and cancellation invalidate all
                // bindings immediately.
                Context = state is MissionLifecycleState.Failed or MissionLifecycleState.Cancelled
                    ? MissionContextBindings.Empty
                    : current.Context,
                State = state,
                UpdatedAt = clock.GetUtcNow(),
                Message = message,
            };
            if (state == MissionLifecycleState.Completed) workspaces.Complete(current);
            return current;
        }
    }

    private MissionSnapshot CreateMission(string request, string origin)
    {
        var now = clock.GetUtcNow();
        var context = workspaces.BindForRequest(request, MissionContextBindings.FromRequest(request));
        return new MissionSnapshot(
            Guid.NewGuid(),
            1,
            origin,
            request,
            [request],
            context,
            MissionLifecycleState.Accepted,
            now,
            now,
            null);
    }

    private MissionSnapshot Replace(
        MissionSnapshot mission,
        string request,
        DateTimeOffset now)
    {
        var contextualCorrection = MissionIntentRouter.IsContextualCorrection(request) &&
            !MissionIntentRouter.IsNewMissionSignal(request) &&
            !MissionIntentRouter.IsExplicitTargetSwitch(request, mission);

        return mission with
        {
            Revision = mission.Revision + 1,
            CurrentRequest = contextualCorrection
                ? $"{mission.CurrentRequest}\n纠正要求：{request}"
                : request,
            Requirements = contextualCorrection
                ? mission.Requirements.Add($"纠正：{request}")
                : [request],
            Context = contextualCorrection
                ? workspaces.BindForRequest(request, mission.Context.Merge(request))
                : workspaces.BindForRequest(request, MissionContextBindings.FromRequest(request)),
            State = MissionLifecycleState.Correcting,
            UpdatedAt = now,
            Message = null,
        };
    }

    private static bool IsTerminal(MissionLifecycleState state) =>
        state is MissionLifecycleState.Completed or MissionLifecycleState.Failed or MissionLifecycleState.Cancelled;

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static MissionContextBindings CreateVerifiedDocumentContext(string fileName) => new(
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".doc" or ".docx" => "Word",
            ".xls" or ".xlsx" => "Excel",
            _ => "记事本",
        },
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".doc" or ".docx" => "Word",
            ".xls" or ".xlsx" => "Excel",
            _ => "记事本",
        },
        null,
        fileName.Trim(),
        fileName.Trim(),
        null,
        null);
}
