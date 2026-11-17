using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class WindowsControlIntentDetectorTests
{
    [Theory]
    [InlineData("打开记事本", WindowsControlActions.OpenApplication, WindowsApplicationIds.Notepad)]
    [InlineData("请启动微信", WindowsControlActions.OpenApplication, WindowsApplicationIds.WeChat)]
    [InlineData("关闭 Google Chrome", WindowsControlActions.CloseApplication, WindowsApplicationIds.Chrome)]
    [InlineData("退出微软 Excel", WindowsControlActions.CloseApplication, WindowsApplicationIds.MicrosoftExcel)]
    [InlineData("打开 Word", WindowsControlActions.OpenApplication, WindowsApplicationIds.Word)]
    [InlineData("打开 Wod", WindowsControlActions.OpenApplication, WindowsApplicationIds.Word)]
    [InlineData("打开微软 Word", WindowsControlActions.OpenApplication, WindowsApplicationIds.MicrosoftWord)]
    [InlineData("打开 Excel", WindowsControlActions.OpenApplication, WindowsApplicationIds.Excel)]
    [InlineData("打开 Ex 表格", WindowsControlActions.OpenApplication, WindowsApplicationIds.Excel)]
    [InlineData("打开微软 Excel", WindowsControlActions.OpenApplication, WindowsApplicationIds.MicrosoftExcel)]
    [InlineData("最小化谷歌浏览器", WindowsControlActions.MinimizeApplication, WindowsApplicationIds.Chrome)]
    [InlineData("把微信调出来", WindowsControlActions.RestoreApplication, WindowsApplicationIds.WeChat)]
    [InlineData("最大化记事本", WindowsControlActions.MaximizeApplication, WindowsApplicationIds.Notepad)]
    [InlineData("打开企业微信", WindowsControlActions.OpenApplication, WindowsApplicationIds.WeCom)]
    [InlineData("启动钉钉", WindowsControlActions.OpenApplication, WindowsApplicationIds.DingTalk)]
    [InlineData("打开WPS", WindowsControlActions.OpenApplication, WindowsApplicationIds.WpsWriter)]
    [InlineData("打开WPS表格", WindowsControlActions.OpenApplication, WindowsApplicationIds.WpsSpreadsheets)]
    public void AcceptsMatchingAllowlistedApplicationRequests(string request, string action, string applicationId)
    {
        Assert.True(WindowsControlIntentDetector.IsExplicitCommand(request, new WindowsControlCommand(action, ApplicationId: applicationId)));
    }

    [Theory]
    [InlineData("打开记事本", WindowsControlActions.OpenApplication, WindowsApplicationIds.Calculator)]
    [InlineData("不要打开记事本", WindowsControlActions.OpenApplication, WindowsApplicationIds.Notepad)]
    [InlineData("怎么打开记事本", WindowsControlActions.OpenApplication, WindowsApplicationIds.Notepad)]
    [InlineData("记事本很好用", WindowsControlActions.OpenApplication, WindowsApplicationIds.Notepad)]
    [InlineData("用谷歌浏览器打开百度首页", WindowsControlActions.OpenApplication, WindowsApplicationIds.Chrome)]
    [InlineData("用 Chrome 搜索罗永浩", WindowsControlActions.OpenApplication, WindowsApplicationIds.Chrome)]
    [InlineData("打开微软 Word", WindowsControlActions.OpenApplication, WindowsApplicationIds.Word)]
    [InlineData("打开 Word", WindowsControlActions.OpenApplication, WindowsApplicationIds.MicrosoftWord)]
    [InlineData("打开微软 Excel", WindowsControlActions.OpenApplication, WindowsApplicationIds.Excel)]
    [InlineData("打开 Excel", WindowsControlActions.OpenApplication, WindowsApplicationIds.MicrosoftExcel)]
    public void RejectsChangedDeniedOrInstructionalApplicationRequests(string request, string action, string applicationId)
    {
        Assert.False(WindowsControlIntentDetector.IsExplicitCommand(request, new WindowsControlCommand(action, ApplicationId: applicationId)));
    }

    [Theory]
    [InlineData("把音量调到 35", WindowsControlActions.SetVolume, 35)]
    [InlineData("把声音调高 10", WindowsControlActions.AdjustVolume, 10)]
    [InlineData("把音量调低 20", WindowsControlActions.AdjustVolume, -20)]
    [InlineData("声音大一点", WindowsControlActions.AdjustVolume, 10)]
    [InlineData("声音小一点", WindowsControlActions.AdjustVolume, -10)]
    public void AcceptsExactVolumeValues(string request, string action, int value)
    {
        Assert.True(WindowsControlIntentDetector.IsExplicitCommand(request, new WindowsControlCommand(action, Value: value)));
    }

    [Fact]
    public void RejectsInventedVolumeValuesAndDeniedMute()
    {
        Assert.False(WindowsControlIntentDetector.IsExplicitCommand("把音量调到 35", new WindowsControlCommand(WindowsControlActions.SetVolume, Value: 80)));
        Assert.False(WindowsControlIntentDetector.IsExplicitCommand("不要静音", new WindowsControlCommand(WindowsControlActions.Mute)));
        Assert.True(WindowsControlIntentDetector.IsExplicitCommand("取消静音", new WindowsControlCommand(WindowsControlActions.Unmute)));
        Assert.True(WindowsControlIntentDetector.IsExplicitCommand("请取消系统静音", new WindowsControlCommand(WindowsControlActions.Unmute)));
        Assert.True(WindowsControlIntentDetector.IsExplicitCommand("恢复系统声音", new WindowsControlCommand(WindowsControlActions.Unmute)));
    }

    [Theory]
    [InlineData("设置 10 秒计时器", 10)]
    [InlineData("5 分钟后提醒我", 300)]
    [InlineData("倒计时 1 小时 30 分钟", 5_400)]
    [InlineData("设置半分钟倒计时", 30)]
    public void AcceptsExactTimerDurations(string request, int seconds)
    {
        Assert.True(WindowsControlIntentDetector.IsExplicitCommand(request, new WindowsControlCommand(WindowsControlActions.StartTimer, DurationSeconds: seconds)));
    }

    [Fact]
    public void RejectsInventedTimerDurationAndAcceptsExplicitCancellation()
    {
        Assert.False(WindowsControlIntentDetector.IsExplicitCommand("设置 10 秒计时器", new WindowsControlCommand(WindowsControlActions.StartTimer, DurationSeconds: 60)));
        Assert.False(WindowsControlIntentDetector.IsExplicitCommand("不要设置 10 秒计时器", new WindowsControlCommand(WindowsControlActions.StartTimer, DurationSeconds: 10)));
        Assert.True(WindowsControlIntentDetector.IsExplicitCommand("取消计时器", new WindowsControlCommand(WindowsControlActions.CancelTimer)));
    }

    [Theory]
    [InlineData("打开记事本", WindowsControlActions.OpenApplication, WindowsApplicationIds.Notepad, null, null)]
    [InlineData("把音量调到 35", WindowsControlActions.SetVolume, null, 35, null)]
    [InlineData("声音小一点", WindowsControlActions.AdjustVolume, null, -10, null)]
    [InlineData("设置 10 秒计时器", WindowsControlActions.StartTimer, null, null, 10)]
    [InlineData("取消计时器", WindowsControlActions.CancelTimer, null, null, null)]
    [InlineData("缩小谷歌浏览器", WindowsControlActions.MinimizeApplication, WindowsApplicationIds.Chrome, null, null)]
    [InlineData("切换到微信", WindowsControlActions.RestoreApplication, WindowsApplicationIds.WeChat, null, null)]
    [InlineData("放大记事本", WindowsControlActions.MaximizeApplication, WindowsApplicationIds.Notepad, null, null)]
    [InlineData("打开Word", WindowsControlActions.OpenApplication, WindowsApplicationIds.Word, null, null)]
    [InlineData("打开微软Word", WindowsControlActions.OpenApplication, WindowsApplicationIds.MicrosoftWord, null, null)]
    [InlineData("打开WPS文字", WindowsControlActions.OpenApplication, WindowsApplicationIds.WpsWriter, null, null)]
    [InlineData("打开Excel", WindowsControlActions.OpenApplication, WindowsApplicationIds.Excel, null, null)]
    [InlineData("打开微软Excel", WindowsControlActions.OpenApplication, WindowsApplicationIds.MicrosoftExcel, null, null)]
    [InlineData("打开WPS表格", WindowsControlActions.OpenApplication, WindowsApplicationIds.WpsSpreadsheets, null, null)]
    public void ParsesOnlyDeterministicCommandsForTheLocalFastPath(
        string request,
        string action,
        string? applicationId,
        int? value,
        int? durationSeconds)
    {
        Assert.True(WindowsControlIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(action, command.Action);
        Assert.Equal(applicationId, command.ApplicationId);
        Assert.Equal(value, command.Value);
        Assert.Equal(durationSeconds, command.DurationSeconds);
    }

    [Theory]
    [InlineData("不要打开记事本")]
    [InlineData("打开记事本和计算器")]
    [InlineData("用谷歌浏览器打开百度首页")]
    [InlineData("用 Chrome 搜索罗永浩")]
    [InlineData("音量舒服一点")]
    [InlineData("提醒我以后休息")]
    [InlineData("打开example")]
    public void DoesNotGuessAmbiguousOrDeniedFastPathCommands(string request)
    {
        Assert.False(WindowsControlIntentDetector.TryParseExplicitCommand(request, out _));
    }
}
