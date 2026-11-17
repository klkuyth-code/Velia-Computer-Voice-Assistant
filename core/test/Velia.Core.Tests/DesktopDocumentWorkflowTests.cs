using System.Text;
using Velia.Core.Documents;
using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class DesktopDocumentWorkflowTests
{
    [Theory]
    [InlineData("在桌面创建一个TXT文档，命名为秋天养生", "秋天养生.txt")]
    [InlineData("创建一个名为“工作计划”的文本文档", "工作计划.txt")]
    [InlineData("新建文本文件，改名为：测试.txt，然后打开", "测试.txt")]
    [InlineData("在桌面创建一个文本文档，取名叫做小鹿乱撞。", "小鹿乱撞.txt")]
    [InlineData("创建文本文档，起名为工作记录", "工作记录.txt")]
    [InlineData("新建记事本文档，名字叫每日安排", "每日安排.txt")]
    [InlineData("在桌面创建一个记事本，命名为行级统一验收", "行级统一验收.txt")]
    [InlineData("在桌面创建一个命名为行级统一验收", "行级统一验收.txt")]
    [InlineData("在桌面创建一个文档。命名为删除验收。", "删除验收.txt")]
    [InlineData("在桌面创建一个完档。命名为删除验收。", "删除验收.txt")]
    [InlineData("创建一个文档，命名为清空计划", "清空计划.txt")]
    public void ParsesExplicitNamedTextDocumentCreation(string request, string expected)
    {
        Assert.True(DesktopDocumentCreationIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(expected, command.FileName);
        Assert.True(command.OpenAfterCreate);

        Assert.True(DeterministicLocalTaskIntentDetector.TryParse(request, null, out var intent));
        Assert.Equal(DeterministicLocalTaskKind.DesktopDocumentCreate, intent.Kind);
        Assert.Equal(expected, intent.DesktopDocumentCreate!.FileName);
    }

    [Theory]
    [InlineData("在桌面创建一个TXT文档")]
    [InlineData("不要创建名为测试的文本文档")]
    [InlineData("怎么创建名为测试的文本文档")]
    [InlineData("创建一个名为测试的Word文档")]
    [InlineData("创建一个名为测试的PDF文档")]
    [InlineData("创建一个文件夹，命名为测试")]
    public void RejectsMissingNameDeniedInstructionalAndUnsupportedKinds(string request)
    {
        Assert.False(DesktopDocumentCreationIntentDetector.TryParseExplicitCommand(request, out _));
    }

    [Fact]
    public async Task AtomicallyCreatesUtf8TextAndOpensTheVerifiedFile()
    {
        using var workspace = new TemporaryDirectory();
        var opener = new FakeFileOpener();
        var service = new DesktopDocumentWorkflowService(opener, workspace.Path);
        var command = new DesktopDocumentCreateCommand(
            "秋天养生.txt",
            InitialText: "秋季宜早睡早起。" );

        var result = await service.CreateTextDocumentAsync(command, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.True(result.Opened);
        Assert.Equal("createdAndOpened", result.State);
        Assert.Equal(["秋天养生.txt"], opener.RequestedNames);
        var bytes = await File.ReadAllBytesAsync(result.FullPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("秋季宜早睡早起。", Encoding.UTF8.GetString(bytes));
        Assert.Empty(Directory.EnumerateFiles(workspace.Path, ".velia-*.tmp"));
    }

    [Fact]
    public async Task NeverOverwritesAnExistingDocument()
    {
        using var workspace = new TemporaryDirectory();
        var path = System.IO.Path.Combine(workspace.Path, "已有内容.txt");
        await File.WriteAllTextAsync(path, "必须保留");
        var service = new DesktopDocumentWorkflowService(new FakeFileOpener(), workspace.Path);

        var exception = await Assert.ThrowsAsync<DesktopDocumentWorkflowException>(() =>
            service.CreateTextDocumentAsync(
                new DesktopDocumentCreateCommand("已有内容.txt", InitialText: "新内容"),
                CancellationToken.None));

        Assert.Equal("document_already_exists", exception.Code);
        Assert.Equal("必须保留", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(workspace.Path, ".velia-*.tmp"));
    }

    [Theory]
    [InlineData("..\\越界.txt")]
    [InlineData("CON.txt")]
    [InlineData("错误.docx")]
    [InlineData("尾部空格 .txt ")]
    public async Task RejectsUnsafeOrUnsupportedFileNames(string fileName)
    {
        using var workspace = new TemporaryDirectory();
        var service = new DesktopDocumentWorkflowService(new FakeFileOpener(), workspace.Path);

        var exception = await Assert.ThrowsAsync<DesktopDocumentWorkflowException>(() =>
            service.CreateTextDocumentAsync(
                new DesktopDocumentCreateCommand(fileName),
                CancellationToken.None));

        Assert.Equal("document_name_invalid", exception.Code);
        Assert.Empty(Directory.EnumerateFiles(workspace.Path));
    }

    [Fact]
    public async Task PreservesTheCreatedFileWhenWindowsCannotVerifyTheWindow()
    {
        using var workspace = new TemporaryDirectory();
        var service = new DesktopDocumentWorkflowService(
            new FakeFileOpener(new WindowsControlException(
                "desktop_file_open_unverified",
                "未验证窗口",
                true)),
            workspace.Path);

        var exception = await Assert.ThrowsAsync<DesktopDocumentWorkflowException>(() =>
            service.CreateTextDocumentAsync(
                new DesktopDocumentCreateCommand("已创建.txt"),
                CancellationToken.None));

        Assert.Equal("document_created_open_failed", exception.Code);
        Assert.True(exception.FileCreated);
        Assert.True(File.Exists(System.IO.Path.Combine(workspace.Path, "已创建.txt")));
    }

    private sealed class FakeFileOpener(Exception? failure = null) : IDesktopFileOpenService
    {
        public List<string> RequestedNames { get; } = [];

        public Task<DesktopFileOpenResult> OpenAsync(
            DesktopFileOpenCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedNames.Add(command.RequestedName);
            if (failure is not null) throw failure;
            return Task.FromResult(new DesktopFileOpenResult(true, "opened", command.RequestedName));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"velia-document-tests-{Guid.NewGuid():N}");
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
