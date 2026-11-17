using Velia.Core.Documents;
using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class DesktopTextDocumentEditingTests
{
    [Theory]
    [InlineData("在当前文档末尾输入：秋天适合早睡早起", "append", "秋天适合早睡早起", null)]
    [InlineData("把文档里的早睡替换成早睡早起", "replace", "早睡早起", "早睡")]
    [InlineData("删除文档里的广告内容", "delete", null, "广告内容")]
    [InlineData("删除当前文档的广告内容", "delete", null, "广告内容")]
    [InlineData("把当前文档里的广告内容删掉", "delete", null, "广告内容")]
    [InlineData("然后去掉 Word 文档中的作者署名", "delete", null, "作者署名")]
    [InlineData("末尾输入：秋天养生，早睡早起", "append", "秋天养生,早睡早起", null)]
    [InlineData("未尾输入养生广告内容早睡早起", "append", "养生广告内容早睡早起", null)]
    [InlineData("把刚才选中的内容删掉", "deleteSelection", null, null)]
    [InlineData("删除当前选中的文字", "deleteSelection", null, null)]
    [InlineData("清空当前文档", "clear", null, null)]
    [InlineData("删除当前 Word 文档的全部内容", "clear", null, null)]
    [InlineData("删除作者署名和广告，只保留正文", "clean", null, null)]
    [InlineData("保存文档", "save", null, null)]
    [InlineData("保存并关闭文档", "close", null, null)]
    [InlineData("保存并关闭当前 Word 文档", "close", null, null)]
    [InlineData("存好后关掉这个Wod文档", "close", null, null)]
    [InlineData("保存好这个WPS文档然后退出", "close", null, null)]
    [InlineData("把当前记事本存一下再关掉", "close", null, null)]
    [InlineData("这个文本文件保存后关闭", "close", null, null)]
    [InlineData("保存好再退出", "close", null, null)]
    [InlineData("关闭并保存当前Word文档", "close", null, null)]
    [InlineData("关闭当前文档", "close", null, null)]
    [InlineData("保存一下", "save", null, null)]
    [InlineData("存好当前文档", "save", null, null)]
    [InlineData("先保存一下，然后关闭当前文档", "close", null, null)]
    [InlineData("保存完就关闭", "close", null, null)]
    [InlineData("保存好这个文档然后把它关掉", "close", null, null)]
    [InlineData("保存更改并关闭当前文档", "close", null, null)]
    [InlineData("这个Word保存一下再关了吧", "close", null, null)]
    public void ParsesBoundedNaturalDocumentCommands(string request, string action, string? text, string? match)
    {
        Assert.True(DesktopTextDocumentEditIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(action, command.Action);
        Assert.Equal(text, command.Text);
        Assert.Equal(match, command.Match);
    }

    [Theory]
    [InlineData("保存并关闭当前 Word 文档", "close")]
    [InlineData("存好后关掉这个Wod文档", "close")]
    [InlineData("保存好再退出", "close")]
    [InlineData("保存一下", "save")]
    public void RoutesNaturalLifecycleCommandsThroughTheSharedDeterministicDocumentPath(
        string request,
        string expectedAction)
    {
        Assert.True(DeterministicLocalTaskIntentDetector.TryParse(request, null, out var intent));
        Assert.Equal(DeterministicLocalTaskKind.DesktopDocumentEdit, intent.Kind);
        Assert.Equal(expectedAction, intent.DesktopDocumentEdit?.Action);
    }

    [Theory]
    [InlineData("写入：今天你吃饭了吗？你过得怎么样？", "今天你吃饭了吗？你过得怎么样")]
    [InlineData("把连续测试成功写进文档", "连续测试成功")]
    [InlineData("然后输入内容：第二段自然语言", "第二段自然语言")]
    [InlineData("记下 秋天适合早睡早起", "秋天适合早睡早起")]
    public void ParsesImplicitDocumentWritesOnlyWhenAContextWasVerified(
        string request,
        string expectedText)
    {
        Assert.False(DesktopTextDocumentEditIntentDetector.TryParseExplicitCommand(request, out _));
        Assert.True(DesktopTextDocumentEditIntentDetector.TryParseContextualCommand(request, out var command));
        Assert.Equal(DesktopTextDocumentEditActions.Append, command.Action);
        Assert.Equal(expectedText, command.Text);

        Assert.True(DeterministicLocalTaskIntentDetector.TryParse(
            request,
            null,
            out var contextual,
            allowImplicitTextDocumentTarget: true));
        Assert.Equal(DeterministicLocalTaskKind.DesktopDocumentEdit, contextual.Kind);

        if (DeterministicLocalTaskIntentDetector.TryParse(request, null, out var unscoped))
            Assert.NotEqual(DeterministicLocalTaskKind.DesktopDocumentEdit, unscoped.Kind);
    }

    [Fact]
    public void AllowsBareCloseOnlyInsideAVerifiedDocumentContext()
    {
        Assert.False(DesktopTextDocumentEditIntentDetector.TryParseExplicitCommand("关闭", out _));
        Assert.True(DesktopTextDocumentEditIntentDetector.TryParseContextualCommand("关闭", out var command));
        Assert.Equal(DesktopTextDocumentEditActions.Close, command.Action);
    }

    [Theory]
    [InlineData("在末尾写入：连续测试成功", "append", null, "连续测试成功")]
    [InlineData("写到最后面：连续测试成功", "append", null, "连续测试成功")]
    [InlineData("在第二行写入：第二行内容", "setLine", 2, "第二行内容")]
    [InlineData("第三行填写第三行内容", "setLine", 3, "第三行内容")]
    [InlineData("把当前 Word 文档的第十二行改为：新内容", "setLine", 12, "新内容")]
    [InlineData("在第二〇二行写入：编号内容", "setLine", 202, "编号内容")]
    [InlineData("在文档第三行前插入：插入内容", "insertLine", 3, "插入内容")]
    [InlineData("删除文档里的第二行", "deleteLine", 2, null)]
    [InlineData("把 WPS 文档第三行的内容删掉", "deleteLine", 3, null)]
    public void ParsesDocumentPositionsWithoutRequiringARepeatedDocumentName(
        string request,
        string action,
        int? lineNumber,
        string? text)
    {
        Assert.True(DesktopTextDocumentEditIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(action, command.Action);
        Assert.Equal(lineNumber, command.LineNumber);
        Assert.Equal(text, command.Text);
    }

    [Theory]
    [InlineData("在第二行第一列输入：商品")]
    [InlineData("在第三行第二列写入：198")]
    public void DoesNotMisclassifyExcelCoordinatesAsTextDocumentLines(string request)
    {
        Assert.False(DesktopTextDocumentEditIntentDetector.TryParseExplicitCommand(request, out _));
    }

    [Fact]
    public void AppliesSetInsertAndDeleteLineWithStableLogicalLineSemantics()
    {
        var set = DesktopTextDocumentLineEditor.SetLine("第一行", 3, "第三行");
        var inserted = DesktopTextDocumentLineEditor.InsertLine(set, 2, "第二行");
        var deleted = DesktopTextDocumentLineEditor.DeleteLine(inserted, 3);

        Assert.Equal("第一行\n\n第三行", set);
        Assert.Equal("第一行\n第二行\n\n第三行", inserted);
        Assert.Equal("第一行\n第二行\n第三行", deleted);
    }

    [Fact]
    public void RejectsDeletingALogicalLineThatDoesNotExist()
    {
        var error = Assert.Throws<DesktopDocumentWorkflowException>(() =>
            DesktopTextDocumentLineEditor.DeleteLine("只有一行", 2));

        Assert.Equal("document_line_not_found", error.Code);
    }

    [Theory]
    [InlineData("不要删除文档里的内容")]
    [InlineData("怎么替换文档里的文字")]
    [InlineData("输入密码")]
    [InlineData("关闭浏览器")]
    [InlineData("根据当前记事本里的题目，在文档末尾输入正确答案和计算式")]
    [InlineData("在桌面创建一个文档。命名为删除验收。")]
    [InlineData("在桌面创建一个完档。命名为删除验收。")]
    [InlineData("创建一个文档，命名为清空计划")]
    [InlineData("关闭")]
    [InlineData("退出")]
    [InlineData("退出Word")]
    [InlineData("保存密码并关闭当前文档")]
    [InlineData("保存网页并关闭标签页")]
    [InlineData("保存并关闭Word和Excel")]
    [InlineData("不要保存并关闭当前Word文档")]
    [InlineData("不保存直接关闭当前Word文档")]
    [InlineData("放弃修改并关闭当前Word文档")]
    [InlineData("怎么保存并关闭当前Word文档")]
    public void RejectsDeniedInstructionalUnscopedAndUnrelatedCommands(string request)
    {
        Assert.False(DesktopTextDocumentEditIntentDetector.TryParseExplicitCommand(request, out _));
    }

    [Theory]
    [InlineData("关闭Word")]
    [InlineData("退出微软 Excel")]
    public void LeavesBareApplicationCloseCommandsOnTheWindowsControlRoute(string request)
    {
        Assert.True(DeterministicLocalTaskIntentDetector.TryParse(request, null, out var intent));
        Assert.Equal(DeterministicLocalTaskKind.WindowsControl, intent.Kind);
    }

    [Fact]
    public async Task VerifiesEditorContentAgainstTheBoundDesktopFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"velia-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "小鹿乱撞.txt");
            await File.WriteAllTextAsync(path, "第一行\r\n第二行");
            var editor = new FakeEditor(path, "第一行\r\n第二行\r\n第三行");
            var service = new DesktopTextDocumentWorkflowService(editor, root);

            var result = await service.ExecuteAsync(
                new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Append, "第三行"),
                "小鹿乱撞.txt",
                CancellationToken.None);

            Assert.True(result.Verified);
            Assert.Equal("小鹿乱撞.txt", result.FileName);
            Assert.Equal("第三行", editor.LastCommand?.Text);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RejectsAStaleOrNonDesktopDocumentBinding()
    {
        var root = Path.Combine(Path.GetTempPath(), $"velia-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var service = new DesktopTextDocumentWorkflowService(new FakeEditor(string.Empty, string.Empty), root);
            var error = await Assert.ThrowsAsync<DesktopDocumentWorkflowException>(() => service.ExecuteAsync(
                new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Save),
                "..\\其他.txt",
                CancellationToken.None));
            Assert.Equal("document_binding_invalid", error.Code);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RebindsTheCurrentVerifiedDesktopTextDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), $"velia-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "当前记录.txt"), "内容");
            var service = new DesktopTextDocumentWorkflowService(
                new FakeEditor(string.Empty, string.Empty) { CurrentFileName = "当前记录.txt" },
                root);

            Assert.Equal("当前记录.txt", await service.TryBindCurrentAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BindsAndEditsAnUntitledNotepadWithoutClaimingDiskPersistence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"velia-untitled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var editor = new FakeEditor(string.Empty, "综合压力测试成功")
            {
                CurrentFileName = DesktopTextDocumentBindings.UntitledNotepad,
            };
            var service = new DesktopTextDocumentWorkflowService(editor, root);

            var binding = await service.TryBindCurrentAsync(CancellationToken.None);
            var result = await service.ExecuteAsync(
                new DesktopTextDocumentEditCommand(
                    DesktopTextDocumentEditActions.Append,
                    "综合压力测试成功"),
                binding!,
                CancellationToken.None);

            Assert.Equal(DesktopTextDocumentBindings.UntitledNotepad, binding);
            Assert.Equal(DesktopTextDocumentBindings.UntitledNotepadDisplayName, result.FileName);
            Assert.False(result.Persisted);
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CleansKnownNoiseWithoutRemovingOrdinaryArticleParagraphs()
    {
        const string input = "作者：测试作者\n正文第一段。\n广告\n标题Qilai Shen for The New York Times正文第二段。\n免费下载 示例iOS 和 Android App";

        var cleaned = DocumentContentCleaner.CleanText(input);

        Assert.Equal("正文第一段。\n标题正文第二段。", cleaned);
    }

    private sealed class FakeEditor(string path, string content) : IDesktopTextEditorService
    {
        public DesktopTextDocumentEditCommand? LastCommand { get; private set; }
        public string? CurrentFileName { get; init; }

        public Task<string?> TryGetCurrentTextDocumentFileNameAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CurrentFileName);

        public async Task<DesktopTextEditorResult> ExecuteTextDocumentAsync(
            DesktopTextDocumentEditCommand command,
            string expectedFileName,
            CancellationToken cancellationToken)
        {
            LastCommand = command;
            if (path.Length > 0) await File.WriteAllTextAsync(path, content, cancellationToken);
            return new DesktopTextEditorResult(true, command.Action, content, expectedFileName, command.Action == "close");
        }
    }
}
