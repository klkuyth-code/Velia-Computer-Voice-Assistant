using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class BrowserPageElementMatcherTests
{
    [Fact]
    public void MatchesAUniqueVisibleButtonWithoutModelReasoning()
    {
        var result = WrapPage("""
            [
              {"ref":"v111111111111","tag":"a","text":"首页","href":"https://weibo.com/"},
              {"ref":"v222222222222","tag":"button","text":"相册"}
            ]
            """);

        Assert.True(BrowserPageElementMatcher.TryFind(
            result,
            new BrowserElementActionCommand("相册", null),
            "范冰冰",
            out var match));
        Assert.Equal("v222222222222", match.Ref);
    }

    [Fact]
    public void UsesTheBoundEntityToFindAProfileLink()
    {
        var result = WrapPage("""
            [
              {"ref":"v111111111111","tag":"a","text":"范冰冰","href":"https://weibo.com/u/123"},
              {"ref":"v222222222222","tag":"a","text":"其他用户","href":"https://weibo.com/u/456"}
            ]
            """);

        Assert.True(BrowserPageElementMatcher.TryFind(
            result,
            new BrowserElementActionCommand("个人主页", null),
            "范冰冰",
            out var match));
        Assert.Equal("v111111111111", match.Ref);
    }

    [Fact]
    public void RefusesAnAmbiguousTargetInsteadOfGuessing()
    {
        var result = WrapPage("""
            [
              {"ref":"v111111111111","tag":"button","text":"查看作品"},
              {"ref":"v222222222222","tag":"button","text":"查看作品"}
            ]
            """);

        Assert.False(BrowserPageElementMatcher.TryFind(
            result,
            new BrowserElementActionCommand("查看作品", null),
            null,
            out _));
    }

    [Fact]
    public void SelectsTheRequestedOrderedSearchResultInsteadOfNavigationLinks()
    {
        var result = WrapPage("""
            [
              {"ref":"v000000000000","tag":"a","text":"图片","href":"https://google.com/images"},
              {"ref":"v999999999999","tag":"a","text":"推广内容","href":"https://ads.example/"},
              {"ref":"v111111111111","tag":"a","kind":"search-result","text":"第一条","href":"https://first.example/"},
              {"ref":"v222222222222","tag":"a","kind":"search-result","text":"第二条","href":"https://second.example/"},
              {"ref":"v333333333333","tag":"a","kind":"search-result","text":"第三条","href":"https://third.example/"}
            ]
            """);

        Assert.True(BrowserPageElementMatcher.TryFind(
            result,
            new BrowserElementActionCommand("第三个搜索结果", 3),
            null,
            out var match));
        Assert.Equal("v333333333333", match.Ref);
    }

    [Theory]
    [InlineData("第3页")]
    [InlineData("第三个页面")]
    [InlineData("第三个页")]
    public void SelectsNumberedPaginationControlsAcrossSites(string target)
    {
        var result = WrapPage("""
            [
              {"ref":"v111111111111","tag":"a","text":"1","href":"https://example.com/search?page=1"},
              {"ref":"v222222222222","tag":"a","text":"2","href":"https://example.com/search?page=2"},
              {"ref":"v333333333333","tag":"a","text":"3","href":"https://example.com/search?page=3"},
              {"ref":"v444444444444","tag":"button","text":"下一页"}
            ]
            """);

        Assert.True(BrowserPageElementMatcher.TryFind(
            result,
            new BrowserElementActionCommand(target, 3),
            null,
            out var match));
        Assert.Equal("v333333333333", match.Ref);
    }

    [Fact]
    public void SelectsPaginationWhenAccessibleNameRepeatsTheVisibleNumber()
    {
        var result = WrapPage("""
            [
              {"ref":"v222222222222","tag":"a","text":"Page 2 2","ariaLabel":"Page 2","href":"https://example.com/search?start=10"},
              {"ref":"v333333333333","tag":"a","text":"Page 3 3","ariaLabel":"Page 3","href":"https://example.com/search?start=20"}
            ]
            """);

        Assert.True(BrowserPageElementMatcher.TryFind(
            result,
            new BrowserElementActionCommand("第3页", 3),
            null,
            out var match));
        Assert.Equal("v333333333333", match.Ref);
        Assert.Contains("start=20", match.Href, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("下一页", "v222222222222")]
    [InlineData("上一页", "v111111111111")]
    public void SelectsStandardNextAndPreviousPaginationLinks(string target, string expectedRef)
    {
        var result = WrapPage("""
            [
              {"ref":"v111111111111","tag":"a","text":"Previous","ariaLabel":"Previous","rel":"prev","href":"https://example.com/search?page=2"},
              {"ref":"v222222222222","tag":"a","text":"Next","ariaLabel":"Next","rel":"next","href":"https://example.com/search?page=4"}
            ]
            """);

        Assert.True(BrowserPageElementMatcher.TryFind(
            result,
            new BrowserElementActionCommand(target, null),
            null,
            out var match));
        Assert.Equal(expectedRef, match.Ref);
    }

    private static string WrapPage(string elements) =>
        "{\"ok\":true,\"result\":{\"ok\":true,\"page\":{\"url\":\"https://weibo.com/search\"," +
        "\"title\":\"微博\",\"visibleText\":\"\",\"elements\":" + elements + "}}}";
}
