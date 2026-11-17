namespace Velia.Core.Tasks;

/// <summary>
/// Resolves only explicit mission-control language. Ambiguous utterances are
/// never guessed into a correction while work is running.
/// </summary>
internal static class MissionIntentRouter
{
    private static readonly string[] StopCommands =
    [
        "停止", "停下", "停一下", "取消", "取消任务", "终止", "终止任务", "别做了", "不要继续了",
    ];

    private static readonly string[] ProgressCommands =
    [
        "进度", "任务进度", "现在进度", "做到哪了", "进行到哪了", "现在到哪一步了",
    ];

    private static readonly string[] AppendPrefixes =
    [
        "补充", "再补充", "另外", "还有一个要求", "加上", "记得", "然后", "接着",
        "随后", "之后", "下一步", "再", "继续", "顺便", "同时", "并且", "不要", "别",
        "禁止", "先别", "不能", "不允许",
    ];

    private static readonly string[] ReplacePrefixes =
    [
        "改成", "改为", "改名为", "更名为", "纠正", "更正", "不对", "不是", "不是这个",
        "不要这样", "重新来", "换成",
    ];

    private static readonly string[] NewMissionPrefixes =
    [
        "新任务", "换个任务", "另外一件事", "另一件事", "不做这个了", "接下来帮我",
    ];

    private static readonly string[] NaturalActionPrefixes =
    [
        "点", "点击", "点开", "选择", "打开", "进入", "查看", "访问", "关闭", "输入", "填写", "保存", "滚动",
        "返回", "前进", "刷新", "切换标签页", "搜索", "搜一下", "搜一搜", "搜", "重命名", "更名", "改名",
    ];

    private static readonly string[] ApplicationTargets =
    [
        "文件资源管理器", "谷歌浏览器", "Microsoft Edge", "PowerPoint", "资源管理器",
        "网易云音乐", "Chrome", "Edge", "浏览器", "记事本", "计算器", "画图",
        "Word", "Excel", "PPT", "微信", "抖音", "微博", "哔哩哔哩", "B站",
        "小红书", "知乎", "YouTube", "百度", "谷歌", "Google", "必应", "Bing",
    ];

    private static readonly string[] HighRiskTerms =
    [
        "删除", "清空", "永久移除", "格式化", "付款", "支付", "购买", "下单", "结算",
        "提交", "发送", "发布", "上传", "密码", "验证码", "恢复码", "密钥", "登录账号",
        "退出账号", "注册账号", "管理员权限", "UAC", "关闭防火墙", "关闭杀毒",
        "修改安全设置", "修改账号",
    ];

    private static readonly string[] RiskNegations =
    [
        "不要", "别", "禁止", "无需", "不用", "不准", "先别", "不可", "不能", "不允许",
    ];

    public static MissionRelationship Resolve(
        string request,
        string requestedRelationship,
        MissionSnapshot? current,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        requestedRelationship = string.IsNullOrWhiteSpace(requestedRelationship)
            ? "auto"
            : requestedRelationship.Trim();

        var normalized = Normalize(request);
        if (!StringComparer.Ordinal.Equals(requestedRelationship, "auto"))
        {
            var explicitRelationship = ParseExplicit(requestedRelationship);
            if (current is not null &&
                explicitRelationship is MissionRelationship.Append or MissionRelationship.Replace)
            {
                if (HasHighRiskContinuation(normalized)) return MissionRelationship.New;
                if (current.State == MissionLifecycleState.Completed &&
                    (!MissionContinuationPolicy.CanReuseCompleted(
                         current,
                         now ?? TimeProvider.System.GetUtcNow()) ||
                     IsNewMissionSignal(normalized) ||
                     IsExplicitTargetSwitch(normalized, current)))
                {
                    return MissionRelationship.New;
                }
            }
            return explicitRelationship;
        }

        if (StopCommands.Contains(normalized, StringComparer.Ordinal)) return MissionRelationship.Stop;
        if (ProgressCommands.Contains(normalized, StringComparer.Ordinal)) return MissionRelationship.Progress;
        if (current is null) return MissionRelationship.New;

        if (current.State == MissionLifecycleState.Completed)
        {
            if (!MissionContinuationPolicy.CanReuseCompleted(
                    current,
                    now ?? TimeProvider.System.GetUtcNow()) ||
                HasHighRiskContinuation(normalized) ||
                IsNewMissionSignal(normalized) ||
                IsExplicitTargetSwitch(normalized, current))
            {
                return MissionRelationship.New;
            }

            if (ReplacePrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal)))
                return MissionRelationship.Replace;
            if (AppendPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal)) ||
                LooksLikeNaturalContinuation(normalized))
            {
                return MissionRelationship.Append;
            }
            return MissionRelationship.New;
        }

        if (IsTerminal(current.State)) return MissionRelationship.New;

        // A mission grants only bounded, low-risk continuation context. Payment,
        // deletion, credential, publishing and security changes require a fresh
        // task boundary instead of inheriting the existing target binding.
        if (HasHighRiskContinuation(normalized)) return MissionRelationship.New;

        if (IsNewMissionSignal(normalized) || IsExplicitTargetSwitch(normalized, current))
            return MissionRelationship.Replace;
        if (ReplacePrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal)))
            return MissionRelationship.Replace;
        if (AppendPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal)))
            return MissionRelationship.Append;

        if (current?.State == MissionLifecycleState.AwaitingInput) return MissionRelationship.Append;
        if (LooksLikeNaturalContinuation(normalized)) return MissionRelationship.Append;
        return MissionRelationship.New;
    }

    internal static bool IsContextualCorrection(string request)
    {
        var normalized = Normalize(request);
        return ReplacePrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal));
    }

    internal static bool IsNewMissionSignal(string request)
    {
        var normalized = Normalize(request);
        return NewMissionPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal));
    }

    internal static bool IsExplicitTargetSwitch(string request, MissionSnapshot? current)
    {
        var normalized = Normalize(request);
        if (LooksLikeMenuAction(normalized)) return false;
        var action = StripLeadIn(normalized);
        if (!(action.Contains("切换到", StringComparison.Ordinal) ||
              action.Contains("转到", StringComparison.Ordinal) ||
              action.Contains("回到", StringComparison.Ordinal) ||
              action.Contains("改用", StringComparison.Ordinal) ||
              action.Contains("换到", StringComparison.Ordinal) ||
              action.StartsWith("打开", StringComparison.Ordinal) ||
              action.StartsWith("启动", StringComparison.Ordinal) ||
              action.StartsWith("进入", StringComparison.Ordinal)))
        {
            return false;
        }

        var mentioned = ApplicationTargets.FirstOrDefault(
            target => normalized.Contains(target, StringComparison.OrdinalIgnoreCase));
        if (mentioned is null && !normalized.Contains("窗口", StringComparison.Ordinal) &&
            !normalized.Contains("应用", StringComparison.Ordinal) &&
            !normalized.Contains("软件", StringComparison.Ordinal) &&
            !normalized.Contains("程序", StringComparison.Ordinal))
        {
            return false;
        }

        var currentTarget = current?.Context.TargetApplication ?? current?.Context.TargetWindow;
        return mentioned is not null || currentTarget is not null;
    }

    private static string StripLeadIn(string request)
    {
        var result = request;
        var prefixes = new[]
        {
            "请", "帮我", "麻烦", "然后", "接着", "随后", "之后", "下一步", "再", "继续",
        };
        for (var pass = 0; pass < 3; pass++)
        {
            var prefix = prefixes.FirstOrDefault(
                candidate => result.StartsWith(candidate, StringComparison.Ordinal));
            if (prefix is null) break;
            result = result[prefix.Length..].TrimStart(' ', '，', ',');
        }
        return result;
    }

    internal static bool HasHighRiskContinuation(string request)
    {
        var normalized = Normalize(request);
        foreach (var term in HighRiskTerms)
        {
            var searchIndex = 0;
            while (searchIndex < normalized.Length)
            {
                var index = normalized.IndexOf(term, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                var prefixStart = Math.Max(0, index - 8);
                var prefix = normalized[prefixStart..index];
                if (!RiskNegations.Any(negation => prefix.Contains(negation, StringComparison.Ordinal)))
                    return true;
                searchIndex = index + term.Length;
            }
        }
        return false;
    }

    private static bool LooksLikeNaturalContinuation(string request)
    {
        if (request.Length > 96) return false;
        var withoutPoliteness = request;
        foreach (var prefix in new[] { "请", "帮我", "麻烦", "你再", "再帮我" })
        {
            if (!withoutPoliteness.StartsWith(prefix, StringComparison.Ordinal)) continue;
            withoutPoliteness = withoutPoliteness[prefix.Length..].TrimStart();
            break;
        }
        return NaturalActionPrefixes.Any(
            prefix => withoutPoliteness.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool LooksLikeMenuAction(string request) =>
        request.Contains("文件菜单", StringComparison.Ordinal) ||
        request.Contains("编辑菜单", StringComparison.Ordinal) ||
        request.Contains("查看菜单", StringComparison.Ordinal) ||
        request.Contains("视图菜单", StringComparison.Ordinal) ||
        request.Contains("格式菜单", StringComparison.Ordinal) ||
        request.Contains("帮助菜单", StringComparison.Ordinal) ||
        request.Contains("工具菜单", StringComparison.Ordinal);

    private static MissionRelationship ParseExplicit(string value) => value switch
    {
        "new" => MissionRelationship.New,
        "append" => MissionRelationship.Append,
        "replace" => MissionRelationship.Replace,
        "progress" => MissionRelationship.Progress,
        "stop" => MissionRelationship.Stop,
        _ => throw new ArgumentException("不支持的任务关系。", nameof(value)),
    };

    private static string Normalize(string value) => value.Trim().TrimEnd('。', '！', '!', '？', '?', '，', ',').Trim();

    private static bool IsTerminal(MissionLifecycleState state) =>
        state is MissionLifecycleState.Completed or MissionLifecycleState.Failed or MissionLifecycleState.Cancelled;
}
