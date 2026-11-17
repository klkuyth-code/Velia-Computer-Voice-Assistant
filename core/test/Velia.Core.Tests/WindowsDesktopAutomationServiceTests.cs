using System.Diagnostics;
using Velia.Core.DesktopVisual;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class WindowsDesktopAutomationServiceTests
{
    [Theory]
    [InlineData("无标题 - 记事本")]
    [InlineData("*未命名 - 记事本")]
    [InlineData("Untitled - Notepad")]
    public void RecognizesLocalizedUntitledNotepadTitles(string title)
    {
        Assert.True(WindowsDesktopAutomationService.IsUntitledNotepadTitle(title));
    }

    [Theory]
    [InlineData("工作记录.txt - 记事本")]
    [InlineData("计划 - Notepad")]
    public void DoesNotTreatNamedNotepadWindowsAsUntitled(string title)
    {
        Assert.False(WindowsDesktopAutomationService.IsUntitledNotepadTitle(title));
    }

    [Fact]
    public void AcceptsTheSameForegroundWindowThatWasBoundAfterCapture()
    {
        var parameters = Request(processId: 123, processName: "notepad", windowTitle: "测试窗口");

        WindowsDesktopAutomationService.EnsureExpectedForeground(123, "Notepad", "测试窗口", parameters);
    }

    [Theory]
    [InlineData(456, "notepad", "测试窗口")]
    [InlineData(123, "chrome", "测试窗口")]
    [InlineData(123, "notepad", "另一个窗口")]
    public void RejectsTheActionWhenTheForegroundWindowChanged(int processId, string processName, string windowTitle)
    {
        var exception = Assert.Throws<DesktopVisualException>(() =>
            WindowsDesktopAutomationService.EnsureExpectedForeground(
                processId,
                processName,
                windowTitle,
                Request(123, "notepad", "测试窗口")));

        Assert.Equal("desktop_visual_target_changed", exception.Code);
        Assert.True(exception.Retryable);
    }

    [Theory]
    [InlineData("编辑菜单", "编辑")]
    [InlineData("编辑菜单。", "编辑")]
    [InlineData("“编辑”菜单", "编辑")]
    [InlineData("请在当前窗口中点击记事本的编辑菜单", "编辑")]
    [InlineData("在当前窗口的记事本中，点击编辑菜单。", "编辑")]
    [InlineData("请打开 File menu.", "File")]
    public void SelectsOneRealMenuFromNaturalLanguage(string target, string expected)
    {
        var selected = WindowsDesktopAutomationService.SelectUniqueMenuName(
            target,
            ["文件", "编辑", "查看", "File", "文件(F)"]);

        Assert.Equal(expected, selected);
    }

    [Theory]
    [InlineData("点击文件和编辑菜单")]
    [InlineData("点击不存在的工具菜单")]
    public void RejectsAmbiguousOrMissingMenuTargets(string target)
    {
        var selected = WindowsDesktopAutomationService.SelectUniqueMenuName(target, ["文件", "编辑", "查看"]);

        Assert.Null(selected);
    }

    [Theory]
    [InlineData("确定按钮", "确定")]
    [InlineData("点击继续对话", "继续对话")]
    [InlineData("选择设置标签页", "设置")]
    [InlineData("请点开搜索输入框", "搜索")]
    public void SelectsOneAccessibleControlFromNaturalLanguage(string target, string expected)
    {
        var selected = WindowsDesktopAutomationService.SelectUniqueControlName(
            target,
            ["确定", "继续对话", "设置", "搜索"]);

        Assert.Equal(expected, selected);
    }

    [Fact]
    public void RejectsAnAmbiguousAccessibleControlName()
    {
        var selected = WindowsDesktopAutomationService.SelectUniqueControlName(
            "点击保存按钮",
            ["保存", "保存"]);

        Assert.Null(selected);
    }

    [Fact]
    public void SelectsAnIconButtonByAutomationIdWhenItHasNoVisibleName()
    {
        var selected = WindowsDesktopAutomationService.SelectUniqueControlIndex(
            "点击搜索按钮",
            [
                Control(name: "", automationId: "SearchButton", type: "ControlType.Button"),
                Control(name: "聊天记录", automationId: "HistoryList", type: "ControlType.ListItem"),
            ]);

        Assert.Equal(0, selected);
    }

    [Fact]
    public void UsesRequestedControlTypeToDisambiguateAButtonFromAnEditBox()
    {
        var selected = WindowsDesktopAutomationService.SelectUniqueControlIndex(
            "点击搜索按钮",
            [
                Control(name: "搜索", automationId: "SearchEdit", type: "ControlType.Edit"),
                Control(name: "", automationId: "SearchButton", type: "ControlType.Button"),
            ]);

        Assert.Equal(1, selected);
    }

    [Fact]
    public void SelectsACommonIconThroughBilingualSemanticMetadata()
    {
        var selected = WindowsDesktopAutomationService.SelectUniqueControlIndex(
            "点击返回图标",
            [
                Control(name: "", automationId: "NavigationViewBackButton", type: "ControlType.Button"),
                Control(name: "", automationId: "RefreshButton", type: "ControlType.Button"),
            ]);

        Assert.Equal(0, selected);
    }

    [Fact]
    public void MatchesAChineseOrdinalToANumericPageControl()
    {
        var selected = WindowsDesktopAutomationService.SelectUniqueControlIndex(
            "点击第三页",
            [Control(name: "2"), Control(name: "3"), Control(name: "4")]);

        Assert.Equal(1, selected);
    }

    [Fact]
    public void KeepsEquallyStrongMetadataMatchesAmbiguous()
    {
        var selected = WindowsDesktopAutomationService.SelectUniqueControlIndex(
            "点击关闭按钮",
            [
                Control(name: "关闭", automationId: "CloseWindow", type: "ControlType.Button"),
                Control(name: "关闭", automationId: "CloseTab", type: "ControlType.Button"),
            ]);

        Assert.Null(selected);
    }

    [Fact]
    public void MessageInputNeverUsesAFocusedSearchBox()
    {
        var selected = WindowsDesktopAutomationService.SelectInputCandidateIndex(
            "消息输入框",
            [
                Input("搜索", "SearchEdit", focused: true, width: 300, height: 35),
                Input("输入消息", "ChatInput", y: 600, width: 700, height: 180),
            ]);

        Assert.Equal(1, selected);
    }

    [Fact]
    public void SearchInputNeverUsesAFocusedMessageEditor()
    {
        var selected = WindowsDesktopAutomationService.SelectInputCandidateIndex(
            "搜索框",
            [
                Input("搜索", "SearchEdit", width: 300, height: 35),
                Input("输入消息", "ChatInput", focused: true, y: 600, width: 700, height: 180),
            ]);

        Assert.Equal(0, selected);
    }

    [Fact]
    public void UnscopedInputUsesTheSingleFocusedWritableControl()
    {
        var selected = WindowsDesktopAutomationService.SelectInputCandidateIndex(
            "当前输入框",
            [
                Input("搜索", "SearchEdit", width: 300, height: 35),
                Input("备注", "NotesEdit", focused: true, y: 400, width: 500, height: 100),
            ]);

        Assert.Equal(1, selected);
    }

    [Fact]
    public void MessageInputUsesTheBottomEditorWhenAChatHistoryIsLarger()
    {
        var selected = WindowsDesktopAutomationService.SelectInputCandidateIndex(
            "消息输入框",
            [
                Input("聊天记录", "HistoryDocument", y: 100, width: 800, height: 450, type: "ControlType.Document"),
                Input("", "", y: 575, width: 800, height: 140, type: "ControlType.Document"),
            ]);

        Assert.Equal(1, selected);
    }

    [Fact]
    public void AmbiguousEqualInputAreasAreRejected()
    {
        var selected = WindowsDesktopAutomationService.SelectInputCandidateIndex(
            "当前输入框",
            [
                Input("内容一", "FirstEdit", width: 400, height: 100),
                Input("内容二", "SecondEdit", y: 200, width: 400, height: 100),
            ]);

        Assert.Null(selected);
    }

    [Theory]
    [InlineData("Weixin")]
    [InlineData("WeChatAppEx")]
    [InlineData("QQ")]
    [InlineData("QQNT")]
    [InlineData("WXWork")]
    [InlineData("DingTalk")]
    [InlineData("Feishu")]
    [InlineData("Lark")]
    public void RecognizesSupportedMessagingProcesses(string processName)
    {
        Assert.True(WindowsDesktopAutomationService.IsMessagingProcess(processName));
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("chrome")]
    [InlineData("WINWORD")]
    [InlineData("explorer")]
    public void DoesNotAuthorizeSubmitInOrdinaryApplications(string processName)
    {
        Assert.False(WindowsDesktopAutomationService.IsMessagingProcess(processName));
    }

    [Theory]
    [InlineData("微信", "Weixin", true)]
    [InlineData("微信", "notepad", false)]
    [InlineData("微信", "WXWork", false)]
    [InlineData("QQ", "QQNT", true)]
    [InlineData("企业微信", "WXWork", true)]
    [InlineData("钉钉", "DingTalk", true)]
    [InlineData("飞书", "Lark", true)]
    public void BindsNamedMessageInputToTheExpectedForegroundApplication(
        string application,
        string processName,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsDesktopAutomationService.MatchesMessagingApplication(application, processName));
    }

    [Fact]
    public void ResolvesStableWeixinEditorAndSubmitPointsFromTheCurrentWindow()
    {
        var editor = WindowsDesktopAutomationService.ResolveKnownMessagingPoint(
            "Weixin",
            "消息输入框",
            left: 1_097,
            top: 114,
            right: 1_990,
            bottom: 1_001);
        var submit = WindowsDesktopAutomationService.ResolveKnownMessagingPoint(
            "Weixin",
            "发送按钮",
            left: 1_097,
            top: 114,
            right: 1_990,
            bottom: 1_001);

        Assert.Equal(new WindowsDesktopAutomationService.MessagingLayoutPoint(1_651, 806), editor);
        Assert.Equal(new WindowsDesktopAutomationService.MessagingLayoutPoint(1_936, 957), submit);
    }

    [Fact]
    public void KeepsTheWeixinSubmitPointInsideACompactWindowButton()
    {
        var submit = WindowsDesktopAutomationService.ResolveKnownMessagingPoint(
            "Weixin",
            "发送按钮",
            left: 981,
            top: 324,
            right: 1_864,
            bottom: 965);

        Assert.Equal(new WindowsDesktopAutomationService.MessagingLayoutPoint(1_811, 933), submit);
    }

    [Theory]
    [InlineData("notepad", "消息输入框", 1_000, 800)]
    [InlineData("Weixin", "搜索框", 1_000, 800)]
    [InlineData("Weixin", "消息输入框", 640, 480)]
    public void DoesNotApplyTheMessagingLayoutOutsideAKnownSafeSurface(
        string processName,
        string target,
        int width,
        int height)
    {
        Assert.Null(WindowsDesktopAutomationService.ResolveKnownMessagingPoint(
            processName,
            target,
            0,
            0,
            width,
            height));
    }

    [Fact]
    public void RequiresARealChatRegionMutationBeforeReportingMessageSubmission()
    {
        var unchanged = Enumerable.Repeat((byte)100, 400 * 240 * 4).ToArray();
        var changed = unchanged.ToArray();
        for (var pixel = 0; pixel < 400; pixel++)
        {
            var offset = pixel * 4;
            changed[offset] = 220;
            changed[offset + 1] = 220;
            changed[offset + 2] = 220;
        }
        var before = new WindowsDesktopAutomationService.MessagingMutationSample(400, 240, unchanged);

        Assert.False(WindowsDesktopAutomationService.HasMeaningfulPixelChange(
            before,
            new WindowsDesktopAutomationService.MessagingMutationSample(400, 240, unchanged.ToArray())));
        Assert.True(WindowsDesktopAutomationService.HasMeaningfulPixelChange(
            before,
            new WindowsDesktopAutomationService.MessagingMutationSample(400, 240, changed)));
    }

    [Theory]
    [InlineData(50, 25, 3, true)]
    [InlineData(50, 75, -3, true)]
    [InlineData(0, 0, 3, true)]
    [InlineData(100, 100, -3, true)]
    [InlineData(50, 50, 3, false)]
    [InlineData(50, 50, -3, false)]
    [InlineData(25, 50, 3, false)]
    [InlineData(75, 50, -3, false)]
    public void VerifiesNativeVerticalScrollDirectionAndBoundaries(
        double before,
        double after,
        int delta,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsDesktopAutomationService.IsVerticalScrollVerified(before, after, delta));
    }

    [Fact]
    public async Task RealNotepadTextEntryIsReadBackWhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VELIA_RUN_DESKTOP_TEXT_INPUT_SMOKE"),
                "1",
                StringComparison.Ordinal))
            return;

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var fileName = $"Velia通用输入烟测-{Guid.NewGuid():N}.txt";
        var path = Path.Combine(desktop, fileName);
        await File.WriteAllTextAsync(path, string.Empty);
        try
        {
            _ = Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
            var service = new WindowsDesktopAutomationService();
            DeterministicDesktopActionResult? entered = null;
            Exception? lastError = null;
            for (var attempt = 0; attempt < 30 && entered is null; attempt++)
            {
                await Task.Delay(100);
                try
                {
                    entered = await service.ExecuteAsync(
                        new DeterministicDesktopActionCommand(
                            DeterministicDesktopActionTypes.TypeText,
                            "当前输入框",
                            "真实通用输入成功。"),
                        CancellationToken.None);
                }
                catch (DesktopVisualException exception)
                {
                    lastError = exception;
                }
            }
            Assert.True(entered?.Verified, lastError?.Message);
            Assert.Equal("textInputVerified", entered!.State);
        }
        finally
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            foreach (var process in Process.GetProcessesByName("notepad"))
            {
                using (process)
                {
                    if (!process.MainWindowTitle.Contains(stem, StringComparison.Ordinal)) continue;
                    if (!process.CloseMainWindow() || !process.WaitForExit(1_000)) process.Kill();
                }
            }
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static WindowsDesktopAutomationService.AccessibleControlDescriptor Control(
        string name,
        string automationId = "",
        string helpText = "",
        string accessKey = "",
        string type = "ControlType.Button") =>
        new(name, automationId, helpText, accessKey, type);

    private static WindowsDesktopAutomationService.AccessibleInputDescriptor Input(
        string name,
        string automationId,
        bool focused = false,
        double x = 0,
        double y = 0,
        double width = 100,
        double height = 30,
        string type = "ControlType.Edit") =>
        new(name, automationId, string.Empty, type, focused, x, y, width, height);

    private static DesktopVisualExecuteActionParams Request(int processId, string processName, string windowTitle) => new(
        Guid.Parse("1196ea21-1692-46a8-b87b-92c608429ead"),
        Guid.Parse("88dbc31f-e8e4-4500-8af5-cad59b87acdf"),
        DesktopVisualActions.Click,
        "确定按钮",
        "1",
        100,
        100,
        null,
        null,
        null,
        processId,
        processName,
        windowTitle);
}
