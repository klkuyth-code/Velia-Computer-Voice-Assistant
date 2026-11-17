using Velia.Core.WindowsControl;
using System.Text.Json;
using Xunit;

namespace Velia.Core.Tests;

public sealed class BrowserTargetEvidenceVerifierTests
{
    [Fact]
    public void CarriesTheSearchedEntityIntoAPronounProfileContinuation()
    {
        var expectation = BrowserTargetEvidenceVerifier.TryCreate(
            "在微博搜索范冰冰\n补充要求：进入她的个人主页");

        Assert.NotNull(expectation);
        Assert.Equal(BrowserTargetKind.EntityProfile, expectation.Kind);
        Assert.Equal("微博", expectation.Platform);
        Assert.Equal("范冰冰", expectation.Entity);
    }

    [Fact]
    public void RejectsASearchPageAsEvidenceOfEnteringTheProfile()
    {
        var expectation = new BrowserTargetExpectation(BrowserTargetKind.EntityProfile, "微博", "范冰冰");
        var result = ToolResult(
            "https://s.weibo.com/weibo?q=%E8%8C%83%E5%86%B0%E5%86%B0",
            "范冰冰 - 微博搜索",
            "范冰冰 关注 粉丝 作品 搜索结果");

        Assert.False(BrowserTargetEvidenceVerifier.IsVerified(result, expectation));
    }

    [Fact]
    public void AcceptsTheRequestedEntityOnARealProfilePage()
    {
        var expectation = new BrowserTargetExpectation(BrowserTargetKind.EntityProfile, "微博", "范冰冰");
        var result = ToolResult(
            "https://weibo.com/u/123456",
            "范冰冰的微博",
            "范冰冰 关注 粉丝 作品 简介");

        Assert.True(BrowserTargetEvidenceVerifier.IsVerified(result, expectation));
    }

    [Fact]
    public void RequiresTheRequestedPlatformRatherThanAThirdPartySearchPage()
    {
        var expectation = new BrowserTargetExpectation(BrowserTargetKind.SearchResults, "微博", "范冰冰");
        var result = ToolResult(
            "https://www.google.com/search?q=%E5%BE%AE%E5%8D%9A%E8%8C%83%E5%86%B0%E5%86%B0",
            "微博范冰冰 - Google 搜索",
            "微博 范冰冰");

        Assert.False(BrowserTargetEvidenceVerifier.IsVerified(result, expectation));
    }

    private static string ToolResult(string url, string title, string visibleText) => JsonSerializer.Serialize(new
    {
        ok = true,
        result = new
        {
            ok = true,
            finalPage = new { page = new { url, title, visibleText } },
        },
    });
}
