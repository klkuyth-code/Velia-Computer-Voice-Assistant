using Velia.Core.Tasks;
using Xunit;

namespace Velia.Core.Tests;

public sealed class MissionIntentRouterTests
{
    [Theory]
    [InlineData("停止", "Stop")]
    [InlineData("做到哪了？", "Progress")]
    [InlineData("补充：不要关闭标签页", "Append")]
    [InlineData("改成只读取页面", "Replace")]
    public void RoutesOnlyExplicitControlLanguage(string request, string expected)
    {
        var current = new MissionCoordinator().Submit("读取网页", "voice");
        Assert.Equal(expected, MissionIntentRouter.Resolve(request, "auto", current).ToString());
    }

    [Fact]
    public void TreatsAnAnswerAsAppendOnlyWhenTheMissionIsWaitingForInput()
    {
        var coordinator = new MissionCoordinator();
        var mission = coordinator.Submit("创建文档", "voice");
        mission = coordinator.AwaitInput(mission.Id, "文件名是什么");

        Assert.Equal(
            MissionRelationship.Append,
            MissionIntentRouter.Resolve("报告.docx", "auto", mission));
    }

    [Theory]
    [InlineData("然后点文件")]
    [InlineData("再点保存")]
    [InlineData("点击编辑菜单")]
    [InlineData("请打开文件菜单")]
    public void RoutesNaturalShortActionsToTheActiveMission(string request)
    {
        var current = new MissionCoordinator().Submit("在记事本窗口点击查看菜单", "voice");

        Assert.Equal(
            MissionRelationship.Append,
            MissionIntentRouter.Resolve(request, "auto", current));
    }

    [Theory]
    [InlineData("改成点保存")]
    [InlineData("不对，点文件")]
    [InlineData("更名为最终版.txt")]
    public void RoutesNaturalCorrectionsAsReplacementOfTheCurrentPlan(string request)
    {
        var current = new MissionCoordinator().Submit("在记事本窗口点击查看菜单", "voice");

        Assert.Equal(
            MissionRelationship.Replace,
            MissionIntentRouter.Resolve(request, "auto", current));
    }

    [Theory]
    [InlineData("打开谷歌浏览器")]
    [InlineData("切换到资源管理器窗口")]
    [InlineData("新任务：查询天气")]
    public void RoutesTargetSwitchesAndNewTasksAsReplacement(string request)
    {
        var current = new MissionCoordinator().Submit("在记事本窗口点击查看菜单", "voice");

        Assert.Equal(
            MissionRelationship.Replace,
            MissionIntentRouter.Resolve(request, "auto", current));
    }

    [Theory]
    [InlineData("然后打开谷歌浏览器")]
    [InlineData("请打开谷歌浏览器")]
    public void RecognizesTargetSwitchesAfterNaturalLeadIns(string request)
    {
        var current = new MissionCoordinator().Submit("在记事本窗口点击查看菜单", "voice");

        Assert.Equal(
            MissionRelationship.Replace,
            MissionIntentRouter.Resolve(request, "auto", current));
    }

    [Theory]
    [InlineData("然后点文件", "Append")]
    [InlineData("点击编辑菜单", "Append")]
    [InlineData("进入她的个人主页", "Append")]
    [InlineData("不对，点文件", "Replace")]
    public void ReusesARecentlyCompletedMissionForNaturalLowRiskFollowUps(
        string request,
        string expected)
    {
        var completedAt = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
        var coordinator = new MissionCoordinator(new FixedTimeProvider(completedAt));
        var current = coordinator.Submit("在记事本窗口点击查看菜单", "voice");
        current = coordinator.Complete(current.Id, "完成");

        Assert.Equal(expected, MissionIntentRouter.Resolve(
            request,
            "auto",
            current,
            completedAt + TimeSpan.FromSeconds(90)).ToString());
    }

    [Theory]
    [InlineData("然后点文件")]
    [InlineData("然后删除这个文件")]
    [InlineData("然后打开谷歌浏览器")]
    public void StartsANewMissionWhenACompletedMissionCannotSafelyContinue(string request)
    {
        var completedAt = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
        var coordinator = new MissionCoordinator(new FixedTimeProvider(completedAt));
        var current = coordinator.Submit("在记事本窗口点击查看菜单", "voice");
        current = coordinator.Complete(current.Id, "完成");
        var now = request == "然后点文件"
            ? completedAt + TimeSpan.FromMinutes(2) + TimeSpan.FromMilliseconds(1)
            : completedAt + TimeSpan.FromSeconds(30);

        Assert.Equal(
            MissionRelationship.New,
            MissionIntentRouter.Resolve(request, "auto", current, now));
    }

    [Theory]
    [InlineData("然后删除这个文件")]
    [InlineData("再点击付款")]
    [InlineData("输入验证码")]
    [InlineData("发布这段内容")]
    public void DoesNotInheritMissionAuthorityForHighRiskActions(string request)
    {
        var current = new MissionCoordinator().Submit("在记事本窗口点击查看菜单", "voice");

        Assert.Equal(
            MissionRelationship.New,
            MissionIntentRouter.Resolve(request, "auto", current));
    }

    [Theory]
    [InlineData("不要删除文件")]
    [InlineData("先别提交")]
    public void AllowsNegativeSafetyConstraintsToRemainInTheMission(string request)
    {
        var current = new MissionCoordinator().Submit("整理选中的文件", "voice");

        Assert.Equal(
            MissionRelationship.Append,
            MissionIntentRouter.Resolve(request, "auto", current));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
