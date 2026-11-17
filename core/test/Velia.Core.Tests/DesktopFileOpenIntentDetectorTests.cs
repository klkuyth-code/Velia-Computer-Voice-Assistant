using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class DesktopFileOpenIntentDetectorTests
{
    [Theory]
    [InlineData("帮我打开桌面的 GPT导演决策", "GPT导演决策")]
    [InlineData("打开桌面上的GPT导演决策.txt。", "GPT导演决策.txt")]
    [InlineData("请启动桌面里的测试文档一下", "测试文档")]
    [InlineData("打开桌面上的八月表格验收文档。", "八月表格验收文档")]
    [InlineData("打开桌面的普通模式，Word 验收文档。", "普通模式,Word 验收文档")]
    [InlineData("打开桌面的 Excel 统一边界验收表格。", "Excel 统一边界验收表格")]
    public void ParsesExplicitDesktopFileOpenCommands(string request, string expected)
    {
        Assert.True(DesktopFileOpenIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(expected, command.RequestedName);
    }

    [Theory]
    [InlineData("重新打开刚才的文档", "刚才的文档")]
    [InlineData("再打开之前那个 Word 文档", "之前那个 Word 文档")]
    [InlineData("打开最近的 Excel 表格", "最近的 Excel 表格")]
    public void ParsesRecentDocumentReferencesWithoutRequiringTheDesktopWord(string request, string expected)
    {
        Assert.True(DesktopFileOpenIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(expected, command.RequestedName);
    }

    [Theory]
    [InlineData("不要打开桌面的 GPT导演决策")]
    [InlineData("怎么打开桌面的 GPT导演决策")]
    [InlineData("打开 C:\\Temp\\unknown.exe")]
    [InlineData("看看桌面的 GPT导演决策")]
    public void RejectsDeniedInstructionalPathAndNonActionRequests(string request)
    {
        Assert.False(DesktopFileOpenIntentDetector.TryParseExplicitCommand(request, out _));
    }

    [Theory]
    [InlineData("打开桌面的普通模式 Word 验收文档", WindowsApplicationIds.Word)]
    [InlineData("打开桌面的 Excel 统一边界验收表格", WindowsApplicationIds.Excel)]
    [InlineData("打开桌面的 WPS 工作记录", WindowsApplicationIds.WpsWriter)]
    public void NamedDesktopFilesAreNeverDowngradedToApplicationLaunches(
        string request,
        string applicationId)
    {
        Assert.False(WindowsControlIntentDetector.IsExplicitCommand(
            request,
            new WindowsControlCommand(WindowsControlActions.OpenApplication, ApplicationId: applicationId)));
        Assert.False(WindowsControlIntentDetector.TryParseExplicitCommand(request, out _));
        Assert.True(DeterministicLocalTaskIntentDetector.TryParse(request, null, out var intent));
        Assert.Equal(DeterministicLocalTaskKind.DesktopFileOpen, intent.Kind);
        Assert.NotNull(intent.DesktopFileOpen);
    }
}
