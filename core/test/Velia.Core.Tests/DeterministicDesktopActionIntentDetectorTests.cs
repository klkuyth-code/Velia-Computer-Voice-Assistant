using Velia.Core.DesktopVisual;
using Xunit;

namespace Velia.Core.Tests;

public sealed class DeterministicDesktopActionIntentDetectorTests
{
    [Theory]
    [InlineData("点击当前窗口的编辑菜单", DeterministicDesktopActionTypes.ClickControl, "编辑菜单")]
    [InlineData("然后点击文件", DeterministicDesktopActionTypes.ClickControl, "文件")]
    [InlineData("请按一下确定按钮", DeterministicDesktopActionTypes.ClickControl, "确定按钮")]
    [InlineData("接着点击编辑菜单中的复制", DeterministicDesktopActionTypes.CopySelection, "复制")]
    [InlineData("点击复制", DeterministicDesktopActionTypes.CopySelection, "复制")]
    [InlineData("复制一下", DeterministicDesktopActionTypes.CopySelection, "复制")]
    [InlineData("选择全选", DeterministicDesktopActionTypes.SelectAll, "全选")]
    [InlineData("全选一下", DeterministicDesktopActionTypes.SelectAll, "全选")]
    [InlineData("全选当前栏目的文字", DeterministicDesktopActionTypes.SelectAll, "全选")]
    [InlineData("当前文档的文字全部选择", DeterministicDesktopActionTypes.SelectAll, "全选")]
    [InlineData("把当前记事本里的内容全部选中", DeterministicDesktopActionTypes.SelectAll, "全选")]
    [InlineData("打开文件菜单", DeterministicDesktopActionTypes.ClickControl, "文件菜单")]
    [InlineData("点击张三", DeterministicDesktopActionTypes.ClickControl, "张三")]
    [InlineData("选择通讯录", DeterministicDesktopActionTypes.ClickControl, "通讯录")]
    public void ParsesExplicitLowRiskStructuredActions(string request, string action, string target)
    {
        Assert.True(DeterministicDesktopActionIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(action, command.Action);
        Assert.Equal(target, command.Target);
    }

    [Theory]
    [InlineData("不要点击编辑菜单")]
    [InlineData("怎么点击编辑菜单")]
    [InlineData("点击删除按钮")]
    [InlineData("点击立即支付按钮")]
    [InlineData("编辑菜单很好用")]
    [InlineData("点击一段普通文字")]
    public void RejectsDeniedRiskyInstructionalOrUnstructuredTargets(string request)
    {
        Assert.False(DeterministicDesktopActionIntentDetector.TryParseExplicitCommand(request, out _));
    }

    [Fact]
    public void ProducesAStableKeyForEquivalentCopySpeech()
    {
        Assert.True(DeterministicDesktopActionIntentDetector.TryParseExplicitCommand("点击复制", out var first));
        Assert.True(DeterministicDesktopActionIntentDetector.TryParseExplicitCommand("然后点击编辑菜单中的拷贝", out var second));

        Assert.Equal(
            DeterministicDesktopActionIntentDetector.GetCanonicalKey(first),
            DeterministicDesktopActionIntentDetector.GetCanonicalKey(second));
    }

    [Theory]
    [InlineData("输入：你好，明天下午三点见", "当前输入框", "你好，明天下午三点见", null)]
    [InlineData("在微信输入：测试消息", "消息输入框", "测试消息", "微信")]
    [InlineData("在QQ中输入文字：稍后回复你", "消息输入框", "稍后回复你", "QQ")]
    [InlineData("在搜索框输入：张三", "搜索框", "张三", null)]
    [InlineData("接着填写消息：收到", "当前输入框", "收到", null)]
    public void ParsesExplicitTextEntryWithoutSubmitting(
        string request,
        string target,
        string text,
        string? application)
    {
        Assert.True(DeterministicDesktopActionIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(DeterministicDesktopActionTypes.TypeText, command.Action);
        Assert.Equal(target, command.Target);
        Assert.Equal(text, command.Text);
        Assert.Equal(application, command.Application);
    }

    [Theory]
    [InlineData("发送")]
    [InlineData("点击发送按钮")]
    [InlineData("发送这条消息")]
    [InlineData("把刚才输入的内容发出去")]
    public void ParsesOnlyAnExplicitStandaloneSubmitGesture(string request)
    {
        Assert.True(DeterministicDesktopActionIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(DeterministicDesktopActionTypes.SubmitText, command.Action);
        Assert.Null(command.Text);
    }

    [Theory]
    [InlineData("输入密码")]
    [InlineData("在密码输入框输入：123456")]
    [InlineData("不要输入：这条消息")]
    [InlineData("怎么输入消息")]
    [InlineData("给张三发送你好")]
    public void RejectsSensitiveDeniedInstructionalAndCombinedSendRequests(string request)
    {
        Assert.False(DeterministicDesktopActionIntentDetector.TryParseExplicitCommand(request, out _));
    }

    [Fact]
    public void CanonicalTextEntryKeyDoesNotContainTheMessageBody()
    {
        Assert.True(DeterministicDesktopActionIntentDetector.TryParseExplicitCommand(
            "输入：这是私密消息",
            out var command));

        var key = DeterministicDesktopActionIntentDetector.GetCanonicalKey(command);

        Assert.DoesNotContain("这是私密消息", key, StringComparison.Ordinal);
        Assert.StartsWith("desktop:typeText:", key, StringComparison.Ordinal);
    }
}
