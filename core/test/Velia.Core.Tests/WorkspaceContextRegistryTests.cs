using Velia.Core.Tasks;
using Xunit;

namespace Velia.Core.Tests;

public sealed class WorkspaceContextRegistryTests
{
    [Fact]
    public void KeepsBrowserDocumentAndWeChatContextsIndependent()
    {
        var registry = new WorkspaceContextRegistry();
        registry.BindForRequest(
            "在记事本打开计划.txt",
            MissionContextBindings.FromRequest("在记事本打开计划.txt"));
        registry.BindForRequest(
            "在微博搜索范冰冰",
            MissionContextBindings.FromRequest("在微博搜索范冰冰"));
        registry.BindForRequest(
            "切换到微信",
            MissionContextBindings.FromRequest("切换到微信"));

        var snapshots = registry.GetSnapshots();

        Assert.Equal(3, snapshots.Length);
        Assert.Contains(snapshots, item =>
            item.Kind == WorkspaceResourceKind.Document &&
            item.Context.SelectedFile == "计划.txt");
        Assert.Contains(snapshots, item =>
            item.Kind == WorkspaceResourceKind.Browser &&
            item.Context.WebPlatform == "微博" &&
            item.Context.WebEntity == "范冰冰");
        Assert.Contains(snapshots, item =>
            item.Kind == WorkspaceResourceKind.WeChat &&
            item.Context.TargetApplication == "微信");
    }

    [Fact]
    public void RetrievesTheLatestDocumentWhileBrowserRemainsActive()
    {
        var coordinator = new MissionCoordinator();
        coordinator.ObserveVerifiedDocument("秋天养生.docx");
        coordinator.ObserveVerifiedWorkspaceAction("打开谷歌浏览器");

        var active = coordinator.GetActiveWorkspace();
        var document = coordinator.GetLatestWorkspace(WorkspaceResourceKind.Document);

        Assert.Equal(WorkspaceResourceKind.Browser, active?.Kind);
        Assert.Equal("秋天养生.docx", document?.Context.SelectedFile);
    }

    [Fact]
    public void IgnoresANewerGenericDocumentMentionWhenRetrievingTheLatestBoundFile()
    {
        var coordinator = new MissionCoordinator();
        coordinator.ObserveVerifiedDocument("秋天养生.docx");
        coordinator.ObserveVerifiedWorkspaceAction("打开谷歌浏览器");
        coordinator.Submit("把当前网页正文写入刚才的 Word 文档", "voice", MissionRelationship.New);

        var document = coordinator.GetLatestWorkspace(WorkspaceResourceKind.Document);

        Assert.Equal("秋天养生.docx", document?.Context.SelectedFile);
    }

    [Fact]
    public void RestoresOnlyTheRequestedWorkspace()
    {
        var registry = new WorkspaceContextRegistry();
        registry.BindForRequest(
            "在记事本打开计划.txt",
            MissionContextBindings.FromRequest("在记事本打开计划.txt"));
        registry.BindForRequest(
            "在微博搜索范冰冰",
            MissionContextBindings.FromRequest("在微博搜索范冰冰"));
        registry.BindForRequest(
            "切换到微信",
            MissionContextBindings.FromRequest("切换到微信"));

        var document = registry.BindForRequest(
            "回到刚才的文档",
            MissionContextBindings.FromRequest("回到刚才的文档"));
        var browser = registry.BindForRequest(
            "回到之前的网页",
            MissionContextBindings.FromRequest("回到之前的网页"));
        var weChat = registry.BindForRequest(
            "回到微信",
            MissionContextBindings.FromRequest("回到微信"));

        Assert.Equal("记事本", document.TargetApplication);
        Assert.Equal("计划.txt", document.SelectedFile);
        Assert.Null(document.WebPlatform);
        Assert.Equal("微博", browser.WebPlatform);
        Assert.Equal("范冰冰", browser.WebEntity);
        Assert.Null(browser.SelectedFile);
        Assert.Equal("微信", weChat.TargetApplication);
        Assert.Null(weChat.SelectedFile);
        Assert.Null(weChat.WebPlatform);
    }

    [Fact]
    public void NaturalDocumentEditsAndLifecycleCommandsKeepTheConcreteFileBinding()
    {
        var coordinator = new MissionCoordinator();
        coordinator.ObserveVerifiedDocument("秋天养生.docx");

        var edit = coordinator.Submit("删除当前文档的广告内容", "voice");
        Assert.Equal("秋天养生.docx", edit.Context.SelectedFile);
        coordinator.Complete(edit.Id, "指定文字已删除");

        var close = coordinator.Submit(
            "保存并关闭当前 Word 文档",
            "voice",
            MissionRelationship.Append);

        Assert.Equal("秋天养生.docx", close.Context.SelectedFile);
        Assert.Equal(WorkspaceResourceKind.Document, coordinator.GetActiveWorkspace()?.Kind);
    }

    [Fact]
    public void CreatingOrOpeningANewDocumentDoesNotBorrowThePreviousFileBinding()
    {
        var coordinator = new MissionCoordinator();
        coordinator.ObserveVerifiedDocument("旧文档.docx");

        var mission = coordinator.Submit("创建一个新的 Word 文档", "voice");

        Assert.Null(mission.Context.SelectedFile);
    }

    [Fact]
    public void MissionCoordinatorRestoresDocumentAfterBrowserAndWeChatSwitches()
    {
        var coordinator = new MissionCoordinator();
        var mission = coordinator.Submit("在记事本打开计划.txt", "voice");
        coordinator.Complete(mission.Id, "文档已打开");

        mission = coordinator.Submit("打开谷歌浏览器", "voice");
        coordinator.Complete(mission.Id, "浏览器已打开");

        mission = coordinator.Submit("切换到微信", "voice");
        coordinator.Complete(mission.Id, "微信已显示");

        mission = coordinator.Submit("回到刚才的文档", "voice");

        Assert.Equal("记事本", mission.Context.TargetApplication);
        Assert.Equal("记事本", mission.Context.TargetWindow);
        Assert.Equal("计划.txt", mission.Context.SelectedFile);
        Assert.Null(mission.Context.WebPlatform);
    }

    [Fact]
    public void ClosingAWorkspaceInvalidatesOnlyThatWorkspace()
    {
        var registry = new WorkspaceContextRegistry();
        var coordinator = new MissionCoordinator(workspaceContextRegistry: registry);
        var document = coordinator.Submit("在记事本打开计划.txt", "voice");
        coordinator.Complete(document.Id, "文档已打开");
        var weChat = coordinator.Submit("打开微信", "voice");
        coordinator.Complete(weChat.Id, "微信已打开");

        var close = coordinator.Submit("关闭微信", "voice");
        coordinator.Complete(close.Id, "微信已关闭");

        var snapshots = registry.GetSnapshots();
        Assert.DoesNotContain(snapshots, item => item.Kind == WorkspaceResourceKind.WeChat);
        Assert.Contains(snapshots, item => item.Kind == WorkspaceResourceKind.Document);
    }

    [Fact]
    public void ExpiresEachWorkspaceAccordingToItsOwnLifetime()
    {
        var clock = new AdjustableTimeProvider(
            new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero));
        var registry = new WorkspaceContextRegistry(clock);
        registry.BindForRequest(
            "在记事本打开计划.txt",
            MissionContextBindings.FromRequest("在记事本打开计划.txt"));
        registry.BindForRequest(
            "切换到微信",
            MissionContextBindings.FromRequest("切换到微信"));

        clock.Advance(TimeSpan.FromMinutes(16));

        var snapshots = registry.GetSnapshots();
        Assert.DoesNotContain(snapshots, item => item.Kind == WorkspaceResourceKind.WeChat);
        Assert.Contains(snapshots, item => item.Kind == WorkspaceResourceKind.Document);
    }

    [Fact]
    public void VerifiedLiveFastPathUpdatesWorkspaceWithoutReplacingTheMission()
    {
        var coordinator = new MissionCoordinator();
        var mission = coordinator.Submit("打开计算器", "voice");

        coordinator.ObserveVerifiedWorkspaceAction("打开谷歌浏览器");

        Assert.Equal(mission.Id, coordinator.GetCurrent()!.Id);
        var workspace = coordinator.GetActiveWorkspace();
        Assert.NotNull(workspace);
        Assert.Equal(WorkspaceResourceKind.Browser, workspace.Kind);
        Assert.Equal("谷歌浏览器", workspace.Context.TargetApplication);
    }

    [Fact]
    public void ConcurrentObservationsKeepAllResourceScopesConsistent()
    {
        var registry = new WorkspaceContextRegistry();
        var requests = new[]
        {
            "在记事本打开计划.txt",
            "打开谷歌浏览器",
            "打开微信",
            "打开计算器",
        };

        Parallel.For(0, 2_000, index =>
        {
            var request = requests[index % requests.Length];
            registry.CompleteVerifiedRequest(request);
        });

        var snapshots = registry.GetSnapshots();
        Assert.Contains(snapshots, item => item.Kind == WorkspaceResourceKind.Document);
        Assert.Contains(snapshots, item => item.Kind == WorkspaceResourceKind.Browser);
        Assert.Contains(snapshots, item => item.Kind == WorkspaceResourceKind.WeChat);
        Assert.Contains(snapshots, item =>
            item.Kind == WorkspaceResourceKind.Application &&
            item.Context.TargetApplication == "计算器");
        Assert.All(snapshots, item => Assert.True(item.Revision > 0));
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }
}
