using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class BrowserTabCommandIntentDetectorTests
{
    [Theory]
    [InlineData("关闭标签页")]
    [InlineData("把这个页面关掉")]
    [InlineData("关闭谷歌浏览器当前标签页")]
    public void ParsesNaturalCurrentTabCloseCommands(string request)
    {
        Assert.True(BrowserTabCommandIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(BrowserTabCommandActions.CloseTab, command.Action);
        Assert.Null(command.TargetTitle);
    }

    [Theory]
    [InlineData("返回上一页")]
    [InlineData("请后退到上一页")]
    [InlineData("浏览器返回上一页")]
    public void ParsesBrowserBackCommands(string request)
    {
        Assert.True(BrowserTabCommandIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(BrowserTabCommandActions.GoBack, command.Action);
        Assert.Null(command.TargetTitle);
    }

    [Theory]
    [InlineData("滑动到页面底部", "bottom", null)]
    [InlineData("滑动页面到底部", "bottom", null)]
    [InlineData("把当前页面滑到最底部", "bottom", null)]
    [InlineData("滑到页面最底部", "bottom", null)]
    [InlineData("滚到网页最底部", "bottom", null)]
    [InlineData("一直滑到底部", "bottom", null)]
    [InlineData("返回页面顶部", "top", null)]
    [InlineData("将网页滚动到最顶部", "top", null)]
    [InlineData("向下滚动", null, 900)]
    [InlineData("往上滑", null, -900)]
    [InlineData("向下滚动一点", null, 520)]
    [InlineData("网页往上滑半页", null, -900)]
    public void ParsesGenericBrowserScrollCommands(string request, string? position, int? amount)
    {
        Assert.True(BrowserTabCommandIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(BrowserTabCommandActions.Scroll, command.Action);
        Assert.Equal(position, command.ScrollPosition);
        Assert.Equal(amount, command.ScrollAmount);
    }

    [Fact]
    public void GenericScrollRequiresBrowserScopeOrAnActiveWebPlatform()
    {
        Assert.True(BrowserTabCommandIntentDetector.TryParseExplicitCommand("然后向下滚动", out var generic));
        Assert.False(BrowserTabCommandIntentDetector.IsContextuallyAllowed("然后向下滚动", generic, null));
        Assert.True(BrowserTabCommandIntentDetector.IsContextuallyAllowed("然后向下滚动", generic, "douyin"));

        Assert.True(BrowserTabCommandIntentDetector.TryParseExplicitCommand("页面向下滚动", out var explicitPage));
        Assert.True(BrowserTabCommandIntentDetector.IsContextuallyAllowed("页面向下滚动", explicitPage, null));
    }

    [Fact]
    public void GenericScrollAcceptsAManuallyOpenedForegroundBrowser()
    {
        Assert.True(BrowserTabCommandIntentDetector.TryParseExplicitCommand("向下滚动", out var command));
        Assert.True(BrowserTabCommandIntentDetector.IsContextuallyAllowed(
            "向下滚动",
            command,
            null,
            foregroundBrowser: true));
    }

    [Theory]
    [InlineData("关闭当前标签页")]
    [InlineData("关掉这个网页")]
    [InlineData("把当前标签页关闭")]
    public void ParsesCurrentTabCloseCommands(string request)
    {
        Assert.True(BrowserTabCommandIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(BrowserTabCommandActions.CloseTab, command.Action);
        Assert.Null(command.TargetTitle);
    }

    [Theory]
    [InlineData("关闭人工智能这个标签页", "人工智能")]
    [InlineData("关闭谷歌浏览器里的抖音创作者中心标签页", "抖音创作者中心")]
    [InlineData("把AI爆款创作方法这个标签页关掉", "AI爆款创作方法")]
    public void ParsesNamedTabCloseCommands(string request, string expectedTitle)
    {
        Assert.True(BrowserTabCommandIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(BrowserTabCommandActions.CloseTab, command.Action);
        Assert.Equal(expectedTitle, command.TargetTitle);
    }

    [Theory]
    [InlineData("不要关闭当前标签页")]
    [InlineData("怎么返回上一页")]
    [InlineData("关闭所有标签页")]
    [InlineData("关闭谷歌浏览器")]
    [InlineData("不要滑到底部")]
    public void RejectsDeniedAmbiguousOrWholeBrowserCommands(string request) =>
        Assert.False(BrowserTabCommandIntentDetector.TryParseExplicitCommand(request, out _));
}
