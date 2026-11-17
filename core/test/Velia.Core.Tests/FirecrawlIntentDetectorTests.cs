using Velia.Core.Web;
using Xunit;

namespace Velia.Core.Tests;

public sealed class FirecrawlIntentDetectorTests
{
    [Fact]
    public void AllowsOnlyAGroundedExplicitSearch()
    {
        var allowed = FirecrawlIntentDetector.IsExplicitCommand(
            "请联网搜索 Velia Firecrawl 的最新资料",
            new FirecrawlCommand(FirecrawlActions.Search, "Velia Firecrawl 最新资料", 3));

        Assert.True(allowed);
    }

    [Theory]
    [InlineData("不要联网，搜索 Velia 的资料")]
    [InlineData("别搜索 Velia，我只想聊聊")]
    [InlineData("告诉我怎么搜索 Velia")]
    [InlineData("请介绍搜索教程")]
    public void RejectsDenialsAndInstructionalQuestions(string request)
    {
        var allowed = FirecrawlIntentDetector.IsExplicitCommand(
            request,
            new FirecrawlCommand(FirecrawlActions.Search, "Velia", 3));

        Assert.False(allowed);
    }

    [Fact]
    public void RejectsAModelThatSubstitutesAnUnrequestedSearchQuery()
    {
        var allowed = FirecrawlIntentDetector.IsExplicitCommand(
            "请联网搜索 Velia Firecrawl 的最新资料",
            new FirecrawlCommand(FirecrawlActions.Search, "Daisy API 密钥", 3));

        Assert.False(allowed);
    }

    [Fact]
    public void DirectScrapeRequiresTheExactUrlFromTheUserRequest()
    {
        var request = "请读取并总结 https://openai.com/research/ 这篇网页";

        Assert.True(FirecrawlIntentDetector.IsExplicitCommand(
            request,
            new FirecrawlCommand(FirecrawlActions.Scrape, Url: "https://openai.com/research/")));
        Assert.False(FirecrawlIntentDetector.IsExplicitCommand(
            request,
            new FirecrawlCommand(FirecrawlActions.Scrape, Url: "https://example.org/private")));
    }

    [Fact]
    public void SearchAndScrapeDenialsApplyOnlyToTheirOwnAction()
    {
        const string Url = "https://openai.com/research/";

        Assert.True(FirecrawlIntentDetector.IsExplicitCommand(
            $"不要搜索，只直接抓取并读取 {Url}",
            new FirecrawlCommand(FirecrawlActions.Scrape, Url: Url)));
        Assert.False(FirecrawlIntentDetector.IsExplicitCommand(
            $"不要抓取，只直接读取 {Url}",
            new FirecrawlCommand(FirecrawlActions.Scrape, Url: Url)));
        Assert.True(FirecrawlIntentDetector.IsExplicitCommand(
            "不要抓取，只联网搜索 Velia Firecrawl",
            new FirecrawlCommand(FirecrawlActions.Search, "Velia Firecrawl", 3)));
    }

    [Fact]
    public void ResultIdScrapeStillRequiresAnExplicitSearchRequest()
    {
        var resultId = Guid.NewGuid();

        Assert.True(FirecrawlIntentDetector.IsExplicitCommand(
            "继续搜索并读取第一个结果",
            new FirecrawlCommand(FirecrawlActions.Scrape, ResultId: resultId)));
        Assert.False(FirecrawlIntentDetector.IsExplicitCommand(
            "继续处理第一个结果",
            new FirecrawlCommand(FirecrawlActions.Scrape, ResultId: resultId)));
    }
}
