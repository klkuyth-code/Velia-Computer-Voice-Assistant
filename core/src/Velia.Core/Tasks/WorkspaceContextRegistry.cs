using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Velia.Core.Tasks;

internal enum WorkspaceResourceKind
{
    Browser,
    WeChat,
    Document,
    Application,
}

internal sealed record WorkspaceContextSnapshot(
    string Key,
    WorkspaceResourceKind Kind,
    MissionContextBindings Context,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Keeps independent, short-lived context for each interactive workspace.
/// This registry is deliberately local and deterministic: it never calls a
/// model and it does not replace the bounded authority of MissionCoordinator.
/// </summary>
internal sealed class WorkspaceContextRegistry(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan BrowserLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan WeChatLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DocumentLifetime = TimeSpan.FromHours(2);
    private static readonly TimeSpan ApplicationLifetime = TimeSpan.FromMinutes(15);
    private static readonly Regex ResumePattern = new(
        @"(?:回到|切换到|切回|转到|返回到|继续(?:操作|编辑|处理|使用)?|在(?:刚才|之前|原来|当前)的)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(25));
    private static readonly Regex DocumentResumePattern = new(
        @"(?:刚才|之前|原来|当前|上一个|那个|这个)?(?:的)?(?:文档|文件|记事本|Word|Excel|表格|PPT|WPS)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(25));
    private static readonly Regex BrowserResumePattern = new(
        @"(?:浏览器|网页|页面|标签页|Chrome|Edge|谷歌浏览器)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(25));
    private static readonly Regex WeChatPattern = new(
        @"(?:微信|WeChat)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(25));
    private static readonly Regex ClosePattern = new(
        @"^(?:(?:请|帮我|麻烦|然后|接着|再)\s*)*(?:关闭|退出)(?!登录|账号)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(25));
    private static readonly Regex DocumentContinuationActionPattern = new(
        @"(?:写入|输入|填写|添加|追加|插入|替换|改为|改成|删除|删掉|去掉|移除|清空|清理|保存|存好|储存|关闭|关掉|退出)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(25));
    private static readonly Regex DocumentCreationOrOpenPattern = new(
        @"^(?:(?:请|帮我|麻烦|然后|接着|再)\s*)*(?:创建|新建|打开)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(25));

    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, WorkspaceContextSnapshot> entries =
        new(StringComparer.OrdinalIgnoreCase);
    private string? activeKey;

    public MissionContextBindings BindForRequest(
        string request,
        MissionContextBindings explicitContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentNullException.ThrowIfNull(explicitContext);

        lock (gate)
        {
            var now = clock.GetUtcNow();
            PruneExpired(now);

            var restored = explicitContext;
            if (ShouldRestore(request) && TryFindResumeTarget(request, explicitContext, out var previous))
                restored = explicitContext.RestoreMissingFrom(previous.Context);

            ObserveCore(restored, now);
            return restored;
        }
    }

    public void Observe(MissionContextBindings context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            PruneExpired(now);
            ObserveCore(context, now);
        }
    }

    public MissionContextBindings CompleteVerifiedRequest(string request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            PruneExpired(now);
            var explicitContext = MissionContextBindings.FromRequest(request);
            var context = explicitContext;
            if (ShouldRestore(request) && TryFindResumeTarget(request, explicitContext, out var previous))
                context = explicitContext.RestoreMissingFrom(previous.Context);

            if (ClosePattern.IsMatch(request.Trim()))
                InvalidateMatching(context);
            else
                ObserveCore(context, now);
            return context;
        }
    }

    public WorkspaceContextSnapshot? GetActiveSnapshot()
    {
        lock (gate)
        {
            PruneExpired(clock.GetUtcNow());
            return activeKey is not null && entries.TryGetValue(activeKey, out var active)
                ? active
                : null;
        }
    }

    public WorkspaceContextSnapshot? GetLatestSnapshot(WorkspaceResourceKind kind)
    {
        lock (gate)
        {
            PruneExpired(clock.GetUtcNow());
            return entries.Values
                .Where(snapshot => snapshot.Kind == kind &&
                    (kind != WorkspaceResourceKind.Document || IsConcreteFile(snapshot.Context.SelectedFile)))
                .OrderByDescending(static snapshot => snapshot.UpdatedAt)
                .FirstOrDefault();
        }
    }

    public void InvalidateActive(WorkspaceResourceKind kind)
    {
        lock (gate)
        {
            if (activeKey is null || !entries.TryGetValue(activeKey, out var active) || active.Kind != kind) return;
            entries.Remove(activeKey);
            activeKey = null;
        }
    }

    public void Complete(MissionSnapshot mission)
    {
        ArgumentNullException.ThrowIfNull(mission);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            PruneExpired(now);
            if (ClosePattern.IsMatch(mission.Requirements[^1].Trim()))
            {
                InvalidateMatching(mission.Context);
                return;
            }
            ObserveCore(mission.Context, now);
        }
    }

    public ImmutableArray<WorkspaceContextSnapshot> GetSnapshots()
    {
        lock (gate)
        {
            PruneExpired(clock.GetUtcNow());
            return entries.Values
                .OrderByDescending(static value => value.UpdatedAt)
                .ToImmutableArray();
        }
    }

    private bool TryFindResumeTarget(
        string request,
        MissionContextBindings explicitContext,
        out WorkspaceContextSnapshot snapshot)
    {
        var requestedKind = InferRequestedKind(request, explicitContext);
        var candidates = entries.Values.AsEnumerable();
        if (requestedKind is not null)
            candidates = candidates.Where(candidate => candidate.Kind == requestedKind);
        else if (activeKey is not null && entries.TryGetValue(activeKey, out var active))
            candidates = [active];

        if (!string.IsNullOrWhiteSpace(explicitContext.TargetApplication))
        {
            var application = NormalizeKeyPart(explicitContext.TargetApplication);
            var applicationMatches = candidates.Where(candidate =>
                NormalizeKeyPart(candidate.Context.TargetApplication ?? string.Empty) == application);
            if (applicationMatches.Any()) candidates = applicationMatches;
        }

        snapshot = candidates
            .OrderByDescending(static candidate => candidate.UpdatedAt)
            .FirstOrDefault()!;
        return snapshot is not null;
    }

    private void ObserveCore(MissionContextBindings context, DateTimeOffset now)
    {
        if (!TryDescribe(context, out var key, out var kind)) return;

        if (entries.TryGetValue(key, out var previous))
        {
            var merged = context.RestoreMissingFrom(previous.Context);
            entries[key] = previous with
            {
                Context = merged,
                Revision = previous.Revision + 1,
                UpdatedAt = now,
                ExpiresAt = now + LifetimeFor(kind),
            };
        }
        else
        {
            entries[key] = new WorkspaceContextSnapshot(
                key,
                kind,
                context,
                1,
                now,
                now,
                now + LifetimeFor(kind));
        }
        activeKey = key;
    }

    private void InvalidateMatching(MissionContextBindings context)
    {
        if (!TryDescribe(context, out var key, out var kind)) return;

        var keys = entries
            .Where(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                           (kind is WorkspaceResourceKind.Browser or WorkspaceResourceKind.WeChat &&
                            pair.Value.Kind == kind))
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (var candidate in keys) entries.Remove(candidate);
        if (activeKey is not null && keys.Contains(activeKey, StringComparer.OrdinalIgnoreCase))
            activeKey = null;
    }

    private void PruneExpired(DateTimeOffset now)
    {
        var expired = entries
            .Where(pair => pair.Value.ExpiresAt <= now)
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (var key in expired) entries.Remove(key);
        if (activeKey is not null && !entries.ContainsKey(activeKey)) activeKey = null;
    }

    private static bool ShouldRestore(string request) =>
        ResumePattern.IsMatch(request) ||
        request.Contains("刚才的文档", StringComparison.Ordinal) ||
        request.Contains("之前的文档", StringComparison.Ordinal) ||
        request.Contains("继续编辑", StringComparison.Ordinal) ||
        request.Contains("继续输入", StringComparison.Ordinal) ||
        (DocumentResumePattern.IsMatch(request) &&
         DocumentContinuationActionPattern.IsMatch(request) &&
         !DocumentCreationOrOpenPattern.IsMatch(request.Trim()));

    private static WorkspaceResourceKind? InferRequestedKind(
        string request,
        MissionContextBindings context)
    {
        if (WeChatPattern.IsMatch(request) || IsWeChat(context.TargetApplication))
            return WorkspaceResourceKind.WeChat;
        if (DocumentResumePattern.IsMatch(request) || IsDocument(context))
            return WorkspaceResourceKind.Document;
        if (BrowserResumePattern.IsMatch(request) || IsBrowser(context))
            return WorkspaceResourceKind.Browser;
        return context.TargetApplication is null ? null : WorkspaceResourceKind.Application;
    }

    private static bool TryDescribe(
        MissionContextBindings context,
        out string key,
        out WorkspaceResourceKind kind)
    {
        if (IsWeChat(context.TargetApplication))
        {
            key = "wechat";
            kind = WorkspaceResourceKind.WeChat;
            return true;
        }
        if (IsDocument(context))
        {
            var identity = IsConcreteFile(context.SelectedFile)
                ? context.SelectedFile!
                : context.TargetApplication ?? "active";
            key = $"document:{NormalizeKeyPart(identity)}";
            kind = WorkspaceResourceKind.Document;
            return true;
        }
        if (IsBrowser(context))
        {
            var identity = context.TargetApplication ?? "active";
            key = $"browser:{NormalizeBrowserIdentity(identity)}";
            kind = WorkspaceResourceKind.Browser;
            return true;
        }
        if (!string.IsNullOrWhiteSpace(context.TargetApplication))
        {
            key = $"application:{NormalizeKeyPart(context.TargetApplication)}";
            kind = WorkspaceResourceKind.Application;
            return true;
        }
        if (!string.IsNullOrWhiteSpace(context.WebPlatform))
        {
            key = "browser:active";
            kind = WorkspaceResourceKind.Browser;
            return true;
        }

        key = string.Empty;
        kind = default;
        return false;
    }

    private static bool IsBrowser(MissionContextBindings context) =>
        context.WebPlatform is not null ||
        (context.TargetApplication is { } application &&
         (application.Contains("浏览器", StringComparison.OrdinalIgnoreCase) ||
          application.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
          application.Contains("Edge", StringComparison.OrdinalIgnoreCase)));

    private static bool IsWeChat(string? application) =>
        application is not null &&
        (application.Contains("微信", StringComparison.OrdinalIgnoreCase) ||
         application.Contains("WeChat", StringComparison.OrdinalIgnoreCase));

    private static bool IsDocument(MissionContextBindings context)
    {
        if (IsConcreteFile(context.SelectedFile)) return true;
        if (context.TargetApplication is not { } application) return false;
        return application.Contains("记事本", StringComparison.OrdinalIgnoreCase) ||
               application.Contains("Word", StringComparison.OrdinalIgnoreCase) ||
               application.Contains("Excel", StringComparison.OrdinalIgnoreCase) ||
               application.Contains("PowerPoint", StringComparison.OrdinalIgnoreCase) ||
               application.Contains("PPT", StringComparison.OrdinalIgnoreCase) ||
               application.Contains("WPS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConcreteFile(string? file) =>
        !string.IsNullOrWhiteSpace(file) &&
        file is not ("选中的文件" or "当前文件" or "这个文件");

    private static string NormalizeBrowserIdentity(string value)
    {
        var normalized = NormalizeKeyPart(value);
        if (normalized.Contains("chrome", StringComparison.Ordinal) ||
            normalized.Contains("谷歌", StringComparison.Ordinal)) return "chrome";
        if (normalized.Contains("edge", StringComparison.Ordinal)) return "edge";
        return "active";
    }

    private static string NormalizeKeyPart(string value) =>
        Regex.Replace(
            value.Trim().Normalize().ToLowerInvariant(),
            @"[\s\p{P}\p{S}]",
            string.Empty,
            RegexOptions.CultureInvariant);

    private static TimeSpan LifetimeFor(WorkspaceResourceKind kind) => kind switch
    {
        WorkspaceResourceKind.Browser => BrowserLifetime,
        WorkspaceResourceKind.WeChat => WeChatLifetime,
        WorkspaceResourceKind.Document => DocumentLifetime,
        _ => ApplicationLifetime,
    };
}
