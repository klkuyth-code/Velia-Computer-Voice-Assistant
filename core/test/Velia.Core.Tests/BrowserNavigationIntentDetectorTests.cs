using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class BrowserNavigationIntentDetectorTests
{
    [Theory]
    [InlineData("用谷歌浏览器打开百度首页", "百度首页", "https://www.baidu.com/", "baidu.com")]
    [InlineData("用谷歌浏览器打开百度", "百度首页", "https://www.baidu.com/", "baidu.com")]
    [InlineData("打开必应", "必应首页", "https://www.bing.com/", "bing.com")]
    [InlineData("打开 Google", "谷歌首页", "https://www.google.com/", "google.com")]
    [InlineData("打开必应首页", "必应首页", "https://www.bing.com/", "bing.com")]
    [InlineData("请访问 Google 首页", "谷歌首页", "https://www.google.com/", "google.com")]
    public void ParsesKnownHomepageTargetsWithoutConfusingTheBrowserName(
        string request,
        string siteName,
        string url,
        string expectedHost)
    {
        Assert.True(BrowserNavigationIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(siteName, command.TargetName);
        Assert.Equal(url, command.Url);
        Assert.Equal(expectedHost, command.ExpectedHost);
    }

    [Theory]
    [InlineData("在当前百度页面搜索罗永浩", "百度搜索结果", "https://www.baidu.com/s?wd=%E7%BD%97%E6%B0%B8%E6%B5%A9", "/s", "已在百度搜索“罗永浩”。")]
    [InlineData("打开抖音，搜索罗永浩", "抖音搜索结果", "https://www.douyin.com/search/%E7%BD%97%E6%B0%B8%E6%B5%A9", "/search/", "已在抖音搜索“罗永浩”。")]
    [InlineData("用谷歌浏览器打开B站搜索雷军", "哔哩哔哩搜索结果", "https://search.bilibili.com/all?keyword=%E9%9B%B7%E5%86%9B", "/all", "已在哔哩哔哩搜索“雷军”。")]
    [InlineData("用谷歌搜索韶关天气", "谷歌搜索结果", "https://www.google.com/search?q=%E9%9F%B6%E5%85%B3%E5%A4%A9%E6%B0%94", "/search", "已在谷歌搜索“韶关天气”。")]
    [InlineData("用谷歌搜索抖音", "谷歌搜索结果", "https://www.google.com/search?q=%E6%8A%96%E9%9F%B3", "/search", "已在谷歌搜索“抖音”。")]
    [InlineData("在抖音搜索微博", "抖音搜索结果", "https://www.douyin.com/search/%E5%BE%AE%E5%8D%9A", "/search/", "已在抖音搜索“微博”。")]
    [InlineData("在知乎搜索人工智能", "知乎搜索结果", "https://www.zhihu.com/search?type=content&q=%E4%BA%BA%E5%B7%A5%E6%99%BA%E8%83%BD", "/search", "已在知乎搜索“人工智能”。")]
    public void ParsesKnownSiteSearchesIntoVerifiedNavigation(
        string request,
        string targetName,
        string url,
        string expectedPathPrefix,
        string successMessage)
    {
        Assert.True(BrowserNavigationIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(targetName, command.TargetName);
        Assert.Equal(url, command.Url);
        Assert.Equal(expectedPathPrefix, command.ExpectedPathPrefix);
        Assert.Equal(successMessage, command.SuccessMessage);
    }

    [Theory]
    [InlineData("进入我的抖音个人主页", "我的抖音个人主页", "https://www.douyin.com/user/self?from_tab_name=main", "/user/self")]
    [InlineData("进入我的抖音个人中心", "我的抖音个人主页", "https://www.douyin.com/user/self?from_tab_name=main", "/user/self")]
    [InlineData("进入抖音创作者中心", "抖音创作者中心", "https://creator.douyin.com/creator-micro/home", "/creator-micro")]
    [InlineData("打开小红书创作中心", "小红书创作服务平台", "https://creator.xiaohongshu.com/", "")]
    [InlineData("进入B站创作中心", "哔哩哔哩创作中心", "https://member.bilibili.com/platform/home", "/platform")]
    public void ParsesCommonCreatorAndProfileDestinationsWithoutModelPlanning(
        string request,
        string targetName,
        string url,
        string expectedPathPrefix)
    {
        Assert.True(BrowserNavigationIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(targetName, command.TargetName);
        Assert.Equal(url, command.Url);
        Assert.Equal(expectedPathPrefix, command.ExpectedPathPrefix);
    }

    [Theory]
    [InlineData("打开谷歌浏览器")]
    [InlineData("不要打开百度首页")]
    [InlineData("怎么打开百度首页")]
    [InlineData("百度首页很好用")]
    [InlineData("点击第三个搜索结果")]
    [InlineData("打开第3条搜索结果")]
    public void RejectsApplicationOnlyDeniedInstructionalOrNonActionRequests(string request)
    {
        Assert.False(BrowserNavigationIntentDetector.TryParseExplicitCommand(request, out _));
    }

    [Theory]
    [InlineData("进入微博里范冰冰的个人主页")]
    [InlineData("打开抖音里罗永浩的个人主页")]
    [InlineData("进入B站某个UP主的个人主页")]
    [InlineData("打开微博相册")]
    public void NeverDowngradesASpecificDestinationToThePlatformHomepage(string request)
    {
        Assert.False(BrowserNavigationIntentDetector.TryParseExplicitCommand(request, out _));
    }

    [Fact]
    public void BrowserNavigationUsesTheVerifiedLocalFastLane()
    {
        Assert.True(DeterministicLocalTaskIntentDetector.TryGetCanonicalKey(
            "用谷歌浏览器打开百度首页",
            out var key));
        Assert.StartsWith("browser:baidu.com", key, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("搜索罗永浩", "抖音", "https://www.douyin.com/search/%E7%BD%97%E6%B0%B8%E6%B5%A9")]
    [InlineData("搜一下范冰冰", "微博", "https://s.weibo.com/weibo?q=%E8%8C%83%E5%86%B0%E5%86%B0")]
    [InlineData("搜索人工智能", "知乎", "https://www.zhihu.com/search?type=content&q=%E4%BA%BA%E5%B7%A5%E6%99%BA%E8%83%BD")]
    public void UsesTheBoundWebPlatformForNaturalSearchFollowUps(
        string request,
        string contextualPlatform,
        string expectedUrl)
    {
        Assert.True(BrowserNavigationIntentDetector.TryParseExplicitCommand(
            request,
            contextualPlatform,
            out var command));
        Assert.Equal(expectedUrl, command.Url);
    }
}
