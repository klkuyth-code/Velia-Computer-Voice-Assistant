using Velia.Core.Conversations;
using Xunit;

namespace Velia.Core.Tests;

public sealed class VoiceConversationIntentDetectorTests
{
    [Theory]
    [InlineData("你好，最近怎么样")]
    [InlineData("给我讲个简短的笑话")]
    [InlineData("介绍一下广东韶关")]
    public void StreamsOnlySelfContainedOrdinaryVoiceConversation(string request)
    {
        Assert.True(VoiceConversationIntentDetector.CanStream(request, "voice"));
    }

    [Theory]
    [InlineData("帮我看看这个是什么意思")]
    [InlineData("解释一下当前选项")]
    [InlineData("刚才的文档保存了吗")]
    [InlineData("打开记事本")]
    public void KeepsContextualOrExternalStateRequestsBuffered(string request)
    {
        Assert.False(VoiceConversationIntentDetector.CanStream(request, "voice"));
    }
}
