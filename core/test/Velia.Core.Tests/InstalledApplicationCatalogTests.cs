using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class InstalledApplicationCatalogTests
{
    [Theory]
    [InlineData("剪映专业版")]
    [InlineData("剪映专业版软件")]
    [InlineData("剪映")]
    public void ResolvesUniqueNaturalApplicationNames(string spokenName)
    {
        var catalog = CreateCatalog(
            Entry("installed:capcut", "剪映专业版", "JianyingPro"),
            Entry("installed:music", "网易云音乐", "cloudmusic"));

        var result = catalog.Resolve(spokenName);

        Assert.Equal(InstalledApplicationResolutionState.Found, result.State);
        Assert.Equal("installed:capcut", result.Entry?.Id);
    }

    [Fact]
    public void DoesNotGuessBetweenSimilarInstalledApplications()
    {
        var catalog = CreateCatalog(
            Entry("installed:studio-a", "创作工作室", "StudioA"),
            Entry("installed:studio-b", "创作工作台", "StudioB"));

        var result = catalog.Resolve("创作工作");

        Assert.Equal(InstalledApplicationResolutionState.Ambiguous, result.State);
        Assert.Null(result.Entry);
        Assert.Equal(2, result.Candidates?.Count);
    }

    [Fact]
    public void ParsesAnIndexedApplicationThroughTheSharedFastPath()
    {
        var catalog = CreateCatalog(Entry("installed:capcut", "剪映专业版", "JianyingPro"));

        var parsed = DeterministicLocalTaskIntentDetector.TryParse(
            "请打开剪映专业版",
            null,
            out var intent,
            catalog);

        Assert.True(parsed);
        Assert.Equal(DeterministicLocalTaskKind.WindowsControl, intent.Kind);
        Assert.Equal(WindowsControlActions.OpenApplication, intent.WindowsControl?.Action);
        Assert.Equal("installed:capcut", intent.WindowsControl?.ApplicationId);
    }

    [Fact]
    public void ExactInstalledApplicationWinsOverGenericDesktopFileWording()
    {
        var catalog = CreateCatalog(Entry("installed:cloud", "夸克网盘", "QuarkCloudDrive"));

        var parsed = DeterministicLocalTaskIntentDetector.TryParse(
            "打开桌面上的夸克网盘",
            null,
            out var intent,
            catalog);

        Assert.True(parsed);
        Assert.Equal(DeterministicLocalTaskKind.WindowsControl, intent.Kind);
        Assert.Equal("installed:cloud", intent.WindowsControl?.ApplicationId);
    }

    [Fact]
    public void ConcreteDesktopDocumentStillWinsOverAnApplicationWithASimilarName()
    {
        var catalog = CreateCatalog(Entry("installed:capcut", "剪映专业版", "JianyingPro"));

        var parsed = DeterministicLocalTaskIntentDetector.TryParse(
            "打开桌面的剪映专业版介绍.docx",
            null,
            out var intent,
            catalog);

        Assert.True(parsed);
        Assert.Equal(DeterministicLocalTaskKind.DesktopFileOpen, intent.Kind);
    }

    [Theory]
    [InlineData("关闭剪映专业版", "closeApplication")]
    [InlineData("把剪映专业版最小化", "minimizeApplication")]
    [InlineData("最大化剪映专业版窗口", "maximizeApplication")]
    [InlineData("切换到剪映专业版", "restoreApplication")]
    public void ParsesWindowOperationsForIndexedApplications(string request, string expectedAction)
    {
        var catalog = CreateCatalog(Entry("installed:capcut", "剪映专业版", "JianyingPro"));

        var parsed = WindowsControlIntentDetector.TryParseExplicitCommand(request, catalog, out var command);

        Assert.True(parsed);
        Assert.Equal(expectedAction, command.Action);
        Assert.Equal("installed:capcut", command.ApplicationId);
    }

    [Fact]
    public void ExposesBoundedApplicationNamesForAsrHotwords()
    {
        var catalog = CreateCatalog(
            Entry("installed:capcut", "剪映专业版", "JianyingPro"),
            Entry("installed:music", "网易云音乐", "cloudmusic"));

        var hotwords = catalog.GetHotwords();

        Assert.Contains("剪映专业版", hotwords);
        Assert.Contains("网易云音乐", hotwords);
        Assert.True(hotwords.Count <= 200);
    }

    [Fact]
    public void WarmUpBuildsTheSharedSnapshotOnlyOnce()
    {
        var calls = 0;
        var entry = Entry("installed:capcut", "剪映专业版", "JianyingPro");
        var catalog = new WindowsInstalledApplicationCatalog(
            entryProvider: () => { calls++; return [entry]; },
            validateSuppliedEntries: false);

        catalog.WarmUp();
        _ = catalog.GetHotwords();
        _ = catalog.Resolve("剪映专业版");

        Assert.Equal(1, calls);
    }

    [Fact]
    public void ResolvesRealMachineApplicationsWhenExplicitlyEnabled()
    {
        var requested = Environment.GetEnvironmentVariable("VELIA_RUN_INSTALLED_APPLICATION_SMOKE");
        if (string.IsNullOrWhiteSpace(requested)) return;

        var catalog = new WindowsInstalledApplicationCatalog();
        catalog.WarmUp();
        foreach (var name in requested.Split(
                     [';', '；', ','],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var result = catalog.Resolve(name);
            Assert.True(
                result.State == InstalledApplicationResolutionState.Found,
                $"'{name}' was {result.State}; candidates: {string.Join(", ", result.Candidates ?? [])}");
            Assert.NotNull(result.Entry);
            Assert.True(WindowsInstalledApplicationCatalog.IsTrustedLaunchTarget(result.Entry.LaunchTarget));
            Assert.True(
                result.Entry.ProcessNames.Count > 0 ||
                string.Equals(Path.GetExtension(result.Entry.LaunchTarget), ".lnk", StringComparison.OrdinalIgnoreCase),
                $"'{name}' has no process identity or trusted shortcut fallback: {result.Entry.LaunchTarget}");
        }
    }

    [Fact]
    public async Task OpensRealMachineApplicationsWhenExplicitlyEnabled()
    {
        var requested = Environment.GetEnvironmentVariable("VELIA_RUN_APPLICATION_OPEN_SMOKE");
        if (string.IsNullOrWhiteSpace(requested)) return;

        var catalog = new WindowsInstalledApplicationCatalog();
        var controller = new WindowsApplicationController(catalog);
        foreach (var name in requested.Split(
                     [';', '；', ','],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var resolution = catalog.Resolve(name);
            Assert.Equal(InstalledApplicationResolutionState.Found, resolution.State);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await controller.OpenAsync(resolution.Entry!.Id, timeout.Token);
            Assert.True(result.Verified);
            Assert.Equal(resolution.Entry.Id, result.ApplicationId);
        }
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
