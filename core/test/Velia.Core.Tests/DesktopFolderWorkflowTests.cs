using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class DesktopFolderWorkflowTests
{
    [Theory]
    [InlineData("在桌面创建一个文件夹，命名为项目资料", "项目资料", false)]
    [InlineData("新建文件夹叫八月资料", "八月资料", false)]
    [InlineData("桌面建个叫客户文档的文件夹", "客户文档", false)]
    [InlineData("创建一个项目资料文件夹", "项目资料", false)]
    [InlineData("麻烦帮我建立一个名为秋季素材的目录", "秋季素材", false)]
    [InlineData("创建一个叫验收资料的文件夹，然后打开", "验收资料", true)]
    [InlineData("请创建并打开一个名为今日工作的新文件夹", "今日工作", true)]
    public void ParsesNaturalNamedFolderCreation(
        string request,
        string expectedName,
        bool expectedOpen)
    {
        Assert.True(DesktopFolderCreationIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(expectedName, command.FolderName);
        Assert.Equal(expectedOpen, command.OpenAfterCreate);

        Assert.True(DeterministicLocalTaskIntentDetector.TryParse(request, null, out var intent));
        Assert.Equal(DeterministicLocalTaskKind.DesktopFolderCreate, intent.Kind);
        Assert.Equal(expectedName, intent.DesktopFolderCreate!.FolderName);
    }

    [Theory]
    [InlineData("创建文件夹")]
    [InlineData("在桌面新建一个文件夹吧")]
    [InlineData("帮我建立一个目录")]
    public void ParsesUnnamedFolderCreationWithSafeAutomaticName(string request)
    {
        Assert.True(DesktopFolderCreationIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Null(command.FolderName);
        Assert.False(command.OpenAfterCreate);
    }

    [Theory]
    [InlineData("怎么创建文件夹")]
    [InlineData("如何新建一个文件夹")]
    [InlineData("不要创建文件夹")]
    [InlineData("先别帮我建立目录")]
    [InlineData("创建一个名为文件夹整理方案的文档")]
    [InlineData("创建一个名为客户资料的 Word 文档")]
    [InlineData("我想了解创建文件夹的方法")]
    public void RejectsQuestionsNegationAndOtherArtifactKinds(string request)
    {
        Assert.False(DesktopFolderCreationIntentDetector.TryParseExplicitCommand(request, out _));
    }

    [Fact]
    public async Task CreatesNamedFolderAndVerifiesDiskStateWithoutOpeningByDefault()
    {
        using var workspace = new TemporaryDirectory();
        var opener = new FakeFileOpener();
        var service = new DesktopFolderWorkflowService(opener, workspace.Path);

        var result = await service.CreateAsync(
            new DesktopFolderCreateCommand("项目资料"),
            CancellationToken.None);

        Assert.True(result.Verified);
        Assert.False(result.Opened);
        Assert.Equal("created", result.State);
        Assert.Equal("项目资料", result.FolderName);
        Assert.True(Directory.Exists(Path.Combine(workspace.Path, "项目资料")));
        Assert.Empty(opener.Commands);
    }

    [Fact]
    public async Task CreatesAndOpensFolderOnlyWhenExplicitlyRequested()
    {
        using var workspace = new TemporaryDirectory();
        var opener = new FakeFileOpener();
        var service = new DesktopFolderWorkflowService(opener, workspace.Path);

        var result = await service.CreateAsync(
            new DesktopFolderCreateCommand("打开验收", OpenAfterCreate: true),
            CancellationToken.None);

        Assert.True(result.Verified);
        Assert.True(result.Opened);
        Assert.Equal("打开验收文件夹", Assert.Single(opener.Commands).RequestedName);
    }

    [Fact]
    public async Task BareCreationUsesTheNextAvailableWindowsStyleName()
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "新建文件夹"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "新建文件夹 (2)"));
        var service = new DesktopFolderWorkflowService(new FakeFileOpener(), workspace.Path);

        var result = await service.CreateAsync(
            new DesktopFolderCreateCommand(null),
            CancellationToken.None);

        Assert.Equal("新建文件夹 (3)", result.FolderName);
        Assert.True(Directory.Exists(Path.Combine(workspace.Path, result.FolderName)));
    }

    [Fact]
    public async Task NeverOverwritesAnExistingFolderOrFile()
    {
        using var workspace = new TemporaryDirectory();
        var existingFolder = Path.Combine(workspace.Path, "已有资料");
        Directory.CreateDirectory(existingFolder);
        await File.WriteAllTextAsync(Path.Combine(existingFolder, "必须保留.txt"), "保留");
        var service = new DesktopFolderWorkflowService(new FakeFileOpener(), workspace.Path);

        var exception = await Assert.ThrowsAsync<DesktopFolderWorkflowException>(() =>
            service.CreateAsync(new DesktopFolderCreateCommand("已有资料"), CancellationToken.None));

        Assert.Equal("folder_already_exists", exception.Code);
        Assert.Equal("保留", await File.ReadAllTextAsync(Path.Combine(existingFolder, "必须保留.txt")));
    }

    [Theory]
    [InlineData("..\\越界")]
    [InlineData("CON")]
    [InlineData("错误/目录")]
    [InlineData("尾部句点.")]
    public async Task RejectsUnsafeFolderNames(string folderName)
    {
        using var workspace = new TemporaryDirectory();
        var service = new DesktopFolderWorkflowService(new FakeFileOpener(), workspace.Path);

        var exception = await Assert.ThrowsAsync<DesktopFolderWorkflowException>(() =>
            service.CreateAsync(new DesktopFolderCreateCommand(folderName), CancellationToken.None));

        Assert.Equal("folder_name_invalid", exception.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Path));
    }

    [Fact]
    public async Task PreservesCreatedFolderWhenExplorerCannotBeVerified()
    {
        using var workspace = new TemporaryDirectory();
        var service = new DesktopFolderWorkflowService(
            new FakeFileOpener(new WindowsControlException(
                "desktop_folder_open_unverified",
                "未验证窗口",
                true)),
            workspace.Path);

        var exception = await Assert.ThrowsAsync<DesktopFolderWorkflowException>(() =>
            service.CreateAsync(
                new DesktopFolderCreateCommand("已创建资料", OpenAfterCreate: true),
                CancellationToken.None));

        Assert.Equal("folder_created_open_failed", exception.Code);
        Assert.True(exception.FolderCreated);
        Assert.True(Directory.Exists(Path.Combine(workspace.Path, "已创建资料")));
    }

    private sealed class FakeFileOpener(Exception? failure = null) : IDesktopFileOpenService
    {
        public List<DesktopFileOpenCommand> Commands { get; } = [];

        public Task<DesktopFileOpenResult> OpenAsync(
            DesktopFileOpenCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (failure is not null) throw failure;
            return Task.FromResult(new DesktopFileOpenResult(
                true,
                "folderOpened",
                command.RequestedName,
                IsFolder: true));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"velia-folder-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
