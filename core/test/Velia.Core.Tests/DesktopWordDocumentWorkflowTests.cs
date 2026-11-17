using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Velia.Core.Documents;
using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class DesktopWordDocumentWorkflowTests
{
    [Theory]
    [InlineData("在桌面创建一个Word文档，命名为秋天养生", "秋天养生.docx")]
    [InlineData("创建一个名为工作计划的Word文档", "工作计划.docx")]
    [InlineData("新建Word文档，取名叫做会议记录", "会议记录.docx")]
    public void ParsesNaturalWordCreationCommands(string request, string expected)
    {
        Assert.True(DesktopWordDocumentCreationIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(expected, command.FileName);
        Assert.True(DeterministicLocalTaskIntentDetector.TryParse(request, null, out var intent));
        Assert.Equal(DeterministicLocalTaskKind.DesktopWordDocumentCreate, intent.Kind);
    }

    [Theory]
    [InlineData("创建Word文档")]
    [InlineData("不要创建名为测试的Word文档")]
    [InlineData("怎么创建Word文档")]
    public void RejectsMissingNameDeniedAndInstructionalRequests(string request)
    {
        Assert.False(DesktopWordDocumentCreationIntentDetector.TryParseExplicitCommand(request, out _));
    }

    [Fact]
    public async Task CreatesAValidDocxWithoutOverwriteAndOpensIt()
    {
        using var root = new TemporaryDirectory();
        var automation = new FakeAutomation();
        var service = new DesktopWordDocumentWorkflowService(automation, desktopPath: root.Path);
        var result = await service.CreateAsync(
            new DesktopWordDocumentCreateCommand("秋天养生.docx", InitialText: "秋季宜早睡早起。"),
            CancellationToken.None);

        Assert.True(result.Verified);
        Assert.True(result.Opened);
        Assert.Equal("秋季宜早睡早起。", DesktopWordDocumentWorkflowService.ReadPackageText(result.FullPath));
        Assert.Equal([Path.Combine(root.Path, "秋天养生.docx")], automation.Opened);
        Assert.Empty(Directory.EnumerateFiles(root.Path, ".velia-*.tmp"));

        var conflict = await Assert.ThrowsAsync<DesktopDocumentWorkflowException>(() => service.CreateAsync(
            new DesktopWordDocumentCreateCommand("秋天养生.docx"), CancellationToken.None));
        Assert.Equal("document_already_exists", conflict.Code);
        Assert.Equal("秋季宜早睡早起。", DesktopWordDocumentWorkflowService.ReadPackageText(result.FullPath));
    }

    [Fact]
    public async Task VerifiesTheCompleteEditSaveAndCloseSequenceAgainstOpenXmlContent()
    {
        using var root = new TemporaryDirectory();
        var service = new DesktopWordDocumentWorkflowService(new FakeAutomation(), desktopPath: root.Path);
        await service.CreateAsync(
            new DesktopWordDocumentCreateCommand("记录.docx", OpenAfterCreate: false, InitialText: "今天开始记录"),
            CancellationToken.None);

        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Append, "请早睡"),
            "记录.docx",
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Replace, "明天", "今天"),
            "记录.docx",
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Delete, Match: "请"),
            "记录.docx",
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Save),
            "记录.docx",
            CancellationToken.None);
        var result = await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Close),
            "记录.docx",
            CancellationToken.None);

        Assert.True(result.Verified);
        Assert.True(result.Closed);
        Assert.Equal("明天开始记录\n早睡", DesktopWordDocumentWorkflowService.ReadPackageText(Path.Combine(root.Path, "记录.docx")));
    }

    [Fact]
    public async Task VerifiesSetInsertAndDeleteLogicalLinesAgainstTheSavedWordPackage()
    {
        using var root = new TemporaryDirectory();
        var service = new DesktopWordDocumentWorkflowService(new FakeAutomation(), desktopPath: root.Path);
        await service.CreateAsync(
            new DesktopWordDocumentCreateCommand("行级编辑.docx", OpenAfterCreate: false, InitialText: "第一行"),
            CancellationToken.None);

        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(
                DesktopTextDocumentEditActions.SetLine,
                "第三行",
                LineNumber: 3),
            "行级编辑.docx",
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(
                DesktopTextDocumentEditActions.InsertLine,
                "第二行",
                LineNumber: 2),
            "行级编辑.docx",
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(
                DesktopTextDocumentEditActions.DeleteLine,
                LineNumber: 3),
            "行级编辑.docx",
            CancellationToken.None);

        Assert.Equal(
            "第一行\n第二行\n第三行",
            DesktopWordDocumentWorkflowService.ReadPackageText(Path.Combine(root.Path, "行级编辑.docx")));
    }

    [Fact]
    public async Task OpensAnExistingDocxThroughTheManagedWriterInsteadOfTheGenericShell()
    {
        using var root = new TemporaryDirectory();
        var automation = new FakeAutomation();
        var resolver = new DesktopFileOpenService(root.Path);
        var service = new DesktopWordDocumentWorkflowService(automation, resolver, root.Path);
        await service.CreateAsync(
            new DesktopWordDocumentCreateCommand("已有记录.docx", OpenAfterCreate: false),
            CancellationToken.None);

        var result = await service.TryOpenExistingAsync(
            new DesktopFileOpenCommand("已有记录"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Verified);
        Assert.Equal("已有记录.docx", result.FileName);
        Assert.Equal([Path.Combine(root.Path, "已有记录.docx")], automation.Opened);
    }

    [Fact]
    public async Task SafelyBindsTheCurrentEditableDesktopDocumentWithoutOpeningADuplicate()
    {
        using var root = new TemporaryDirectory();
        var path = Path.Combine(root.Path, "手动打开.docx");
        File.WriteAllBytes(path, []);
        var automation = new FakeAutomation { CurrentDocumentPath = path };
        var service = new DesktopWordDocumentWorkflowService(automation, desktopPath: root.Path);

        var fileName = await service.TryBindCurrentAsync(CancellationToken.None);

        Assert.Equal("手动打开.docx", fileName);
        Assert.Empty(automation.Opened);
    }

    [Fact]
    public async Task RefusesToBindAnOpenDocumentOutsideTheDesktopBoundary()
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var path = Path.Combine(outside.Path, "其他位置.docx");
        File.WriteAllBytes(path, []);
        var automation = new FakeAutomation { CurrentDocumentPath = path };
        var service = new DesktopWordDocumentWorkflowService(automation, desktopPath: root.Path);

        Assert.Null(await service.TryBindCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task KeepsAVerifiedWordEditSuccessfulWhenWindowsRefusesForegroundOwnership()
    {
        using var root = new TemporaryDirectory();
        var automation = new FakeAutomation { Foregrounded = false };
        var service = new DesktopWordDocumentWorkflowService(automation, desktopPath: root.Path);
        await service.CreateAsync(
            new DesktopWordDocumentCreateCommand("后台记录.docx", OpenAfterCreate: false, InitialText: "原始内容"),
            CancellationToken.None);

        var result = await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Append, "新增内容"),
            "后台记录.docx",
            CancellationToken.None);

        Assert.True(result.Verified);
        Assert.False(result.Closed);
        Assert.Equal(
            "原始内容\n新增内容",
            DesktopWordDocumentWorkflowService.ReadPackageText(Path.Combine(root.Path, "后台记录.docx")));
    }

    [Fact]
    public async Task VerifiesWordCleanupAndClearAgainstSavedPackageContent()
    {
        using var root = new TemporaryDirectory();
        var service = new DesktopWordDocumentWorkflowService(new FakeAutomation(), desktopPath: root.Path);
        await service.CreateAsync(
            new DesktopWordDocumentCreateCommand(
                "清理记录.docx",
                OpenAfterCreate: false,
                InitialText: "作者：测试作者\n正文内容\n广告"),
            CancellationToken.None);

        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Clean),
            "清理记录.docx",
            CancellationToken.None);
        Assert.Equal("正文内容", DesktopWordDocumentWorkflowService.ReadPackageText(Path.Combine(root.Path, "清理记录.docx")));

        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Clear),
            "清理记录.docx",
            CancellationToken.None);
        Assert.Empty(DesktopWordDocumentWorkflowService.ReadPackageText(Path.Combine(root.Path, "清理记录.docx")));
    }

    [Fact]
    public async Task RealWordDeletionPreservesUnaffectedFormattingWhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VELIA_RUN_WORD_DELETE_SMOKE"),
                "1",
                StringComparison.Ordinal))
            return;

        using var root = new TemporaryDirectory();
        using var automation = new WindowsWordAutomationService();
        var service = new DesktopWordDocumentWorkflowService(automation, desktopPath: root.Path);
        var fileName = $"Velia内容删除烟测-{Guid.NewGuid():N}.docx";
        var created = await service.CreateAsync(
            new DesktopWordDocumentCreateCommand(fileName, OpenAfterCreate: false),
            CancellationToken.None);
        using (var package = WordprocessingDocument.Open(created.FullPath, true))
        {
            var main = package.MainDocumentPart ?? throw new InvalidDataException("DOCX main part missing.");
            var document = main.Document ?? throw new InvalidDataException("DOCX document missing.");
            var body = document.Body ?? throw new InvalidDataException("DOCX body missing.");
            body.RemoveAllChildren();
            body.Append(
                new Paragraph(new Run(new RunProperties(new Bold()), new Text("保留标题"))),
                new Paragraph(new Run(new Text("作者：测试作者"))),
                new Paragraph(new Run(new Text("正文包含广告内容和保留内容"))),
                new Paragraph(new Run(new Text("广告"))));
            document.Save();
        }
        Assert.True(await automation.OpenAsync(created.FullPath, CancellationToken.None));

        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Delete, Match: "广告内容"),
            created.FileName,
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Clean),
            created.FileName,
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Close),
            created.FileName,
            CancellationToken.None);

        Assert.Equal(
            "保留标题\n正文包含和保留内容",
            DesktopWordDocumentWorkflowService.ReadPackageText(created.FullPath));
        using (var package = WordprocessingDocument.Open(created.FullPath, false))
        {
            var main = package.MainDocumentPart ?? throw new InvalidDataException("DOCX main part missing.");
            var document = main.Document ?? throw new InvalidDataException("DOCX document missing.");
            var body = document.Body ?? throw new InvalidDataException("DOCX body missing.");
            Assert.NotNull(body
                .Elements<Paragraph>().First().GetFirstChild<Run>()?.RunProperties?.Bold);
        }

        Assert.True(await automation.OpenAsync(created.FullPath, CancellationToken.None));
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Clear),
            created.FileName,
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Close),
            created.FileName,
            CancellationToken.None);
        Assert.Empty(DesktopWordDocumentWorkflowService.ReadPackageText(created.FullPath));
    }

    [Fact]
    public async Task RealWriterLogicalLineEditingPreservesOtherParagraphsWhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VELIA_RUN_WORD_LINE_SMOKE"),
                "1",
                StringComparison.Ordinal))
            return;

        using var root = new TemporaryDirectory();
        using var automation = new WindowsWordAutomationService();
        var service = new DesktopWordDocumentWorkflowService(automation, desktopPath: root.Path);
        var created = await service.CreateAsync(
            new DesktopWordDocumentCreateCommand(
                $"Velia行级编辑烟测-{Guid.NewGuid():N}.docx",
                OpenAfterCreate: false,
                InitialText: "第一行\n原第二行"),
            CancellationToken.None);
        using (var package = WordprocessingDocument.Open(created.FullPath, true))
        {
            var document = package.MainDocumentPart?.Document ??
                throw new InvalidDataException("DOCX document missing.");
            var firstRun = document.Body?
                .Elements<Paragraph>().First().GetFirstChild<Run>();
            Assert.NotNull(firstRun);
            firstRun.RunProperties = new RunProperties(new Bold());
            document.Save();
        }
        Assert.True(await automation.OpenAsync(created.FullPath, CancellationToken.None));

        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(
                DesktopTextDocumentEditActions.SetLine,
                "第二行成功",
                LineNumber: 2),
            created.FileName,
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(
                DesktopTextDocumentEditActions.SetLine,
                "第三行成功",
                LineNumber: 3),
            created.FileName,
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(
                DesktopTextDocumentEditActions.InsertLine,
                "临时插入行",
                LineNumber: 2),
            created.FileName,
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(
                DesktopTextDocumentEditActions.DeleteLine,
                LineNumber: 2),
            created.FileName,
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Close),
            created.FileName,
            CancellationToken.None);

        Assert.Equal("第一行\n第二行成功\n第三行成功", DesktopWordDocumentWorkflowService.ReadPackageText(created.FullPath));
        using var verifiedPackage = WordprocessingDocument.Open(created.FullPath, false);
        Assert.NotNull(verifiedPackage.MainDocumentPart?.Document?.Body?
            .Elements<Paragraph>().First().GetFirstChild<Run>()?.RunProperties?.Bold);
    }

    [Fact]
    public async Task RealWriterSelectionDeletionIsSavedWhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VELIA_RUN_WORD_SELECTION_DELETE_SMOKE"),
                "1",
                StringComparison.Ordinal))
            return;

        using var root = new TemporaryDirectory();
        using var automation = new WindowsWordAutomationService();
        var service = new DesktopWordDocumentWorkflowService(automation, desktopPath: root.Path);
        var created = await service.CreateAsync(
            new DesktopWordDocumentCreateCommand(
                $"Velia选区删除烟测-{Guid.NewGuid():N}.docx",
                OpenAfterCreate: false,
                InitialText: "需要删除保留"),
            CancellationToken.None);
        Assert.True(await automation.OpenAsync(created.FullPath, CancellationToken.None));
        Assert.True(await automation.SelectRangeAsync(created.FullPath, 0, 4, CancellationToken.None));

        var result = await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.DeleteSelection),
            created.FileName,
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.Close),
            created.FileName,
            CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal("保留", DesktopWordDocumentWorkflowService.ReadPackageText(created.FullPath));
    }

    [Fact]
    public void DeletesAWriterSelectionWithoutDependingOnTheWordOnlySelectionDocumentProperty()
    {
        var document = new FakeComDocument("C:\\Users\\test\\Desktop\\文字删除验收.docx");
        var selection = new FakeComSelection("需要删除", 4, 8);
        var application = new FakeComApplication(document, selection);

        WindowsWordAutomationService.DeleteCurrentSelection(application, document);

        Assert.True(selection.Deleted);
    }

    [Fact]
    public void RefusesACollapsedWriterSelection()
    {
        var document = new FakeComDocument("C:\\Users\\test\\Desktop\\文字删除验收.docx");
        var selection = new FakeComSelection(string.Empty, 4, 4);
        var application = new FakeComApplication(document, selection);

        var exception = Assert.Throws<DesktopDocumentWorkflowException>(() =>
            WindowsWordAutomationService.DeleteCurrentSelection(application, document));

        Assert.Equal("document_selection_missing", exception.Code);
        Assert.False(selection.Deleted);
    }

    [Fact]
    public void RefusesAWriterSelectionOwnedByAnotherActiveDocument()
    {
        var bound = new FakeComDocument("C:\\Users\\test\\Desktop\\已绑定.docx");
        var active = new FakeComDocument("C:\\Users\\test\\Desktop\\另一份文档.docx");
        var selection = new FakeComSelection("不能删除", 4, 8);
        var application = new FakeComApplication(active, selection);

        var exception = Assert.Throws<DesktopDocumentWorkflowException>(() =>
            WindowsWordAutomationService.DeleteCurrentSelection(application, bound));

        Assert.Equal("document_window_mismatch", exception.Code);
        Assert.False(selection.Deleted);
    }

    [Fact]
    public void WaitsForAWriterMutationBeforeSavingAndRetriesADeferredSave()
    {
        var document = new FakeDelayedComDocument("删除前保留", "保留");

        var verified = WindowsWordAutomationService.SaveAndVerifyMutation(
            document,
            "删除前保留",
            null,
            DesktopTextDocumentEditActions.DeleteSelection,
            CancellationToken.None);

        Assert.Equal("保留", verified);
        Assert.True(document.Saved);
        Assert.Equal(2, document.SaveCalls);
    }

    [Fact]
    public async Task WaitsForTheSavedPackageWhenWriterTemporarilyLocksTheDiskFile()
    {
        using var root = new TemporaryDirectory();
        var path = Path.Combine(root.Path, "延迟保存.docx");
        var seed = new DesktopWordDocumentWorkflowService(new FakeAutomation(), desktopPath: root.Path);
        await seed.CreateAsync(
            new DesktopWordDocumentCreateCommand(
                "延迟保存.docx",
                OpenAfterCreate: false,
                InitialText: "保留"),
            CancellationToken.None);
        var automation = new DelayedDiskAutomation("保留");
        var service = new DesktopWordDocumentWorkflowService(automation, desktopPath: root.Path);

        var result = await service.ExecuteAsync(
            new DesktopTextDocumentEditCommand(DesktopTextDocumentEditActions.DeleteSelection),
            "延迟保存.docx",
            CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal("保留", DesktopWordDocumentWorkflowService.ReadPackageText(path));
    }

    private sealed class FakeAutomation : IDesktopWordAutomationService
    {
        public List<string> Opened { get; } = [];
        public bool Foregrounded { get; init; } = true;
        public string? CurrentDocumentPath { get; init; }

        public Task<string?> TryGetCurrentDocumentPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CurrentDocumentPath);

        public Task<bool> OpenAsync(string fullPath, CancellationToken cancellationToken)
        {
            Opened.Add(fullPath);
            return Task.FromResult(true);
        }

        public Task<DesktopTextEditorResult> ExecuteAsync(
            DesktopTextDocumentEditCommand command,
            string fullPath,
            CancellationToken cancellationToken)
        {
            var current = DesktopWordDocumentWorkflowService.ReadPackageText(fullPath);
            var next = command.Action switch
            {
                DesktopTextDocumentEditActions.Append => current + Environment.NewLine + command.Text,
                DesktopTextDocumentEditActions.SetLine => DesktopTextDocumentLineEditor.SetLine(current, command.LineNumber!.Value, command.Text!),
                DesktopTextDocumentEditActions.InsertLine => DesktopTextDocumentLineEditor.InsertLine(current, command.LineNumber!.Value, command.Text!),
                DesktopTextDocumentEditActions.DeleteLine => DesktopTextDocumentLineEditor.DeleteLine(current, command.LineNumber!.Value),
                DesktopTextDocumentEditActions.Replace => current.Replace(command.Match!, command.Text!, StringComparison.Ordinal),
                DesktopTextDocumentEditActions.Delete => current.Replace(command.Match!, string.Empty, StringComparison.Ordinal),
                DesktopTextDocumentEditActions.DeleteSelection => current.Length == 0 ? current : current[1..],
                DesktopTextDocumentEditActions.Clear => string.Empty,
                DesktopTextDocumentEditActions.Clean => DocumentContentCleaner.CleanText(current),
                _ => current,
            };
            DesktopWordDocumentWorkflowService.WritePackageText(fullPath, next);
            return Task.FromResult(new DesktopTextEditorResult(
                true,
                command.Action,
                next,
                Path.GetFileName(fullPath),
                command.Action == DesktopTextDocumentEditActions.Close,
                Foregrounded));
        }
    }

    public sealed class FakeComApplication(FakeComDocument document, FakeComSelection selection)
    {
        public FakeComDocument ActiveDocument { get; } = document;
        public FakeComSelection Selection { get; } = selection;
    }

    public sealed class FakeComDocument(string fullName)
    {
        public string FullName { get; } = fullName;
    }

    // Deliberately has no Document property. WPS exposes the active document
    // reliably but does not guarantee Word's Selection.Document member.
    public sealed class FakeComSelection(string text, long start, long end)
    {
        public string Text { get; } = text;
        public long Start { get; } = start;
        public long End { get; } = end;
        public bool Deleted { get; private set; }
        public void Delete() => Deleted = true;
    }

    public sealed class FakeDelayedComDocument(string before, string after)
    {
        private int contentReads;
        public int SaveCalls { get; private set; }
        public bool Saved { get; private set; }
        public FakeDelayedComRange Content
        {
            get
            {
                contentReads++;
                return new FakeDelayedComRange(contentReads < 3 ? before : after);
            }
        }

        public void Save()
        {
            SaveCalls++;
            if (SaveCalls >= 2) Saved = true;
        }
    }

    public sealed class FakeDelayedComRange(string text)
    {
        public string Text { get; } = text;
    }

    private sealed class DelayedDiskAutomation(string persistedText) : IDesktopWordAutomationService
    {
        public Task<string?> TryGetCurrentDocumentPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<bool> OpenAsync(string fullPath, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<DesktopTextEditorResult> ExecuteAsync(
            DesktopTextDocumentEditCommand command,
            string fullPath,
            CancellationToken cancellationToken)
        {
            var fileLock = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            _ = Task.Run(
                async () =>
                {
                    await Task.Delay(125).ConfigureAwait(false);
                    fileLock.Dispose();
                },
                CancellationToken.None);
            return Task.FromResult(new DesktopTextEditorResult(
                true,
                command.Action,
                persistedText,
                Path.GetFileName(fullPath)));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"velia-word-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
