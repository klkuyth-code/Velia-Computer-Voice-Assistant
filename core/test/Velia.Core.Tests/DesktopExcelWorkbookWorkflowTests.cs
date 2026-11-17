using Velia.Core.Documents;
using Velia.Core.WindowsControl;
using Xunit;

namespace Velia.Core.Tests;

public sealed class DesktopExcelWorkbookWorkflowTests
{
    [Theory]
    [InlineData("在桌面创建一个Excel表格，命名为秋天养生", "秋天养生.xlsx")]
    [InlineData("创建一个名为工作计划的Excel工作簿", "工作计划.xlsx")]
    [InlineData("新建表格文档，取名叫做销售记录", "销售记录.xlsx")]
    public void ParsesNaturalWorkbookCreationCommands(string request, string expected)
    {
        Assert.True(DesktopExcelWorkbookCreationIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(expected, command.FileName);
        Assert.True(DeterministicLocalTaskIntentDetector.TryParse(request, null, out var intent));
        Assert.Equal(DeterministicLocalTaskKind.DesktopExcelWorkbookCreate, intent.Kind);
    }

    [Theory]
    [InlineData("创建Excel表格")]
    [InlineData("不要创建名为测试的Excel表格")]
    [InlineData("怎么创建Excel表格")]
    public void RejectsMissingNameDeniedAndInstructionalCreationRequests(string request)
    {
        Assert.False(DesktopExcelWorkbookCreationIntentDetector.TryParseExplicitCommand(request, out _));
    }

    [Theory]
    [InlineData("在 A1 输入：姓名", "setCell", "A1", "姓名")]
    [InlineData("把 b2 改成 198", "setCell", "B2", "198")]
    [InlineData("把 B 二单元格设置为198", "setCell", "B2", "198")]
    [InlineData("在 A 一 单元格输入：姓名", "setCell", "A1", "姓名")]
    [InlineData("在第一行第一列输入：商品", "setCell", "A1", "商品")]
    [InlineData("在第1行第2列输入：价格", "setCell", "B1", "价格")]
    [InlineData("把第二行第二列改为168", "setCell", "B2", "168")]
    [InlineData("清空 C3", "clearCell", "C3", null)]
    [InlineData("清空第三行第二列", "clearCell", "B3", null)]
    [InlineData("清空 C 三单元格", "clearCell", "C3", null)]
    [InlineData("清空 A1 到 C 三区域", "clearRange", "A1:C3", null)]
    [InlineData("清空第一行第一列到第三行第三列", "clearRange", "A1:C3", null)]
    [InlineData("删除 C3:A1 的内容", "clearRange", "A1:C3", null)]
    [InlineData("清空当前选中的单元格", "clearSelection", null, null)]
    [InlineData("把刚才选择的区域清空", "clearSelection", null, null)]
    [InlineData("清空当前表格的全部内容", "clearSheet", null, null)]
    [InlineData("请把当前工作表清空", "clearSheet", null, null)]
    [InlineData("然后删除当前表格的 C 三单元格内容", "clearCell", "C3", null)]
    [InlineData("把当前表格里的 A1 到 C3 区域清空", "clearRange", "A1:C3", null)]
    [InlineData("删除当前表格的广告内容", "deleteText", null, "广告内容")]
    [InlineData("把当前工作表里的作者署名删掉", "deleteText", null, "作者署名")]
    [InlineData("保存当前表格", "save", null, null)]
    [InlineData("保存并关闭当前工作簿", "close", null, null)]
    [InlineData("存好后关闭当前Excel表格", "close", null, null)]
    [InlineData("把这个工作簿保存好然后关掉", "close", null, null)]
    [InlineData("保存好当前Ex表格再退出", "close", null, null)]
    [InlineData("关掉当前XLSX文件", "close", null, null)]
    [InlineData("保存并退出当前电子表格", "close", null, null)]
    [InlineData("先保存这个易克赛尔表格然后关掉", "close", null, null)]
    [InlineData("保存修改并关闭当前表格", "close", null, null)]
    public void ParsesCellAndWorkbookCommands(
        string request,
        string action,
        string? cell,
        string? value)
    {
        Assert.True(DesktopExcelCellIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(action, command.Action);
        Assert.Equal(cell, command.CellReference);
        Assert.Equal(value, command.Value);
        Assert.True(DeterministicLocalTaskIntentDetector.TryParse(request, null, out var intent));
        Assert.Equal(DeterministicLocalTaskKind.DesktopExcelCellEdit, intent.Kind);
    }

    [Theory]
    [InlineData("保存并关闭当前文档")]
    [InlineData("保存并关闭当前Word文档")]
    [InlineData("关闭当前记事本")]
    public void LeavesGenericWordAndTextLifecycleCommandsToTheSharedDocumentRoute(string request)
    {
        Assert.False(DesktopExcelCellIntentDetector.TryParseExplicitCommand(request, out _));
        Assert.True(DesktopTextDocumentEditIntentDetector.TryParseExplicitCommand(request, out var command));
        Assert.Equal(DesktopTextDocumentEditActions.Close, command.Action);
    }

    [Theory]
    [InlineData("A1", true)]
    [InlineData("B 二", true)]
    [InlineData("A一百九十八", true)]
    [InlineData("xfd1048576", true)]
    [InlineData("XFD一百零四万八千五百七十六", true)]
    [InlineData("XFE1", false)]
    [InlineData("A1048577", false)]
    [InlineData("A一百零四万八千五百七十七", false)]
    [InlineData("第一个", false)]
    [InlineData("第一行第一列", true)]
    [InlineData("第2行第2列", true)]
    [InlineData("第三列第四行", true)]
    public void ValidatesExcelCellBounds(string value, bool expected)
    {
        Assert.Equal(expected, DesktopExcelCellIntentDetector.TryNormalizeCell(value, out _));
    }

    [Theory]
    [InlineData("A1", "C3", "A1:C3")]
    [InlineData("C3", "A1", "A1:C3")]
    [InlineData("B 二", "B2", "B2")]
    public void NormalizesExcelRanges(string start, string end, string expected)
    {
        Assert.True(DesktopExcelCellIntentDetector.TryNormalizeRange(start, end, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task CreatesAValidXlsxWithoutOverwriteAndOpensIt()
    {
        using var root = new TemporaryDirectory();
        var automation = new FakeAutomation();
        var service = new DesktopExcelWorkbookWorkflowService(automation, desktopPath: root.Path);
        var result = await service.CreateAsync(
            new DesktopExcelWorkbookCreateCommand("销售记录.xlsx"),
            CancellationToken.None);

        Assert.True(result.Verified);
        Assert.True(result.Opened);
        Assert.Null(DesktopExcelWorkbookWorkflowService.ReadPackageCell(result.FullPath, "A1", formula: false));
        Assert.Equal([result.FullPath], automation.Opened);
        Assert.Empty(Directory.EnumerateFiles(root.Path, ".velia-*.tmp"));

        var conflict = await Assert.ThrowsAsync<DesktopDocumentWorkflowException>(() => service.CreateAsync(
            new DesktopExcelWorkbookCreateCommand("销售记录.xlsx"), CancellationToken.None));
        Assert.Equal("document_already_exists", conflict.Code);
    }

    [Fact]
    public async Task VerifiesCellWriteClearSaveAndCloseAgainstOpenXmlContent()
    {
        using var root = new TemporaryDirectory();
        var service = new DesktopExcelWorkbookWorkflowService(new FakeAutomation(), desktopPath: root.Path);
        await service.CreateAsync(
            new DesktopExcelWorkbookCreateCommand("销售记录.xlsx", OpenAfterCreate: false),
            CancellationToken.None);

        var written = await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.SetCell, "A1", "姓名"),
            "销售记录.xlsx",
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.SetCell, "B2", "198"),
            "销售记录.xlsx",
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.SetCell, "C3", "养生广告内容早睡早起"),
            "销售记录.xlsx",
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.DeleteText, Value: "广告内容"),
            "销售记录.xlsx",
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.ClearCell, "A1"),
            "销售记录.xlsx",
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.Save),
            "销售记录.xlsx",
            CancellationToken.None);
        var closed = await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.Close),
            "销售记录.xlsx",
            CancellationToken.None);

        Assert.True(written.Verified);
        Assert.True(closed.Closed);
        var path = Path.Combine(root.Path, "销售记录.xlsx");
        Assert.Null(DesktopExcelWorkbookWorkflowService.ReadPackageCell(path, "A1", formula: false));
        Assert.Equal("198", DesktopExcelWorkbookWorkflowService.ReadPackageCell(path, "B2", formula: false));
        Assert.Equal("养生早睡早起", DesktopExcelWorkbookWorkflowService.ReadPackageCell(path, "C3", formula: false));
        Assert.False(DesktopExcelWorkbookWorkflowService.PackageContainsText(path, "广告内容"));
    }

    [Fact]
    public async Task VerifiesRangeSelectionAndSheetClearsAgainstOpenXmlContent()
    {
        using var root = new TemporaryDirectory();
        var service = new DesktopExcelWorkbookWorkflowService(new FakeAutomation(), desktopPath: root.Path);
        await service.CreateAsync(
            new DesktopExcelWorkbookCreateCommand("清空验收.xlsx", OpenAfterCreate: false),
            CancellationToken.None);
        var path = Path.Combine(root.Path, "清空验收.xlsx");
        foreach (var cell in new[] { "A1", "B1", "A2", "B2", "D4" })
            DesktopExcelWorkbookWorkflowService.WritePackageCell(path, cell, cell);

        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.ClearRange, "A1:B2"),
            "清空验收.xlsx",
            CancellationToken.None);
        Assert.True(DesktopExcelWorkbookWorkflowService.IsPackageRangeEmpty(path, "A1:B2"));
        Assert.Equal("D4", DesktopExcelWorkbookWorkflowService.ReadPackageCell(path, "D4", formula: false));

        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.ClearSelection),
            "清空验收.xlsx",
            CancellationToken.None);
        Assert.True(DesktopExcelWorkbookWorkflowService.IsPackageRangeEmpty(path, "D4"));

        DesktopExcelWorkbookWorkflowService.WritePackageCell(path, "C3", "剩余");
        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.ClearSheet),
            "清空验收.xlsx",
            CancellationToken.None);
        Assert.True(DesktopExcelWorkbookWorkflowService.IsPackageRangeEmpty(path, "A1:D4"));
    }

    [Fact]
    public async Task OpensAnExistingXlsxThroughTheManagedSpreadsheetPipeline()
    {
        using var root = new TemporaryDirectory();
        var automation = new FakeAutomation();
        var resolver = new DesktopFileOpenService(root.Path);
        var service = new DesktopExcelWorkbookWorkflowService(automation, resolver, root.Path);
        await service.CreateAsync(
            new DesktopExcelWorkbookCreateCommand("已有表格.xlsx", OpenAfterCreate: false),
            CancellationToken.None);

        var result = await service.TryOpenExistingAsync(
            new DesktopFileOpenCommand("已有表格"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Verified);
        Assert.Equal("已有表格.xlsx", result.FileName);
        Assert.Equal([Path.Combine(root.Path, "已有表格.xlsx")], automation.Opened);
    }

    [Fact]
    public async Task RebindsTheCurrentEditableDesktopWorkbook()
    {
        using var root = new TemporaryDirectory();
        var path = Path.Combine(root.Path, "当前表格.xlsx");
        var automation = new FakeAutomation { CurrentWorkbookPath = path };
        var service = new DesktopExcelWorkbookWorkflowService(automation, desktopPath: root.Path);
        await service.CreateAsync(
            new DesktopExcelWorkbookCreateCommand("当前表格.xlsx", OpenAfterCreate: false),
            CancellationToken.None);

        Assert.Equal("当前表格.xlsx", await service.TryBindCurrentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RealSpreadsheetAutomationSmokeTestWhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VELIA_RUN_EXCEL_SMOKE"),
                "1",
                StringComparison.Ordinal))
            return;

        using var root = new TemporaryDirectory();
        using var automation = new WindowsExcelAutomationService();
        var service = new DesktopExcelWorkbookWorkflowService(
            automation,
            new DesktopFileOpenService(root.Path),
            root.Path);
        var created = await service.CreateAsync(
            new DesktopExcelWorkbookCreateCommand("Velia真实表格烟测.xlsx"),
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.SetCell, "A1", "Velia表格烟测成功"),
            created.FileName,
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.SetCell, "B2", "198"),
            created.FileName,
            CancellationToken.None);
        var closed = await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.Close),
            created.FileName,
            CancellationToken.None);
        var reopened = await service.TryOpenExistingAsync(
            new DesktopFileOpenCommand("Velia真实表格烟测"),
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.SetCell, "C3", "重新打开继续编辑成功"),
            created.FileName,
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.Close),
            created.FileName,
            CancellationToken.None);

        Assert.True(created.Opened);
        Assert.True(closed.Closed);
        Assert.NotNull(reopened);
        Assert.Equal(
            "Velia表格烟测成功",
            DesktopExcelWorkbookWorkflowService.ReadPackageCell(created.FullPath, "A1", formula: false));
        Assert.Equal("198", DesktopExcelWorkbookWorkflowService.ReadPackageCell(created.FullPath, "B2", formula: false));
        Assert.Equal(
            "重新打开继续编辑成功",
            DesktopExcelWorkbookWorkflowService.ReadPackageCell(created.FullPath, "C3", formula: false));
    }

    [Fact]
    public async Task RealSpreadsheetRangeAndSheetClearSmokeTestWhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VELIA_RUN_EXCEL_DELETE_SMOKE"),
                "1",
                StringComparison.Ordinal))
            return;

        using var root = new TemporaryDirectory();
        using var automation = new WindowsExcelAutomationService();
        var service = new DesktopExcelWorkbookWorkflowService(automation, desktopPath: root.Path);
        var fileName = $"Velia表格清空烟测-{Guid.NewGuid():N}.xlsx";
        var created = await service.CreateAsync(
            new DesktopExcelWorkbookCreateCommand(fileName),
            CancellationToken.None);
        foreach (var cell in new[] { "A1", "B2" })
            await service.ExecuteAsync(
                new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.SetCell, cell, cell),
                created.FileName,
                CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.SetCell, "C3", "C3广告内容"),
            created.FileName,
            CancellationToken.None);

        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.ClearRange, "A1:B2"),
            created.FileName,
            CancellationToken.None);
        Assert.True(DesktopExcelWorkbookWorkflowService.IsPackageRangeEmpty(created.FullPath, "A1:B2"));
        Assert.Equal("C3广告内容", DesktopExcelWorkbookWorkflowService.ReadPackageCell(created.FullPath, "C3", formula: false));

        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.DeleteText, Value: "广告内容"),
            created.FileName,
            CancellationToken.None);
        Assert.Equal("C3", DesktopExcelWorkbookWorkflowService.ReadPackageCell(created.FullPath, "C3", formula: false));
        Assert.False(DesktopExcelWorkbookWorkflowService.PackageContainsText(created.FullPath, "广告内容"));

        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.ClearSheet),
            created.FileName,
            CancellationToken.None);
        await service.ExecuteAsync(
            new DesktopExcelCellCommand(DesktopExcelWorkbookEditActions.Close),
            created.FileName,
            CancellationToken.None);
        Assert.True(DesktopExcelWorkbookWorkflowService.IsPackageRangeEmpty(created.FullPath, "A1:C3"));
    }

    [Fact]
    public async Task OpensNamedDesktopWorkbookWhenExplicitlyEnabled()
    {
        var requestedName = Environment.GetEnvironmentVariable("VELIA_RUN_EXISTING_EXCEL_OPEN");
        if (string.IsNullOrWhiteSpace(requestedName)) return;

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        using var automation = new WindowsExcelAutomationService();
        var service = new DesktopExcelWorkbookWorkflowService(
            automation,
            new DesktopFileOpenService(desktop),
            desktop);

        var result = await service.TryOpenExistingAsync(
            new DesktopFileOpenCommand(requestedName),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Verified);
        Assert.EndsWith(".xlsx", result.FileName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeAutomation : IDesktopExcelAutomationService
    {
        public List<string> Opened { get; } = [];
        public string? CurrentWorkbookPath { get; init; }

        public Task<string?> TryGetCurrentWorkbookPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CurrentWorkbookPath);

        public Task<bool> OpenAsync(string fullPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Opened.Add(fullPath);
            return Task.FromResult(true);
        }

        public Task<DesktopExcelAutomationResult> ExecuteAsync(
            DesktopExcelCellCommand command,
            string fullPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? stored = null;
            if (command.Action == DesktopExcelWorkbookEditActions.SetCell)
            {
                DesktopExcelWorkbookWorkflowService.WritePackageCell(
                    fullPath, command.CellReference!, command.Value, command.IsFormula);
                stored = command.Value;
            }
            else if (command.Action == DesktopExcelWorkbookEditActions.ClearCell)
            {
                DesktopExcelWorkbookWorkflowService.WritePackageCell(fullPath, command.CellReference!, null);
            }
            else if (command.Action == DesktopExcelWorkbookEditActions.ClearRange)
            {
                foreach (var cell in CellsInRange(command.CellReference!))
                    DesktopExcelWorkbookWorkflowService.WritePackageCell(fullPath, cell, null);
            }
            else if (command.Action == DesktopExcelWorkbookEditActions.ClearSelection)
            {
                DesktopExcelWorkbookWorkflowService.WritePackageCell(fullPath, "D4", null);
                command = command with { CellReference = "D4" };
            }
            else if (command.Action == DesktopExcelWorkbookEditActions.ClearSheet)
            {
                foreach (var cell in new[] { "A1", "B1", "A2", "B2", "C3", "D4" })
                    DesktopExcelWorkbookWorkflowService.WritePackageCell(fullPath, cell, null);
                command = command with { CellReference = "A1:D4" };
            }
            else if (command.Action == DesktopExcelWorkbookEditActions.DeleteText)
            {
                var replaced = false;
                foreach (var cell in new[] { "A1", "B1", "A2", "B2", "C3", "D4" })
                {
                    var current = DesktopExcelWorkbookWorkflowService.ReadPackageCell(fullPath, cell, formula: false);
                    if (current?.Contains(command.Value!, StringComparison.Ordinal) != true) continue;
                    DesktopExcelWorkbookWorkflowService.WritePackageCell(
                        fullPath, cell, current.Replace(command.Value!, string.Empty, StringComparison.Ordinal));
                    replaced = true;
                }
                if (!replaced)
                    throw new DesktopDocumentWorkflowException("document_text_not_found", $"当前表格中没有找到“{command.Value}”。" );
                command = command with { CellReference = "A1:D4" };
                stored = command.Value;
            }
            return Task.FromResult(new DesktopExcelAutomationResult(
                true,
                command.Action,
                command.CellReference,
                stored,
                command.Action == DesktopExcelWorkbookEditActions.Close));
        }

        private static IEnumerable<string> CellsInRange(string range)
        {
            if (range == "A1:B2") return ["A1", "B1", "A2", "B2"];
            return [range];
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"velia-excel-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
