using Velia.Core.Providers.Live;
using Xunit;

namespace Velia.Core.Tests;

public sealed class LiveTranscriptNormalizerTests
{
    [Theory]
    [InlineData("打 开 记 事 本。", "打开记事本。")]
    [InlineData("请 打 开 桌 面 的 文 档。", "请打开桌面的文档。")]
    [InlineData("打开 Chrome 搜索 Velia", "打开 Chrome 搜索 Velia")]
    [InlineData("广东 韶 关", "广东韶关")]
    public void RemovesOnlyArtificialSpacingInsideChineseText(string input, string expected)
    {
        Assert.Equal(expected, LiveTranscriptNormalizer.Normalize(input));
    }
}
