using Velia.Core.Documents;
using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class BrowserArticleToWordWorkflowTests
{
    [Theory]
    [InlineData("把当前网页正文写入刚才的 Word 文档")]
    [InlineData("把这个页面的文章整理到当前文档里")]
    [InlineData("提取当前网页正文并保存到刚才的文档")]
    [InlineData("把这篇文章复制到刚才的沃德文档")]
    public void RecognizesOnlyExplicitArticleToDocumentCommands(string request)
    {
        Assert.True(DeterministicLocalTaskIntentDetector.TryParse(request, null, out var intent));
        Assert.Equal(DeterministicLocalTaskKind.BrowserArticleToWord, intent.Kind);
        Assert.True(intent.IsBrowser);
        Assert.NotNull(intent.BrowserArticleToWord);
    }

    [Theory]
    [InlineData("不要把当前网页正文写入 Word 文档")]
    [InlineData("如何把当前网页正文写入 Word 文档")]
    [InlineData("总结当前网页正文")]
    [InlineData("在当前 Word 文档末尾写入一段文字")]
    public void RejectsDeniedInstructionalOrIncompleteRequests(string request)
    {
        Assert.False(BrowserArticleToWordIntentDetector.TryParseExplicitCommand(request, out _));
    }

    [Fact]
    public void CleansWrappedBrowserArticleWithoutTreatingNoiseAsContent()
    {
        const string result = """
            {
              "ok": true,
              "result": {
                "ok": true,
                "article": {
                  "title": "秋季养生指南",
                  "url": "https://example.com/health/autumn",
                  "text": "作者：测试作者\n\n秋季天气逐渐转凉，日常作息应当规律，并根据气温变化及时增减衣物，避免早晚温差造成不适。\n\n广告\n\n饮食宜清淡多样，保证蔬菜、水果、蛋白质和充足饮水，同时保持适量运动和充足睡眠。\n\n公众号：测试健康",
                  "paragraphCount": 5,
                  "source": "article"
                }
              }
            }
            """;

        Assert.True(BrowserArticleContentParser.TryParseAndClean(result, out var content, out var failure));

        Assert.Empty(failure);
        Assert.Equal("秋季养生指南", content.Title);
        Assert.Equal("https://example.com/health/autumn", content.Source.AbsoluteUri);
        Assert.Equal(2, content.ParagraphCount);
        Assert.StartsWith("秋季养生指南\n\n秋季天气", content.FormattedText, StringComparison.Ordinal);
        Assert.DoesNotContain("作者", content.FormattedText, StringComparison.Ordinal);
        Assert.DoesNotContain("广告", content.FormattedText, StringComparison.Ordinal);
        Assert.DoesNotContain("公众号", content.FormattedText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"ok\":false,\"error\":{\"code\":\"browser_bridge_disconnected\"}}")]
    [InlineData("{\"ok\":true,\"article\":{\"title\":\"短文\",\"url\":\"https://example.com\",\"text\":\"只有一行\",\"paragraphCount\":1}}")]
    [InlineData("{\"ok\":true,\"article\":{\"title\":\"文章\",\"url\":\"file:///C:/secret.txt\",\"text\":\"第一段内容足够长足够长足够长足够长足够长\n第二段内容足够长足够长足够长足够长足够长\",\"paragraphCount\":2}}")]
    [InlineData("not-json")]
    public void RejectsToolFailuresShortBodiesUnsafeUrlsAndMalformedJson(string result)
    {
        Assert.False(BrowserArticleContentParser.TryParseAndClean(result, out _, out var failure));
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    [Fact]
    public void RejectsAHomepageMadeOfRepeatedHeadlineSummaryCards()
    {
        const string result = """
            {
              "ok": true,
              "article": {
                "title": "新闻首页",
                "url": "https://example.com/",
                "source": "main",
                "text": "标题一摘要一足够长用于识别列表结构\n标题一\n摘要一足够长用于识别列表结构\n标题二摘要二足够长用于识别列表结构\n标题二\n摘要二足够长用于识别列表结构\n标题三摘要三足够长用于识别列表结构\n标题三\n摘要三足够长用于识别列表结构",
                "paragraphCount": 9
              }
            }
            """;

        Assert.False(BrowserArticleContentParser.TryParseAndClean(result, out _, out var failure));
        Assert.Contains("首页或内容列表", failure, StringComparison.Ordinal);
    }
}
