using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class BrowserElementActionIntentDetectorTests
{
    [Theory]
    [InlineData("点击相册", "相册", null)]
    [InlineData("然后进入她的个人主页", "个人主页", null)]
    [InlineData("点击当前页面上的第二张照片", "第二张照片", 2)]
    [InlineData("点击第三个搜索结果", "第三个搜索结果", 3)]
    [InlineData("打开第3条搜索结果", "第3条搜索结果", 3)]
    [InlineData("点击第3页", "第3页", 3)]
    [InlineData("点击第三个页面", "第三页", 3)]
    [InlineData("点击第三个页", "第三页", 3)]
    [InlineData("点击第三业", "第三页", 3)]
    [InlineData("请点开网页上的作品", "作品", null)]
    [InlineData("下一页", "下一页", null)]
    [InlineData("下一个页面", "下一页", null)]
    [InlineData("上一个页面", "上一页", null)]
    public void ParsesNaturalLowRiskPageActions(string request, string target, int? ordinal)
    {
        Assert.True(BrowserElementActionIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(target, command.Target);
        Assert.Equal(ordinal, command.Ordinal);
    }

    [Theory]
    [InlineData("点击第3页")]
    [InlineData("点击第三个页面")]
    [InlineData("点击第三叶")]
    [InlineData("下一页")]
    [InlineData("上一个页面")]
    public void TreatsNumberedPaginationAsExplicitBrowserScope(string request)
    {
        Assert.True(BrowserElementActionIntentDetector.HasExplicitBrowserScope(request));
    }

    [Fact]
    public void SharedLocalRouterUsesBoundBrowserContextForNaturalFollowUps()
    {
        Assert.True(DeterministicLocalTaskIntentDetector.TryParse(
            "进入她的个人主页",
            "抖音",
            out var intent));

        Assert.Equal(DeterministicLocalTaskKind.BrowserElementAction, intent.Kind);
        Assert.Equal("个人主页", intent.BrowserElementAction!.Target);
        Assert.Contains("browser-action:抖音", intent.CanonicalKey, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitApplicationCommandsOverrideStaleBrowserContext()
    {
        Assert.True(DeterministicLocalTaskIntentDetector.TryParse(
            "打开记事本",
            "抖音",
            out var intent));

        Assert.Equal(DeterministicLocalTaskKind.WindowsControl, intent.Kind);
        Assert.Equal("notepad", intent.WindowsControl!.ApplicationId);
    }

    [Theory]
    [InlineData("不要点击相册")]
    [InlineData("怎么进入她的个人主页")]
    [InlineData("点击发送")]
    [InlineData("删除当前作品")]
    [InlineData("打开网页并整理内容")]
    [InlineData("打开浏览器并搜索 Velia")]
    public void RejectsDeniedInstructionalAndHighRiskPageActions(string request)
    {
        Assert.False(BrowserElementActionIntentDetector.TryParseExplicitCommand(request, out _));
    }
}
