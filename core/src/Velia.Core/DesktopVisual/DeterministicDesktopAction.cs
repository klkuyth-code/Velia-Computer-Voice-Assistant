using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Velia.Core.DesktopVisual;

internal static class DeterministicDesktopActionTypes
{
    public const string ClickControl = "clickControl";
    public const string SelectAll = "selectAll";
    public const string CopySelection = "copySelection";
    public const string TypeText = "typeText";
    public const string SubmitText = "submitText";
}

internal sealed record DeterministicDesktopActionCommand(
    string Action,
    string Target,
    string? Text = null,
    string? Application = null);

internal sealed record DeterministicDesktopActionResult(
    string Action,
    bool Verified,
    string State,
    string Target,
    string WindowTitle);

internal enum ActiveInteractionSurfaceKind
{
    Unknown,
    Browser,
    Desktop,
}

internal interface IDeterministicDesktopActionService
{
    Task<DeterministicDesktopActionResult> ExecuteAsync(
        DeterministicDesktopActionCommand command,
        CancellationToken cancellationToken);

    ActiveInteractionSurfaceKind GetActiveSurfaceKind() => ActiveInteractionSurfaceKind.Unknown;

    void ObserveVerifiedVisualTextEntry(DeterministicDesktopActionCommand command) { }

    void ConsumeVisualTextSubmissionAuthorization(DeterministicDesktopActionCommand command) { }
}

internal static partial class DeterministicDesktopActionIntentDetector
{
    private static readonly string[] DeniedTerms =
    [
        "不要", "别", "不许", "禁止", "无需", "不用",
    ];

    private static readonly string[] InstructionalPrefixes =
    [
        "怎么", "如何", "为什么", "能不能", "可不可以", "是否",
    ];

    private static readonly string[] UnsafeTargets =
    [
        "删除", "永久删除", "卸载", "购买", "付款", "支付", "转账", "发送",
        "提交订单", "确认订单", "验证码", "密码", "管理员", "用户帐户控制",
    ];
    private static readonly string[] UnstructuredTargetTerms =
        ["文字", "内容", "文档", "文件", "桌面", "窗口", "页面", "界面", "软件", "应用", "程序", "浏览器"];

    private static readonly HashSet<string> MenuLabels = new(StringComparer.Ordinal)
    {
        "文件", "编辑", "查看", "格式", "工具", "窗口", "帮助",
    };

    public static bool TryParseExplicitCommand(
        string request,
        out DeterministicDesktopActionCommand command)
    {
        command = new DeterministicDesktopActionCommand(string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(request)) return false;
        var rawInput = request.Normalize(NormalizationForm.FormC).Trim();
        var normalized = NormalizeRequest(request);
        if (normalized.Length is 0 or > 1_200 ||
            IsDeniedOrInstructionalPrefix(normalized))
        {
            return false;
        }

        // Parse the payload from the NFC request so Chinese punctuation and a
        // final sentence mark remain exactly as spoken. FormKC and generic
        // command punctuation trimming are only for action classification.
        var textInput = ExplicitTextInputRegex().Match(rawInput);
        if (textInput.Success)
        {
            var inputTarget = CleanInputTarget(textInput.Groups["target"].Value);
            var inputApplication = CleanInputApplication(textInput.Groups["target"].Value);
            var text = CleanInputText(textInput.Groups["text"].Value);
            if (IsValidTextInput(inputTarget, text))
            {
                command = new DeterministicDesktopActionCommand(
                    DeterministicDesktopActionTypes.TypeText,
                    inputTarget,
                    text,
                    inputApplication);
                return true;
            }
        }

        if (ExplicitSubmitRegex().IsMatch(normalized))
        {
            command = new DeterministicDesktopActionCommand(
                DeterministicDesktopActionTypes.SubmitText,
                "发送消息");
            return true;
        }

        if (normalized.Length > 160 ||
            DeniedTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal)))
            return false;

        if (DirectCopyRegex().IsMatch(normalized))
        {
            command = new DeterministicDesktopActionCommand(
                DeterministicDesktopActionTypes.CopySelection,
                "复制");
            return true;
        }
        if (DirectSelectAllRegex().IsMatch(normalized))
        {
            command = new DeterministicDesktopActionCommand(
                DeterministicDesktopActionTypes.SelectAll,
                "全选");
            return true;
        }

        var match = ExplicitClickRegex().Match(normalized);
        if (!match.Success) return false;
        var verb = match.Groups["verb"].Value;
        var target = CleanTarget(match.Groups["target"].Value);
        if (target.Length is 0 or > 48 || IsUnsafeTarget(target)) return false;

        var label = NormalizeLabel(target);
        if (label is "全选" or "全部选中" or "全都选中")
        {
            command = new DeterministicDesktopActionCommand(
                DeterministicDesktopActionTypes.SelectAll,
                "全选");
            return true;
        }
        if (label is "复制" or "拷贝")
        {
            command = new DeterministicDesktopActionCommand(
                DeterministicDesktopActionTypes.CopySelection,
                "复制");
            return true;
        }

        if (!LooksLikeStructuredControl(target, label, verb)) return false;
        command = new DeterministicDesktopActionCommand(
            DeterministicDesktopActionTypes.ClickControl,
            target);
        return true;
    }

    public static string GetCanonicalKey(DeterministicDesktopActionCommand command) =>
        $"desktop:{command.Action}:{NormalizeLabel(command.Target)}:{NormalizeLabel(command.Application ?? string.Empty)}:{HashText(command.Text)}";

    private static bool IsDeniedOrInstructionalPrefix(string value)
    {
        var compact = value.TrimStart();
        return InstructionalPrefixes.Any(prefix => compact.StartsWith(prefix, StringComparison.Ordinal)) ||
               DeniedTerms.Any(term => compact.StartsWith(term, StringComparison.Ordinal));
    }

    private static bool IsValidTextInput(string target, string text)
    {
        if (target.Length is 0 or > 48 || text.Length is 0 or > 1_024 ||
            text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            return false;
        if (Regex.IsMatch(
                target,
                "密码|验证码|支付|付款|转账|账号|登录|注册|管理员",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            return false;
        return !Regex.IsMatch(
            text,
            "^(?:密码|验证码|支付密码|银行卡号|账号密码)$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static string CleanInputTarget(string value)
    {
        var target = value.Trim();
        if (target.Length == 0) return "当前输入框";
        target = Regex.Replace(target, "^(?:当前|这个|该)", string.Empty, RegexOptions.CultureInvariant);
        if (target is "微信" or "QQ" or "企业微信" or "钉钉" or "飞书") return "消息输入框";
        return target.Trim();
    }

    private static string? CleanInputApplication(string value)
    {
        var target = value.Trim();
        target = Regex.Replace(target, "^(?:当前|这个|该)", string.Empty, RegexOptions.CultureInvariant);
        return target is "微信" or "QQ" or "企业微信" or "钉钉" or "飞书"
            ? target
            : null;
    }

    private static string CleanInputText(string value) =>
        value.Trim(' ', '：', ':', '“', '”', '"', '‘', '’');

    private static string HashText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static bool LooksLikeStructuredControl(string target, string label, string verb) =>
        MenuLabels.Contains(label) ||
        target.Contains("菜单", StringComparison.Ordinal) ||
        target.EndsWith("按钮", StringComparison.Ordinal) ||
        target.EndsWith("标签页", StringComparison.Ordinal) ||
        target.EndsWith("列表项", StringComparison.Ordinal) ||
        target.EndsWith("选项", StringComparison.Ordinal) ||
        target.EndsWith("链接", StringComparison.Ordinal) ||
        label is "确定" or "取消" or "是" or "否" or "下一步" or "上一步" or "完成" or "关闭" ||
        (verb is "点击" or "单击" or "点开" or "点一下" or "按一下" or "按下" or "选择" &&
         LooksLikeShortNamedControl(label));

    private static bool LooksLikeShortNamedControl(string label) =>
        label.Length is >= 1 and <= 24 &&
        !UnstructuredTargetTerms.Any(term => label.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool IsUnsafeTarget(string target) => UnsafeTargets.Any(term =>
        target.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string CleanTarget(string value)
    {
        var target = value.Trim();
        target = Regex.Replace(
            target,
            "^(?:当前|这个|该)?(?:窗口|页面|界面)(?:里|中|内|上)?(?:的)?",
            string.Empty,
            RegexOptions.CultureInvariant);
        target = Regex.Replace(
            target,
            "^(?:文件|编辑|查看|格式|工具|窗口|帮助)菜单(?:(?:里|中|内)(?:的)?|的)",
            string.Empty,
            RegexOptions.CultureInvariant);
        target = Regex.Replace(target, "(?:一下|下)$", string.Empty, RegexOptions.CultureInvariant);
        return target.Trim();
    }

    private static string NormalizeRequest(string request)
    {
        var value = request.Normalize(NormalizationForm.FormKC).Trim();
        value = Regex.Replace(value, "[。！？!?，,；;]+$", string.Empty, RegexOptions.CultureInvariant);
        return value.Trim();
    }

    private static string NormalizeLabel(string target)
    {
        var value = target.Normalize(NormalizationForm.FormKC);
        value = Regex.Replace(
            value,
            "(?:下拉菜单|菜单栏|菜单项|菜单|按钮|标签页|列表项|选项|链接|功能)$",
            string.Empty,
            RegexOptions.CultureInvariant);
        return Regex.Replace(value, "[\\s\\p{P}\\p{S}]", string.Empty, RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(
        "^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\\s*)*" +
        "(?<verb>点击|单击|点开|打开|展开|点一下|按一下|按下|选择)\\s*(?<target>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitClickRegex();

    [GeneratedRegex(
        "^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\\s*)*(?:复制|拷贝)(?:一下|下)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DirectCopyRegex();

    [GeneratedRegex(
        "^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\\s*)*" +
        "(?:(?:全选|全部选中|全都选中)(?:一下|下)?(?:(?:当前)?(?:文档|记事本|Word|WPS|页面|窗口|栏目)(?:里|中|内|上)?(?:的)?(?:全部|所有)?(?:文字|文本|内容)?)?|" +
        "(?:把|将)?(?:当前)?(?:文档|记事本|Word|WPS|页面|窗口|栏目)(?:里|中|内|上)?(?:的)?(?:文字|文本|内容)(?:全部选择|全部选中|全都选中))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectSelectAllRegex();

    [GeneratedRegex(
        "^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\\s*)*" +
        "(?:(?:在|向)\\s*(?<target>(?:当前)?(?:微信|QQ|企业微信|钉钉|飞书|消息输入框|聊天输入框|输入框|编辑框|文本框|搜索框))(?:里|中|内|上)?\\s*)?" +
        "(?:输入|填写|键入|写入|写上)(?:文字|文本|内容|消息)?(?<separator>\\s*[：:])?\\s*(?<text>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitTextInputRegex();

    [GeneratedRegex(
        "^(?:(?:请|麻烦|帮我|帮忙|现在|然后|接着|继续|再)\\s*)*" +
        "(?:(?:点击|单击|按下|点一下)\\s*(?:发送|发出)(?:按钮|消息)?(?:一下)?|" +
        "(?:把|将)?(?:刚才|之前)?(?:输入的)?(?:文字|内容|消息|这条消息)?\\s*(?:发送|发出|发出去)(?:这条消息|消息)?(?:一下)?)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitSubmitRegex();
}
