using Velia.Core.DesktopContext;
using Xunit;

namespace Velia.Core.Tests;

public sealed class DesktopNamedFileMoveTests
{
    [Theory]
    [InlineData("把桌面的移动验收.txt移动到资料文件夹", "移动验收.txt", "资料")]
    [InlineData("请将桌面上的移动验收记事本放进归档文件夹里", "移动验收记事本", "归档")]
    [InlineData("让桌面的计划.docx进入完成文件夹内", "计划.docx", "完成")]
    [InlineData("把桌面上的报价表格挪到八月资料文件夹里面", "报价表格", "八月资料")]
    [InlineData("把桌面的项目文件夹移动到资料", "项目文件夹", "资料")]
    [InlineData("把桌面上的计划文档放进资料文件夹", "计划文档", "资料")]
    [InlineData("把桌面的语音移动测试文件移动到归档资料文件夹", "语音移动测试文件", "归档资料")]
    [InlineData("把桌面的语音测试文件移动到归档资料文件夹", "语音测试文件", "归档资料")]
    [InlineData("把桌面的语音移动文件夹移动到归档资料文件夹", "语音移动文件夹", "归档资料")]
    public void ParsesNamedDesktopFileMoves(
        string request,
        string expectedSource,
        string expectedDestination)
    {
        Assert.True(DesktopNamedFileMoveIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(expectedSource, command.SourceName);
        Assert.Equal(expectedDestination, command.DestinationFolderName);
    }

    [Theory]
    [InlineData("不要把桌面的计划.txt移动到资料文件夹")]
    [InlineData("怎么把桌面的计划.txt移动到资料文件夹")]
    [InlineData("进入范冰冰主页")]
    [InlineData("把下载目录里的计划.txt移动到资料文件夹")]
    public void RejectsDeniedInstructionalAndNonDesktopMoves(string request)
    {
        Assert.False(DesktopNamedFileMoveIntentDetector.TryParseExplicitCommand(request, out _));
    }

    [Fact]
    public async Task ResolvesASpokenTextFileNameAndUsesVerifiedMoveSemantics()
    {
        var root = CreateTestRoot();
        try
        {
            var source = Path.Combine(root, "移动验收.txt");
            var destination = Path.Combine(root, "资料");
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(source, "不应丢失的内容");
            var service = new DesktopNamedFileMoveService(root);

            var result = await service.ExecuteAsync(
                new DesktopNamedFileMoveCommand("移动验收记事本", "资料"),
                CancellationToken.None);

            Assert.True(result.Verified);
            Assert.False(File.Exists(source));
            Assert.Equal("不应丢失的内容", await File.ReadAllTextAsync(Path.Combine(destination, "移动验收.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RefusesToOverwriteAnExistingDestinationFile()
    {
        var root = CreateTestRoot();
        try
        {
            var source = Path.Combine(root, "同名保护.txt");
            var destination = Path.Combine(root, "资料");
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(source, "原文件");
            await File.WriteAllTextAsync(Path.Combine(destination, "同名保护.txt"), "目标文件");
            var service = new DesktopNamedFileMoveService(root);

            var error = await Assert.ThrowsAsync<DesktopContextException>(() => service.ExecuteAsync(
                new DesktopNamedFileMoveCommand("同名保护.txt", "资料"),
                CancellationToken.None));

            Assert.Equal("destination_conflict", error.Code);
            Assert.True(File.Exists(source));
            Assert.Equal("目标文件", await File.ReadAllTextAsync(Path.Combine(destination, "同名保护.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RequiresAnExtensionWhenSeveralDesktopFilesShareAStem()
    {
        var root = CreateTestRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "资料"));
            await File.WriteAllTextAsync(Path.Combine(root, "同名.txt"), "文本");
            await File.WriteAllTextAsync(Path.Combine(root, "同名.csv"), "表格");
            var service = new DesktopNamedFileMoveService(root);

            var error = await Assert.ThrowsAsync<DesktopContextException>(() => service.ExecuteAsync(
                new DesktopNamedFileMoveCommand("同名", "资料"),
                CancellationToken.None));

            Assert.Equal("source_file_ambiguous", error.Code);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MovesAFolderIntoANestedDesktopFolderAndPreservesItsContents()
    {
        var root = CreateTestRoot();
        try
        {
            var source = Path.Combine(root, "项目");
            var destination = Path.Combine(root, "资料", "归档");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(source, "说明.txt"), "文件夹内容");
            var service = new DesktopNamedFileMoveService(root);

            var result = await service.ExecuteAsync(
                new DesktopNamedFileMoveCommand("项目文件夹", "归档文件夹"),
                CancellationToken.None);

            Assert.True(result.Verified);
            Assert.False(Directory.Exists(source));
            Assert.Equal(
                "文件夹内容",
                await File.ReadAllTextAsync(Path.Combine(destination, "项目", "说明.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ResolvesAFolderCalledAFileInSpokenLanguage()
    {
        var root = CreateTestRoot();
        try
        {
            var source = Path.Combine(root, "语音移动测试");
            var destination = Path.Combine(root, "归档资料");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(source, "内容.txt"), "保留内容");
            var service = new DesktopNamedFileMoveService(root);

            var result = await service.ExecuteAsync(
                new DesktopNamedFileMoveCommand("语音移动测试文件", "归档资料文件夹"),
                CancellationToken.None);

            Assert.True(result.Verified);
            Assert.False(Directory.Exists(source));
            Assert.Equal(
                "保留内容",
                await File.ReadAllTextAsync(Path.Combine(destination, "语音移动测试", "内容.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("语音测试文件")]
    [InlineData("语音移动文件夹")]
    public async Task ResolvesAUniqueFolderWhenSpeechOmitsTwoCharacters(string spokenName)
    {
        var root = CreateTestRoot();
        try
        {
            var source = Path.Combine(root, "语音移动测试");
            var destination = Path.Combine(root, "归档资料");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(source, "内容.txt"), "保留内容");
            var service = new DesktopNamedFileMoveService(root);

            var result = await service.ExecuteAsync(
                new DesktopNamedFileMoveCommand(spokenName, "归档资料文件夹"),
                CancellationToken.None);

            Assert.True(result.Verified);
            Assert.False(Directory.Exists(source));
            Assert.Equal(
                "保留内容",
                await File.ReadAllTextAsync(Path.Combine(destination, "语音移动测试", "内容.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RefusesToGuessWhenSeveralFoldersAreEquallyCloseToSpeech()
    {
        var root = CreateTestRoot();
        try
        {
            var first = Path.Combine(root, "语音移动测试");
            var second = Path.Combine(root, "语音识别测试");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            Directory.CreateDirectory(Path.Combine(root, "归档资料"));
            var service = new DesktopNamedFileMoveService(root);

            var error = await Assert.ThrowsAsync<DesktopContextException>(() => service.ExecuteAsync(
                new DesktopNamedFileMoveCommand("语音测试文件", "归档资料"),
                CancellationToken.None));

            Assert.Equal("source_file_ambiguous", error.Code);
            Assert.True(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DoesNotFuzzyMatchAnUnderspecifiedShortName()
    {
        var root = CreateTestRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "测试资料"));
            Directory.CreateDirectory(Path.Combine(root, "归档资料"));
            var service = new DesktopNamedFileMoveService(root);

            var error = await Assert.ThrowsAsync<DesktopContextException>(() => service.ExecuteAsync(
                new DesktopNamedFileMoveCommand("测试文件", "归档资料"),
                CancellationToken.None));

            Assert.Equal("source_file_not_found", error.Code);
            Assert.True(Directory.Exists(Path.Combine(root, "测试资料")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RefusesToMoveAFolderIntoItsOwnDescendant()
    {
        var root = CreateTestRoot();
        try
        {
            var source = Path.Combine(root, "项目");
            var destination = Path.Combine(source, "归档");
            Directory.CreateDirectory(destination);
            var service = new DesktopNamedFileMoveService(root);

            var error = await Assert.ThrowsAsync<DesktopContextException>(() => service.ExecuteAsync(
                new DesktopNamedFileMoveCommand("项目文件夹", "归档"),
                CancellationToken.None));

            Assert.Equal("invalid_file_operation", error.Code);
            Assert.True(Directory.Exists(source));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateTestRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var root = Path.Combine(documents, $"velia-named-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
