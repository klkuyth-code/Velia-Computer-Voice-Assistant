using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class DesktopFileOpenServiceTests
{
    [Theory]
    [InlineData("普通模式 Word 验收.docx", "普通模式,Word 验收文档")]
    [InlineData("Excel 统一边界验收.xlsx", "Excel 统一边界验收表格")]
    public void ResolvesNamedOfficeFilesInsteadOfOnlyOpeningTheirApplication(
        string fileName,
        string spokenName)
    {
        var directory = CreateDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, fileName), "test");
            var service = new DesktopFileOpenService(directory);

            var file = service.ResolveFile(spokenName);

            Assert.Equal(fileName, file.Name);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ResolvesTheUniqueDesktopFileDespiteARecognitionNearMiss()
    {
        var directory = CreateDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "GPT导演决策.txt"), "test");
            File.WriteAllText(Path.Combine(directory, "普通测试.txt"), "test");
            var service = new DesktopFileOpenService(directory);

            var file = service.ResolveFile("导演角色稿");

            Assert.Equal("GPT导演决策.txt", file.Name);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RejectsAmbiguousNearMatches()
    {
        var directory = CreateDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "GPT导演决策.txt"), "test");
            File.WriteAllText(Path.Combine(directory, "AI导演计划.txt"), "test");
            var service = new DesktopFileOpenService(directory);

            var error = Assert.Throws<WindowsControlException>(() => service.ResolveFile("导演角色稿"));

            Assert.Equal("desktop_file_not_found", error.Code);
            Assert.Contains("多个相近文件", error.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void NeverTreatsExecutableContentAsAnOpenableDesktopDocument()
    {
        var directory = CreateDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "目标.exe"), "not-an-executable");
            var service = new DesktopFileOpenService(directory);

            var error = Assert.Throws<WindowsControlException>(() => service.ResolveFile("目标"));

            Assert.Equal("desktop_file_not_found", error.Code);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("项目资料文件夹")]
    [InlineData("项目资料目录")]
    [InlineData("项目资料")]
    public void ResolvesDesktopFoldersThroughTheSameBoundedIndex(string spokenName)
    {
        var directory = CreateDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "项目资料"));
            File.WriteAllText(Path.Combine(directory, "其他资料.txt"), "test");
            var service = new DesktopFileOpenService(directory);

            var item = service.ResolveItem(spokenName);

            Assert.IsType<DirectoryInfo>(item);
            Assert.Equal("项目资料", item.Name);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"velia-desktop-file-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
