using System.Text.Json;
using Velia.Core.DesktopContext;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class DesktopContextServiceTests
{
    [Fact]
    public void UsesTheNativeWindowsInputStructureSize()
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, WindowsDesktopContextBackend.NativeInputSize);
    }

    [Fact]
    public void MarksNavigationKeysAsExtendedWindowsInput()
    {
        Assert.Equal(0x0001u, WindowsDesktopContextBackend.KeyboardInputFlags(0x25, up: false));
        Assert.Equal(0x0003u, WindowsDesktopContextBackend.KeyboardInputFlags(0x25, up: true));
        Assert.Equal(0x0000u, WindowsDesktopContextBackend.KeyboardInputFlags(0x43, up: false));
        Assert.Equal(0x0002u, WindowsDesktopContextBackend.KeyboardInputFlags(0x43, up: true));
    }

    [Fact]
    public void AcceptsWindowsClipboardAliasesButNotMissingCustomFormats()
    {
        Assert.True(WindowsDesktopContextBackend.ClipboardFormatsMatch(
            ["Bitmap", "System.Drawing.Bitmap"],
            ["System.Drawing.Bitmap"],
            restoredContainsImage: true));
        Assert.True(WindowsDesktopContextBackend.ClipboardFormatsMatch(
            ["UnicodeText"],
            ["UnicodeText", "Locale"],
            restoredContainsImage: false));
        Assert.False(WindowsDesktopContextBackend.ClipboardFormatsMatch(
            ["UnicodeText", "Velia.Custom"],
            ["UnicodeText"],
            restoredContainsImage: false));
    }

    [Fact]
    public async Task CapturesAContextBoundToTheCurrentOperation()
    {
        var backend = new FakeBackend(Observation(text: "已选文字"));
        var service = new DesktopContextService(backend);
        var operationId = Guid.NewGuid();

        var result = await service.CaptureAsync(
            new DesktopContextCaptureParams(operationId, DesktopContextModes.Selection),
            CancellationToken.None);

        var stored = service.Get(operationId, result.ContextId);
        Assert.NotNull(stored);
        Assert.Equal(operationId, stored.OperationId);
        Assert.Equal("已选文字", stored.Text);
    }

    [Fact]
    public async Task ReplacesAnOlderContextForTheSameOperation()
    {
        var backend = new FakeBackend(Observation(text: "第一次"), Observation(text: "第二次"));
        var service = new DesktopContextService(backend);
        var operationId = Guid.NewGuid();

        var first = await service.CaptureAsync(
            new DesktopContextCaptureParams(operationId, DesktopContextModes.Selection), CancellationToken.None);
        var second = await service.CaptureAsync(
            new DesktopContextCaptureParams(operationId, DesktopContextModes.Selection), CancellationToken.None);

        Assert.Null(service.Get(operationId, first.ContextId));
        Assert.Equal("第二次", service.Get(operationId, second.ContextId)?.Text);
    }

    [Fact]
    public async Task DoesNotExposeAContextToAnotherOperation()
    {
        var service = new DesktopContextService(new FakeBackend(Observation(text: "私有上下文")));
        var operationId = Guid.NewGuid();
        var result = await service.CaptureAsync(
            new DesktopContextCaptureParams(operationId, DesktopContextModes.Auto), CancellationToken.None);

        Assert.Null(service.Get(Guid.NewGuid(), result.ContextId));
    }

    [Fact]
    public async Task ReleaseRemovesTheBoundContext()
    {
        var service = new DesktopContextService(new FakeBackend(Observation(text: "待释放")));
        var operationId = Guid.NewGuid();
        var result = await service.CaptureAsync(
            new DesktopContextCaptureParams(operationId, DesktopContextModes.Auto), CancellationToken.None);

        var released = service.Release(operationId);

        Assert.True(released.Released);
        Assert.Null(service.Get(operationId, result.ContextId));
        Assert.False(service.Release(operationId).Released);
    }

    [Fact]
    public async Task RemovesExpiredContexts()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero));
        var service = new DesktopContextService(
            new FakeBackend(Observation(text: "短时上下文")),
            clock,
            TimeSpan.FromSeconds(30));
        var operationId = Guid.NewGuid();
        var result = await service.CaptureAsync(
            new DesktopContextCaptureParams(operationId, DesktopContextModes.Auto), CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.Null(service.Get(operationId, result.ContextId));
    }

    [Fact]
    public async Task ReturnsFileMetadataWithoutReturningThePrivatePath()
    {
        const string privatePath = @"C:\Users\customer\PrivateFolder\report.pdf";
        var service = new DesktopContextService(new FakeBackend(Observation(files: [privatePath])));

        var result = await service.CaptureAsync(
            new DesktopContextCaptureParams(Guid.NewGuid(), DesktopContextModes.Selection), CancellationToken.None);
        var serialized = JsonSerializer.Serialize(result);

        var file = Assert.Single(result.Files!);
        Assert.Equal("report.pdf", file.Name);
        Assert.Equal(".pdf", file.Extension);
        Assert.DoesNotContain("PrivateFolder", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(privatePath, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void MarksARealSelectedFolderWithoutReturningItsPath()
    {
        using var workspace = new TestFileWorkspace();
        var folder = workspace.CreateDirectory("目标文件夹");

        var result = DesktopContextService.ToFileResult(folder);
        var serialized = JsonSerializer.Serialize(result);

        Assert.True(result.IsDirectory);
        Assert.Equal("目标文件夹", result.Name);
        Assert.Equal(string.Empty, result.Extension);
        Assert.DoesNotContain(workspace.Root, serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsAnUnknownModeBeforeTouchingTheDesktop()
    {
        var backend = new FakeBackend(Observation(text: "不会读取"));
        var service = new DesktopContextService(backend);

        var exception = await Assert.ThrowsAsync<DesktopContextException>(() => service.CaptureAsync(
            new DesktopContextCaptureParams(Guid.NewGuid(), "shell"), CancellationToken.None));

        Assert.Equal("invalid_context_mode", exception.Code);
        Assert.Equal(0, backend.CaptureCount);
    }

    [Fact]
    public async Task ReplacesOnlyTheSelectionBoundToTheSameOperationAndContext()
    {
        var backend = new FakeBackend(Observation(text: "原文字"));
        var service = new DesktopContextService(backend);
        var operationId = Guid.NewGuid();
        var captured = await service.CaptureAsync(
            new DesktopContextCaptureParams(operationId, DesktopContextModes.Selection), CancellationToken.None);

        var result = await service.ReplaceSelectionAsync(
            new DesktopContextReplaceSelectionParams(operationId, captured.ContextId, "新文字"),
            CancellationToken.None);

        Assert.True(result.Replaced);
        Assert.True(result.Verified);
        Assert.True(result.ClipboardRestored);
        Assert.Equal("新文字", service.Get(operationId, captured.ContextId)?.Text);
        Assert.Equal((nint)123, backend.LastReplaceTarget?.WindowHandle);
        Assert.Equal((nint)124, backend.LastReplaceTarget?.ControlHandle);
        Assert.Equal("原文字", backend.LastReplaceTarget?.Text);
    }

    [Fact]
    public async Task RejectsReplacementForAStaleOrForeignContext()
    {
        var backend = new FakeBackend(Observation(text: "原文字"));
        var service = new DesktopContextService(backend);
        var operationId = Guid.NewGuid();
        var captured = await service.CaptureAsync(
            new DesktopContextCaptureParams(operationId, DesktopContextModes.Selection), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DesktopContextException>(() => service.ReplaceSelectionAsync(
            new DesktopContextReplaceSelectionParams(Guid.NewGuid(), captured.ContextId, "新文字"),
            CancellationToken.None));

        Assert.Equal("desktop_context_stale", exception.Code);
        Assert.Equal(0, backend.ReplaceCount);
    }

    [Fact]
    public async Task RejectsReplacementForASelectedFileContext()
    {
        var backend = new FakeBackend(Observation(files: [@"C:\Temp\report.txt"]));
        var service = new DesktopContextService(backend);
        var operationId = Guid.NewGuid();
        var captured = await service.CaptureAsync(
            new DesktopContextCaptureParams(operationId, DesktopContextModes.Selection), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DesktopContextException>(() => service.ReplaceSelectionAsync(
            new DesktopContextReplaceSelectionParams(operationId, captured.ContextId, "新文字"),
            CancellationToken.None));

        Assert.Equal("context_not_replaceable", exception.Code);
        Assert.Equal(0, backend.ReplaceCount);
    }

    [Fact]
    public async Task CopiesSelectedFilesIntoTheSelectedFolderAndVerifiesTheResult()
    {
        using var workspace = new TestFileWorkspace();
        var source = workspace.CreateFile("FILE-A-0816.txt", "copy-test");
        var destination = workspace.CreateDirectory("目标文件夹");
        var operation = new WindowsSelectedFileOperator();

        var result = await operation.ExecuteAsync(
            new SelectedFileOperationRequest(
                DesktopContextFileOperations.CopyToSelectedFolder,
                [source, destination],
                null),
            CancellationToken.None);

        Assert.True(File.Exists(source));
        var copied = Assert.Single(result.ResultPaths);
        Assert.Equal("copy-test", File.ReadAllText(copied));
        Assert.Equal(destination, result.DestinationFolderPath);
    }

    [Fact]
    public async Task MovesSelectedFilesWithoutLeavingTheSourceBehind()
    {
        using var workspace = new TestFileWorkspace();
        var source = workspace.CreateFile("FILE-B-0816.txt", "move-test");
        var destination = workspace.CreateDirectory("目标文件夹");
        var operation = new WindowsSelectedFileOperator();

        var result = await operation.ExecuteAsync(
            new SelectedFileOperationRequest(
                DesktopContextFileOperations.MoveToSelectedFolder,
                [source, destination],
                null),
            CancellationToken.None);

        Assert.False(File.Exists(source));
        Assert.Equal("move-test", File.ReadAllText(Assert.Single(result.ResultPaths)));
    }

    [Fact]
    public async Task RenamesOneSelectedFileAndPreservesItsExtension()
    {
        using var workspace = new TestFileWorkspace();
        var source = workspace.CreateFile("FILE-C-0816.txt", "rename-test");
        var operation = new WindowsSelectedFileOperator();

        var result = await operation.ExecuteAsync(
            new SelectedFileOperationRequest(
                DesktopContextFileOperations.RenameSelectedFile,
                [source],
                "RENAMED-0816"),
            CancellationToken.None);

        Assert.False(File.Exists(source));
        var renamed = Assert.Single(result.ResultPaths);
        Assert.Equal("RENAMED-0816.txt", Path.GetFileName(renamed));
        Assert.Equal("rename-test", File.ReadAllText(renamed));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("C O N")]
    [InlineData("C　O　N")]
    [InlineData("COM 1")]
    [InlineData("L P T 9")]
    public async Task RejectsWindowsReservedNamesIncludingSpeechSpelling(string requestedName)
    {
        using var workspace = new TestFileWorkspace();
        var source = workspace.CreateFile("SAFE-TEST.txt", "reserved-name-test");
        var operation = new WindowsSelectedFileOperator();

        var exception = await Assert.ThrowsAsync<DesktopContextException>(() => operation.ExecuteAsync(
            new SelectedFileOperationRequest(
                DesktopContextFileOperations.RenameSelectedFile,
                [source],
                requestedName),
            CancellationToken.None));

        Assert.Equal("invalid_file_name", exception.Code);
        Assert.True(File.Exists(source));
        Assert.Equal("reserved-name-test", File.ReadAllText(source));
    }

    [Fact]
    public async Task NeverOverwritesAnExistingDestinationFile()
    {
        using var workspace = new TestFileWorkspace();
        var source = workspace.CreateFile("FILE-D-0816.txt", "source");
        var destination = workspace.CreateDirectory("目标文件夹");
        File.WriteAllText(Path.Combine(destination, "FILE-D-0816.txt"), "existing");
        var operation = new WindowsSelectedFileOperator();

        var exception = await Assert.ThrowsAsync<DesktopContextException>(() => operation.ExecuteAsync(
            new SelectedFileOperationRequest(
                DesktopContextFileOperations.CopyToSelectedFolder,
                [source, destination],
                null),
            CancellationToken.None));

        Assert.Equal("destination_conflict", exception.Code);
        Assert.Equal("source", File.ReadAllText(source));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(destination, "FILE-D-0816.txt")));
    }

    [Fact]
    public async Task ConsumesAFileContextAfterOneMutation()
    {
        var backend = new FakeBackend(Observation(files: [@"C:\Users\customer\Documents\FILE.txt", @"C:\Users\customer\Documents\目标"]));
        var fileOperator = new FakeFileOperator();
        var service = new DesktopContextService(backend, selectedFileOperator: fileOperator);
        var operationId = Guid.NewGuid();
        var captured = await service.CaptureAsync(
            new DesktopContextCaptureParams(operationId, DesktopContextModes.Selection), CancellationToken.None);
        var parameters = new DesktopContextFileOperationParams(
            operationId,
            captured.ContextId,
            DesktopContextFileOperations.MoveToSelectedFolder);

        var result = await service.OperateSelectedFilesAsync(parameters, CancellationToken.None);
        var replay = await Assert.ThrowsAsync<DesktopContextException>(() =>
            service.OperateSelectedFilesAsync(parameters, CancellationToken.None));
        var serialized = JsonSerializer.Serialize(result);

        Assert.True(result.Verified);
        Assert.Equal(1, fileOperator.CallCount);
        Assert.Equal("desktop_context_stale", replay.Code);
        Assert.DoesNotContain(@"C:\Users\customer", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsAnInvalidFileActionWithoutConsumingTheContext()
    {
        var backend = new FakeBackend(Observation(files: [@"C:\Users\customer\Documents\FILE.txt"]));
        var service = new DesktopContextService(backend, selectedFileOperator: new FakeFileOperator());
        var operationId = Guid.NewGuid();
        var captured = await service.CaptureAsync(
            new DesktopContextCaptureParams(operationId, DesktopContextModes.Selection), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DesktopContextException>(() => service.OperateSelectedFilesAsync(
            new DesktopContextFileOperationParams(operationId, captured.ContextId, "deleteSelectedFiles"),
            CancellationToken.None));

        Assert.Equal("invalid_file_operation", exception.Code);
        Assert.NotNull(service.Get(operationId, captured.ContextId));
    }

    [Fact]
    public async Task ResolvesExactlyOneSupportedSelectedDocumentOnlyForTheTrustedHost()
    {
        using var workspace = new TestFileWorkspace();
        var path = workspace.CreateFile("VOICE-DOCUMENT.txt", "selected document");
        var service = new DesktopContextService(new FakeBackend(Observation(files: [path])));
        var operationId = Guid.NewGuid();
        var captured = await service.CaptureAsync(
            new DesktopContextCaptureParams(operationId, DesktopContextModes.Selection), CancellationToken.None);

        var result = service.ResolveSelectedDocument(
            new DesktopContextResolveSelectedDocumentParams(operationId, captured.ContextId));

        Assert.Equal("VOICE-DOCUMENT.txt", result.Name);
        Assert.Equal(".txt", result.Extension);
        Assert.Equal(new FileInfo(path).Length, result.SizeBytes);
        Assert.Equal(path, result.InternalPath);
    }

    [Fact]
    public async Task RejectsMultipleFilesAndUnsupportedDocumentTypes()
    {
        using var workspace = new TestFileWorkspace();
        var first = workspace.CreateFile("FIRST.txt", "one");
        var second = workspace.CreateFile("SECOND.txt", "two");
        var multiService = new DesktopContextService(new FakeBackend(Observation(files: [first, second])));
        var multiOperation = Guid.NewGuid();
        var multi = await multiService.CaptureAsync(
            new DesktopContextCaptureParams(multiOperation, DesktopContextModes.Selection), CancellationToken.None);

        var multipleError = Assert.Throws<DesktopContextException>(() => multiService.ResolveSelectedDocument(
            new DesktopContextResolveSelectedDocumentParams(multiOperation, multi.ContextId)));

        var binary = workspace.CreateFile("PROGRAM.exe", "not executable in this test");
        var binaryService = new DesktopContextService(new FakeBackend(Observation(files: [binary])));
        var binaryOperation = Guid.NewGuid();
        var binaryContext = await binaryService.CaptureAsync(
            new DesktopContextCaptureParams(binaryOperation, DesktopContextModes.Selection), CancellationToken.None);
        var typeError = Assert.Throws<DesktopContextException>(() => binaryService.ResolveSelectedDocument(
            new DesktopContextResolveSelectedDocumentParams(binaryOperation, binaryContext.ContextId)));

        Assert.Equal("document_selection_invalid", multipleError.Code);
        Assert.Equal("document_type_not_supported", typeError.Code);
    }

    private static DesktopContextObservation Observation(
        string? text = null,
        IReadOnlyList<string>? files = null) =>
        new(
            files is { Count: > 0 } ? DesktopContextKinds.SelectedFiles : DesktopContextKinds.SelectedText,
            new DesktopContextSourceResult("notepad", "测试窗口"),
            true,
            (nint)123,
            (nint)124,
            456,
            text,
            files);

    private sealed class FakeBackend(params DesktopContextObservation[] observations) : IDesktopContextBackend
    {
        private readonly Queue<DesktopContextObservation> pending = new(observations);
        public int CaptureCount { get; private set; }
        public int ReplaceCount { get; private set; }
        public DesktopContextTarget? LastReplaceTarget { get; private set; }

        public Task<DesktopContextObservation> CaptureAsync(string mode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            return Task.FromResult(pending.Dequeue());
        }

        public Task<DesktopContextReplaceObservation> ReplaceSelectionAsync(
            DesktopContextTarget target,
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceCount++;
            LastReplaceTarget = target;
            return Task.FromResult(new DesktopContextReplaceObservation(
                new DesktopContextSourceResult("notepad", "测试窗口"),
                true));
        }
    }

    private sealed class FakeFileOperator : ISelectedFileOperator
    {
        public int CallCount { get; private set; }

        public Task<SelectedFileOperationObservation> ExecuteAsync(
            SelectedFileOperationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new SelectedFileOperationObservation(
                [@"C:\Users\customer\Documents\目标\FILE.txt"],
                @"C:\Users\customer\Documents\目标"));
        }
    }

    private sealed class TestFileWorkspace : IDisposable
    {
        public TestFileWorkspace()
        {
            Root = Path.Combine(AppContext.BaseDirectory, "file-operation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateFile(string name, string contents)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, contents);
            return path;
        }

        public string CreateDirectory(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}
