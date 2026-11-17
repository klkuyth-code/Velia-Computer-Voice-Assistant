namespace Velia.Core.Conversations;

/// <summary>
/// Identifies voice turns that are safe to expose while the provider is still
/// streaming.  The allow-list is deliberately expressed as the absence of any
/// external-state, current-context, or long-form cue: tool-capable turns stay
/// buffered until the provider proves that no tool call follows.
/// </summary>
internal static class VoiceConversationIntentDetector
{
    private static readonly string[] ExternalStateOrActionCues =
    [
        "打开", "启动", "运行", "关闭", "退出", "关掉", "最小化", "最大化", "恢复", "切换",
        "点击", "点开", "选择", "输入", "填写", "写入", "写到", "添加", "插入", "保存",
        "删除", "清空", "修改", "替换", "复制", "粘贴", "移动", "重命名", "发送", "下载",
        "上传", "搜索", "查找", "滚动", "滑动", "返回上一页", "标签页", "浏览器", "网页",
        "桌面", "窗口", "软件", "应用", "文件", "文件夹", "文档", "表格", "单元格",
        "记事本", "Word", "Wod", "Excel", "WPS", "微信", "QQ", "剪贴板", "选中文字",
        "选中的", "当前屏幕", "当前画面", "屏幕上", "画面里", "画面中", "读屏", "截图",
        "这个", "那个", "这里", "那里", "当前", "刚才", "刚刚", "之前", "上一个",
        "看一下", "看下", "看看", "帮我看", "这些", "那些",
        "音量", "静音", "计时器", "倒计时", "提醒我", "停止任务", "任务进度", "进度",
        "网址", "链接", "新闻", "天气", "股价", "价格", "汇率", "最新", "今天", "现在几点",
        "登录", "账号", "密码", "验证码", "支付", "购买", "发布",
    ];

    private static readonly string[] LongFormOrReasoningCues =
    [
        "深入分析", "详细分析", "全面分析", "写一份", "编写", "生成文档", "代码", "推理过程",
        "完整方案", "调研", "报告", "长文", "论文",
    ];

    public static bool CanStream(string request, string origin)
    {
        if (!string.Equals(origin, "voice", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(request))
            return false;

        var normalized = request.Trim();
        if (normalized.Length is < 1 or > 600) return false;
        return !ContainsAny(normalized, ExternalStateOrActionCues) &&
               !ContainsAny(normalized, LongFormOrReasoningCues);
    }

    private static bool ContainsAny(string value, IEnumerable<string> terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
