using Velia.Core.Providers.Asr;
using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class ContextualAsrPostProcessorTests
{
    [Theory]
    [InlineData("打开剪影专业版。", "打开剪映专业版。")]
    [InlineData("请把剪影专业版最小化一下", "请把剪映专业版最小化一下")]
    [InlineData("切换到剪影专业版窗口", "切换到剪映专业版窗口")]
    [InlineData("打开桌面上的剪影专业版。", "打开桌面上的剪映专业版。")]
    public void CorrectsOnlyTheApplicationEntityAgainstLocalState(string transcript, string expected)
    {
        var processor = new ContextualAsrPostProcessor(CreateCatalog(
            Entry("installed:capcut", "剪映专业版", "JianyingPro")));

        Assert.Equal(expected, processor.Process(transcript));
    }

    [Theory]
    [InlineData("我觉得剪影专业版挺好用")]
    [InlineData("在文档里写入：打开剪影专业版。")]
    [InlineData("打开桌面的剪影专业版介绍.docx")]
    public void NeverRewritesConversationOrDocumentPayloads(string transcript)
    {
        var processor = new ContextualAsrPostProcessor(CreateCatalog(
            Entry("installed:capcut", "剪映专业版", "JianyingPro")));

        Assert.Equal(transcript, processor.Process(transcript));
    }

    [Fact]
    public void LeavesAmbiguousApplicationNamesUnchanged()
    {
        var processor = new ContextualAsrPostProcessor(CreateCatalog(
            Entry("installed:studio-a", "创作工作室", "StudioA"),
            Entry("installed:studio-b", "创作工作台", "StudioB")));

        Assert.Equal("打开创作工作", processor.Process("打开创作工作"));
    }

    private static WindowsInstalledApplicationCatalog CreateCatalog(params InstalledApplicationEntry[] entries) =>
        new(entryProvider: () => entries, validateSuppliedEntries: false);

    private static InstalledApplicationEntry Entry(string id, string displayName, string processName) =>
        new(
            id,
            displayName,
            $@"C:\Program Files\Velia Tests\{processName}.exe",
            new HashSet<string>([processName], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([displayName, processName], StringComparer.OrdinalIgnoreCase));
}
