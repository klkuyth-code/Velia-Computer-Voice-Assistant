using Velia.Core.Tasks;
using Xunit;

namespace Velia.Core.Tests;

public sealed class MissionCoordinatorTests
{
    [Fact]
    public void SupportsAppendReplaceProgressAndCancellationWithoutGuessingIntent()
    {
        var coordinator = new MissionCoordinator();
        var started = coordinator.Submit("整理桌面资料", "voice");
        Assert.Equal(MissionLifecycleState.Accepted, started.State);

        var appended = coordinator.Submit("不要移动原文件", "voice", MissionRelationship.Append);
        Assert.Equal(started.Id, appended.Id);
        Assert.Equal(2, appended.Revision);
        Assert.Equal(MissionLifecycleState.Correcting, appended.State);
        Assert.Equal(2, appended.Requirements.Length);

        var progress = coordinator.Submit("进度", "voice", MissionRelationship.Progress);
        Assert.Same(appended, progress);

        var replaced = coordinator.Submit("只生成分类建议", "voice", MissionRelationship.Replace);
        Assert.Equal(3, replaced.Revision);
        Assert.Single(replaced.Requirements);
        Assert.Equal("只生成分类建议", replaced.CurrentRequest);

        var cancelled = coordinator.Cancel("用户要求停止");
        Assert.Equal(MissionLifecycleState.Cancelled, cancelled.State);
    }

    [Fact]
    public void RejectsAmbiguousNewWorkWhileAMissionIsActive()
    {
        var coordinator = new MissionCoordinator();
        coordinator.Submit("读取文档", "voice");

        Assert.Throws<InvalidOperationException>(() => coordinator.Submit("打开浏览器", "voice"));
    }

    [Fact]
    public void RejectsAStaleWorkerTransition()
    {
        var coordinator = new MissionCoordinator();
        coordinator.Submit("读取文档", "voice");

        Assert.Throws<InvalidOperationException>(() => coordinator.MarkRunning(Guid.NewGuid()));
    }

    [Fact]
    public void KeepsWindowAndMenuBindingsAcrossNaturalContinuations()
    {
        var coordinator = new MissionCoordinator();
        var mission = coordinator.Submit("在记事本窗口点击查看菜单", "voice");

        Assert.Equal("记事本", mission.Context.TargetApplication);
        Assert.Equal("记事本", mission.Context.TargetWindow);
        Assert.Equal("查看", mission.Context.ActiveMenu);

        mission = coordinator.Submit("然后点文件", "voice", MissionRelationship.Append);
        Assert.Equal("记事本", mission.Context.TargetApplication);
        Assert.Equal("文件", mission.Context.ActiveMenu);
        Assert.Equal("文件", mission.Context.LastTarget);
        Assert.Contains("在记事本窗口点击查看菜单", mission.CurrentRequest, StringComparison.Ordinal);
        Assert.Contains("补充要求：然后点文件", mission.CurrentRequest, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsWebPlatformAndEntityAcrossPronounContinuations()
    {
        var coordinator = new MissionCoordinator();
        var mission = coordinator.Submit("在微博搜索范冰冰", "live");

        Assert.Equal("微博", mission.Context.WebPlatform);
        Assert.Equal("范冰冰", mission.Context.WebEntity);

        mission = coordinator.Complete(mission.Id, "已显示搜索结果");
        mission = coordinator.Submit("进入她的个人主页", "live", MissionRelationship.Append);

        Assert.Equal("微博", mission.Context.WebPlatform);
        Assert.Equal("范冰冰", mission.Context.WebEntity);
        Assert.Contains("进入她的个人主页", mission.CurrentRequest, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextualCorrectionPreservesTheBoundTarget()
    {
        var coordinator = new MissionCoordinator();
        var mission = coordinator.Submit("在记事本窗口点击文件菜单", "voice");

        mission = coordinator.Submit("改成点保存", "voice", MissionRelationship.Replace);

        Assert.Equal("记事本", mission.Context.TargetApplication);
        Assert.Equal("文件", mission.Context.ActiveMenu);
        Assert.Equal("保存", mission.Context.LastTarget);
        Assert.Contains("纠正要求：改成点保存", mission.CurrentRequest, StringComparison.Ordinal);
        Assert.Equal(2, mission.Requirements.Length);
    }

    [Fact]
    public void DoesNotMistakeTheGenericCurrentWindowForAnApplicationOrMenu()
    {
        var coordinator = new MissionCoordinator();

        var mission = coordinator.Submit("点击当前窗口的查看菜单", "voice");

        Assert.Null(mission.Context.TargetApplication);
        Assert.Equal("当前窗口", mission.Context.TargetWindow);
        Assert.Equal("查看", mission.Context.ActiveMenu);
    }

    [Fact]
    public void ExtractsAnUnknownExplicitlyNamedWindowWithoutCapturingControlWords()
    {
        var coordinator = new MissionCoordinator();

        var mission = coordinator.Submit("在 Photoshop 窗口点击查看菜单", "voice");

        Assert.Equal("Photoshop", mission.Context.TargetApplication);
        Assert.Equal("Photoshop", mission.Context.TargetWindow);
        Assert.Equal("查看", mission.Context.ActiveMenu);
    }

    [Fact]
    public void TargetReplacementRebuildsBindingsAndCompletionKeepsThemBriefly()
    {
        var coordinator = new MissionCoordinator();
        var mission = coordinator.Submit("在记事本窗口点击查看菜单", "voice");

        mission = coordinator.Submit("打开谷歌浏览器", "voice", MissionRelationship.Replace);
        Assert.Equal("谷歌浏览器", mission.Context.TargetApplication);
        Assert.Null(mission.Context.ActiveMenu);

        mission = coordinator.Complete(mission.Id, "完成");
        Assert.Equal("谷歌浏览器", mission.Context.TargetApplication);
        Assert.Equal("谷歌浏览器", mission.Context.TargetWindow);
    }

    [Fact]
    public void ReusesACompletedMissionForALowRiskContinuationWithinTwoMinutes()
    {
        var clock = new AdjustableTimeProvider(
            new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero));
        var coordinator = new MissionCoordinator(clock);
        var mission = coordinator.Submit("在记事本窗口点击查看菜单", "voice");
        mission = coordinator.Complete(mission.Id, "已打开查看菜单");

        clock.Advance(TimeSpan.FromSeconds(90));
        mission = coordinator.Submit("然后点文件", "voice", MissionRelationship.Append);

        Assert.Equal(MissionLifecycleState.Correcting, mission.State);
        Assert.Equal("记事本", mission.Context.TargetApplication);
        Assert.Equal("文件", mission.Context.ActiveMenu);
        Assert.Equal("文件", mission.Context.LastTarget);
        Assert.Equal(2, mission.Revision);
    }

    [Fact]
    public void DoesNotReuseACompletedMissionAfterTheContinuationWindow()
    {
        var clock = new AdjustableTimeProvider(
            new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero));
        var coordinator = new MissionCoordinator(clock);
        var mission = coordinator.Submit("在记事本窗口点击查看菜单", "voice");
        coordinator.Complete(mission.Id, "已打开查看菜单");

        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromMilliseconds(1));

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Submit("然后点文件", "voice", MissionRelationship.Append));
    }

    [Fact]
    public void RejectsAHighRiskContinuationAfterSuccessfulCompletion()
    {
        var clock = new AdjustableTimeProvider(
            new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero));
        var coordinator = new MissionCoordinator(clock);
        var mission = coordinator.Submit("在记事本窗口点击文件菜单", "voice");
        coordinator.Complete(mission.Id, "已打开文件菜单");
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Submit("然后删除这个文件", "voice", MissionRelationship.Append));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public void FailureAndCancellationClearBindings(string terminalState)
    {
        var coordinator = new MissionCoordinator();
        var mission = coordinator.Submit("在记事本窗口点击查看菜单", "voice");

        mission = terminalState == "Failed"
            ? coordinator.Fail(mission.Id, "执行失败")
            : coordinator.Cancel("用户取消");

        Assert.Equal(MissionContextBindings.Empty, mission.Context);
    }

    [Theory]
    [InlineData("然后删除这个文件")]
    [InlineData("再输入密码")]
    [InlineData("点击付款")]
    public void RejectsHighRiskActionsThatTryToInheritMissionAuthority(string request)
    {
        var coordinator = new MissionCoordinator();
        coordinator.Submit("在记事本窗口点击文件菜单", "voice");

        Assert.Throws<InvalidOperationException>(
            () => coordinator.Submit(request, "voice", MissionRelationship.Append));
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }
}
