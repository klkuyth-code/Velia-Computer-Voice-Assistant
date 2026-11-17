using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Velia.Protocol;
using Velia.Core.Documents;

namespace Velia.Core.DesktopVisual;

internal sealed class WindowsDesktopAutomationService : IDeterministicDesktopActionService, IDesktopTextEditorService
{
    private static readonly TimeSpan TargetBindingLifetime = TimeSpan.FromMinutes(2);
    private const int MaximumTypedCharacters = 1_024;
    private const int MinimumCoordinate = -32_768;
    private const int MaximumCoordinate = 32_767;
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseWheel = 0x0800;
    private const uint KeyboardUnicode = 0x0004;
    private const uint KeyboardKeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyA = 0x41;
    private const ushort VirtualKeyC = 0x43;
    private const ushort VirtualKeyS = 0x53;
    private const ushort VirtualKeyMenu = 0x12;
    private const ushort VirtualKeyF4 = 0x73;
    private const ushort VirtualKeyEnd = 0x23;
    private const uint EditSetSelection = 0x00B1;
    private const uint EditReplaceSelection = 0x00C2;
    private const int MaximumVerifiedAppendCharacters = 512;
    private const int MaximumVerificationTailCharacters = 4_096;
    private const int MaximumValuePatternDocumentCharacters = 131_072;
    private static readonly TimeSpan PendingTextSubmissionLifetime = TimeSpan.FromMinutes(2);
    private const int WheelDelta = 120;
    private static readonly (string Key, string[] Aliases)[] ControlSemanticAliases =
    [
        ("search", ["搜索", "查找", "search", "find"]),
        ("close", ["关闭", "close", "dismiss"]),
        ("minimize", ["最小化", "minimize", "minimise"]),
        ("maximize", ["最大化", "maximize", "maximise"]),
        ("restore", ["还原", "恢复窗口", "restore"]),
        ("back", ["返回", "后退", "back", "previous"]),
        ("forward", ["前进", "forward", "next"]),
        ("refresh", ["刷新", "重新加载", "refresh", "reload"]),
        ("more", ["更多", "more", "overflow"]),
        ("settings", ["设置", "settings", "preferences"]),
        ("home", ["首页", "主页", "home"]),
        ("add", ["添加", "新增", "新建", "add", "create", "plus"]),
        ("save", ["保存", "save"]),
        ("open", ["打开", "open"]),
        ("download", ["下载", "download"]),
        ("upload", ["上传", "upload"]),
        ("folder", ["文件夹", "folder", "directory"]),
        ("confirm", ["确定", "确认", "ok", "confirm"]),
        ("cancel", ["取消", "cancel"]),
    ];
    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi",
    };
    private readonly object gate = new();
    private readonly Dictionary<Guid, TargetBinding> targetBindings = [];
    private PendingTextSubmission? pendingTextSubmission;

    public ActiveInteractionSurfaceKind GetActiveSurfaceKind()
    {
        lock (gate)
        {
            try
            {
                var foreground = ReadForegroundForBinding();
                return BrowserProcessNames.Contains(foreground.ProcessName)
                    ? ActiveInteractionSurfaceKind.Browser
                    : ActiveInteractionSurfaceKind.Desktop;
            }
            catch
            {
                return ActiveInteractionSurfaceKind.Unknown;
            }
        }
    }

    public void ObserveVerifiedVisualTextEntry(DeterministicDesktopActionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Action != DeterministicDesktopActionTypes.TypeText || string.IsNullOrWhiteSpace(command.Text))
            throw new DesktopVisualException("invalid_desktop_visual_input", "视觉文字输入凭证无效。" );
        lock (gate)
        {
            var foreground = ReadForeground();
            EnsureSafeDesktop(foreground);
            EnsureExpectedMessagingApplication(command, foreground);
            pendingTextSubmission = IsMessagingProcess(foreground.ProcessName) &&
                !LooksLikeSearchInputTarget(command.Target)
                ? new PendingTextSubmission(
                    foreground.Handle,
                    foreground.ProcessId,
                    foreground.ProcessName,
                    foreground.WindowTitle,
                    command.Target,
                    command.Text,
                    string.Empty,
                    DateTimeOffset.UtcNow.Add(PendingTextSubmissionLifetime),
                    VisualOnly: true)
                : null;
        }
    }

    public void ConsumeVisualTextSubmissionAuthorization(DeterministicDesktopActionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Action != DeterministicDesktopActionTypes.SubmitText)
            throw new DesktopVisualException("desktop_message_draft_missing", "当前没有可以发送的消息草稿。" );
        lock (gate)
        {
            var foreground = ReadForeground();
            EnsureSafeDesktop(foreground);
            var binding = GetValidatedPendingSubmission(foreground);
            if (!binding.VisualOnly)
                throw new DesktopVisualException(
                    "desktop_message_submit_unverified",
                    "当前消息草稿不需要视觉发送通道。" );
            // Consume before the visual click begins. If visual verification
            // later fails after a real click, a retry cannot send twice.
            pendingTextSubmission = null;
        }
    }

    public DesktopVisualGetForegroundResult GetForeground(Guid operationId)
    {
        lock (gate)
        {
            if (operationId == Guid.Empty)
                throw new DesktopVisualException("invalid_desktop_visual_target", "桌面视觉任务标识无效。");
            var source = ReadForegroundForBinding();
            EnsureSafeDesktop(source);
            var now = DateTimeOffset.UtcNow;
            PruneTargetBindings(now);
            var targetToken = Guid.NewGuid();
            targetBindings[targetToken] = new TargetBinding(operationId, source, now.Add(TargetBindingLifetime));
            var center = ReadWindowCenter(source.Handle);
            return new DesktopVisualGetForegroundResult(
                targetToken,
                source.ProcessId,
                source.ProcessName,
                source.WindowTitle,
                center.X,
                center.Y,
                now);
        }
    }

    public DesktopVisualExecuteActionResult Execute(DesktopVisualExecuteActionParams parameters)
    {
        lock (gate) return ExecuteLocked(parameters);
    }

    public Task<DeterministicDesktopActionResult> ExecuteAsync(
        DeterministicDesktopActionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        DeterministicDesktopActionResult result;
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = ExecuteDeterministicLocked(command, cancellationToken);
        }
        return Task.FromResult(result);
    }

    public Task<DesktopTextEditorResult> ExecuteTextDocumentAsync(
        DesktopTextDocumentEditCommand command,
        string expectedFileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var before = ReadForeground();
            EnsureSafeDesktop(before);
            var isUntitledNotepad = string.Equals(
                expectedFileName,
                DesktopTextDocumentBindings.UntitledNotepad,
                StringComparison.Ordinal);
            var stem = Path.GetFileNameWithoutExtension(expectedFileName);
            if (isUntitledNotepad
                ? !before.ProcessName.Equals("notepad", StringComparison.OrdinalIgnoreCase) ||
                  !IsUntitledNotepadTitle(before.WindowTitle)
                : !NormalizeControlText(before.WindowTitle).Contains(
                    NormalizeControlText(stem), StringComparison.Ordinal))
                throw new DesktopVisualException("document_window_mismatch", $"当前窗口不是“{expectedFileName}”。", true);
            if (isUntitledNotepad && command.Action is DesktopTextDocumentEditActions.Save or DesktopTextDocumentEditActions.Close)
                throw new DesktopVisualException(
                    "untitled_notepad_save_name_required",
                    "当前记事本尚未命名；请先创建一个有名称的桌面记事本文档后再保存或关闭。",
                    false);
            var editor = ResolveEditableSurface(before.Handle);
            if (!TryReadEditableText(editor, out var content))
                throw new DesktopVisualException("document_read_failed", "Windows 无法读取当前文档内容。", true);

            string next;
            if (command.Action == DesktopTextDocumentEditActions.DeleteSelection)
            {
                if (!TryReadSelectedText(editor, out var selectedText) || selectedText.Length == 0)
                    throw new DesktopVisualException("document_selection_missing", "当前文档没有可删除的选中文字。", true);
                editor.SetFocus();
                SendInputs(KeyPress(DesktopVisualKeys.Backspace));
                if (!WaitForEditableTextChange(editor, content, out next) || next.Length >= content.Length)
                    throw new DesktopVisualException("document_edit_unverified", "选中文字删除没有通过本机回读验证。", true);
            }
            else
            {
                next = command.Action switch
                {
                    DesktopTextDocumentEditActions.Append => AppendDocumentText(content, command.Text!),
                    DesktopTextDocumentEditActions.SetLine => DesktopTextDocumentLineEditor.SetLine(content, command.LineNumber!.Value, command.Text!),
                    DesktopTextDocumentEditActions.InsertLine => DesktopTextDocumentLineEditor.InsertLine(content, command.LineNumber!.Value, command.Text!),
                    DesktopTextDocumentEditActions.DeleteLine => DesktopTextDocumentLineEditor.DeleteLine(content, command.LineNumber!.Value),
                    DesktopTextDocumentEditActions.Replace => ReplaceDocumentText(content, command.Match!, command.Text!),
                    DesktopTextDocumentEditActions.Delete => ReplaceDocumentText(content, command.Match!, string.Empty),
                    DesktopTextDocumentEditActions.Clear => string.Empty,
                    DesktopTextDocumentEditActions.Clean => DocumentContentCleaner.CleanText(content),
                    DesktopTextDocumentEditActions.Save or DesktopTextDocumentEditActions.Close => content,
                    _ => throw new DesktopVisualException("document_edit_not_allowed", "文档操作不在本地白名单中。"),
                };
                if (!StringComparer.Ordinal.Equals(next, content) &&
                    (!TrySetEditableText(editor, before.ProcessId, next) ||
                     !WaitForExpectedEditableText(editor, next)))
                    throw new DesktopVisualException("document_edit_unverified", "文档修改没有通过本机回读验证。", true);
            }

            if (!isUntitledNotepad)
            {
                SendInputs(ModifiedKeyPress(VirtualKeyControl, VirtualKeyS));
                Thread.Sleep(100);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var closed = false;
            if (command.Action == DesktopTextDocumentEditActions.Close)
            {
                var closeBaselineTitle = ReadNativeWindowTitle(before.Handle).Normalize(NormalizationForm.FormKC);
                SendInputs(ModifiedKeyPress(VirtualKeyMenu, VirtualKeyF4));
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    Thread.Sleep(50);
                    var currentTitle = ReadNativeWindowTitle(before.Handle).Normalize(NormalizationForm.FormKC);
                    if (!IsWindowVisible(before.Handle) ||
                        !StringComparer.Ordinal.Equals(closeBaselineTitle, currentTitle))
                    {
                        closed = true;
                        break;
                    }
                }
                if (!closed)
                    throw new DesktopVisualException("document_close_unverified", "Windows 没有确认文档窗口已经关闭。", true);
            }
            return Task.FromResult(new DesktopTextEditorResult(
                true,
                command.Action,
                next,
                before.WindowTitle,
                closed,
                Persisted: !isUntitledNotepad));
        }
    }

    public Task<string?> TryGetCurrentTextDocumentFileNameAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var source = ReadForegroundForBinding("notepad");
            if (!source.ProcessName.Equals("notepad", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<string?>(null);
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var normalizedTitle = NormalizeControlText(source.WindowTitle);
            var fileName = Directory.EnumerateFiles(desktop, "*.txt", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .OrderByDescending(static name => name!.Length)
                .FirstOrDefault(name =>
                    normalizedTitle.Contains(NormalizeControlText(name!), StringComparison.Ordinal) ||
                    normalizedTitle.Contains(
                        NormalizeControlText(Path.GetFileNameWithoutExtension(name!)),
                        StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(fileName)) return Task.FromResult<string?>(fileName);
            return Task.FromResult<string?>(IsUntitledNotepadTitle(source.WindowTitle)
                ? DesktopTextDocumentBindings.UntitledNotepad
                : null);
        }
    }

    internal static bool IsUntitledNotepadTitle(string title)
    {
        var normalized = NormalizeControlText(title);
        return normalized.Contains("无标题", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("未命名", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("untitled", StringComparison.OrdinalIgnoreCase);
    }

    private DeterministicDesktopActionResult ExecuteDeterministicLocked(
        DeterministicDesktopActionCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Target) || command.Target.Length > 48)
            throw new DesktopVisualException("invalid_desktop_visual_target", "本地桌面动作目标无效。");
        // Velia's main window can still own the foreground immediately after
        // it launches a document. Resolve the real user surface behind Velia
        // and activate it before applying a deterministic menu or editor
        // action. A genuinely foreground third-party app remains authoritative.
        var before = ReadForegroundForBinding();
        EnsureSafeDesktop(before);

        if (command.Action == DeterministicDesktopActionTypes.TypeText)
            return EnterVerifiedText(command, before, cancellationToken);

        if (command.Action == DeterministicDesktopActionTypes.SubmitText)
            return SubmitVerifiedText(command, before, cancellationToken);

        if (command.Action == DeterministicDesktopActionTypes.SelectAll)
        {
            var editor = ResolveEditableSurface(before.Handle);
            try
            {
                editor.SetFocus();
                SendInputs(ModifiedKeyPress(VirtualKeyControl, VirtualKeyA));
            }
            catch (Exception exception) when (
                exception is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                throw new DesktopVisualException(
                    "desktop_visual_select_all_failed",
                    "Windows 未能全选当前文档内容。",
                    true,
                    exception);
            }
            var verified = false;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                Thread.Sleep(40);
                if (!HasNonEmptyTextSelection(editor)) continue;
                verified = true;
                break;
            }
            if (!verified)
                throw new DesktopVisualException(
                    "desktop_visual_select_all_unverified",
                    "Windows 没有确认当前文档内容已经全选。",
                    true);
            var afterSelection = ReadForeground();
            EnsureSafeDesktop(afterSelection);
            EnsureSameProcessAfterAction(before, afterSelection);
            return new DeterministicDesktopActionResult(
                command.Action,
                true,
                "uiaTextSelected",
                command.Target,
                afterSelection.WindowTitle);
        }

        if (command.Action == DeterministicDesktopActionTypes.CopySelection)
        {
            var clipboardSequence = GetClipboardSequenceNumber();
            // Ctrl+C is the native deterministic copy command. Looking for a
            // closed application's Copy menu through the desktop-wide UIA tree
            // can block for several seconds and adds no verification value.
            SendInputs(ModifiedKeyPress(VirtualKeyControl, VirtualKeyC));
            var changed = WaitForClipboardChange(clipboardSequence, 600, cancellationToken);
            if (!changed)
                throw new DesktopVisualException(
                    "desktop_visual_copy_unverified",
                    "Windows 没有确认当前选中内容已复制。",
                    true);
            var afterCopy = ReadForeground();
            EnsureSafeDesktop(afterCopy);
            EnsureSameProcessAfterAction(before, afterCopy);
            return new DeterministicDesktopActionResult(
                command.Action,
                true,
                "clipboardUpdated",
                command.Target,
                afterCopy.WindowTitle);
        }

        if (command.Action != DeterministicDesktopActionTypes.ClickControl)
            throw new DesktopVisualException("desktop_visual_action_not_allowed", "本地桌面动作不在安全白名单中。");

        // Any intervening click can change a recipient or form. A later
        // “发送” must never reuse a draft binding across that state change.
        pendingTextSubmission = null;

        var center = ReadWindowCenter(before.Handle);
        var parameters = new DesktopVisualExecuteActionParams(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DesktopVisualActions.Click,
            command.Target,
            "local-uia",
            center.X,
            center.Y,
            null,
            null,
            null,
            before.ProcessId,
            before.ProcessName,
            before.WindowTitle,
            true);
        ResolvedActionTarget target;
        try
        {
            target = ResolveActionTarget(parameters, before.Handle);
        }
        catch (DesktopVisualException exception) when (
            exception.Code == "desktop_visual_target_not_found" &&
            IsMenuTarget(command.Target))
        {
            // A previously expanded native menu can temporarily hide the rest
            // of the menu bar from UI Automation. Close only that transient
            // popup, then resolve the newly requested menu against the same
            // already-bound application window.
            SendInputs(KeyPress(DesktopVisualKeys.Escape));
            Thread.Sleep(70);
            var stillForeground = ReadForeground();
            EnsureSafeDesktop(stillForeground);
            EnsureSameProcessAfterAction(before, stillForeground);
            target = ResolveActionTarget(parameters, before.Handle);
        }
        if (target.AutomationElement is null)
            throw new DesktopVisualException("desktop_visual_target_not_found", "未能找到唯一可访问的目标控件。", true);
        if (!SetCursorPos(target.Point.X, target.Point.Y))
            throw NativeFailure("desktop_visual_cursor_failed", "无法定位到目标屏幕位置。", true);
        var verification = ActivateAutomationTarget(target.AutomationElement);
        var after = ReadForeground();
        EnsureSafeDesktop(after);
        // The bound UIA element proves which control received the action.
        // Opening a child tool, document or external view can legitimately
        // move foreground ownership after Invoke returns; that must not turn a
        // real completed click into a false failure. The consumed binding still
        // prevents any follow-up action from targeting the new window silently.
        return new DeterministicDesktopActionResult(
            command.Action,
            true,
            verification,
            command.Target,
            after.WindowTitle);
    }

    private DeterministicDesktopActionResult EnterVerifiedText(
        DeterministicDesktopActionCommand command,
        ForegroundSource before,
        CancellationToken cancellationToken)
    {
        var text = command.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaximumTypedCharacters ||
            text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new DesktopVisualException("invalid_desktop_visual_input", "输入文字必须为 1 到 1024 个有效字符。");

        EnsureExpectedMessagingApplication(command, before);

        var knownMessagingResult = TryEnterKnownMessagingText(command, before, text);
        if (knownMessagingResult is not null) return knownMessagingResult;

        var editor = ResolveInputSurface(before.Handle, command.Target);
        if (!TryReadEditableText(editor, out var previous))
            throw new DesktopVisualException(
                "desktop_text_input_readback_unavailable",
                "Windows 无法回读目标输入框，已停止输入。",
                true);
        if (previous.Length + text.Length > MaximumValuePatternDocumentCharacters)
            throw new DesktopVisualException("desktop_text_input_too_large", "目标输入框内容过长，已停止输入。" );

        try
        {
            editor.SetFocus();
            if (!TryAppendInputText(editor, previous, text))
            {
                SendInputs(KeyPressVirtualKey(VirtualKeyEnd));
                SendInputs(UnicodeText(text));
            }
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            throw new DesktopVisualException(
                "desktop_text_input_failed",
                "Windows 未能向目标输入框写入文字。",
                true,
                exception);
        }

        if (!WaitForEnteredText(editor, previous, text, cancellationToken, out var verifiedText))
            throw new DesktopVisualException(
                "desktop_text_input_unverified",
                "输入框没有回读到刚才写入的文字。",
                true);

        var after = ReadForeground();
        EnsureSafeDesktop(after);
        EnsureSameProcessAfterAction(before, after);
        var canSubmit = IsMessagingProcess(before.ProcessName) &&
            !LooksLikeSearchInputTarget(command.Target);
        pendingTextSubmission = canSubmit
            ? new PendingTextSubmission(
                before.Handle,
                before.ProcessId,
                before.ProcessName,
                before.WindowTitle,
                command.Target,
                text,
                verifiedText,
                DateTimeOffset.UtcNow.Add(PendingTextSubmissionLifetime),
                VisualOnly: false)
            : null;
        return new DeterministicDesktopActionResult(
            command.Action,
            true,
            canSubmit ? "messageDraftVerified" : "textInputVerified",
            command.Target,
            after.WindowTitle);
    }

    private DeterministicDesktopActionResult SubmitVerifiedText(
        DeterministicDesktopActionCommand command,
        ForegroundSource before,
        CancellationToken cancellationToken)
    {
        var binding = GetValidatedPendingSubmission(before);
        if (binding.VisualOnly)
        {
            var knownMessagingResult = TrySubmitKnownMessagingText(command, before, binding);
            if (knownMessagingResult is not null) return knownMessagingResult;
            throw new DesktopVisualException(
                "desktop_message_submit_requires_visual",
                "当前消息草稿需要通过已绑定画面定位发送按钮。",
                true);
        }

        var editor = ResolveInputSurface(before.Handle, binding.Target);
        if (!TryReadEditableText(editor, out var current) ||
            !ContainsStagedText(current, binding.Text))
        {
            pendingTextSubmission = null;
            throw new DesktopVisualException(
                "desktop_message_draft_changed",
                "消息草稿已发生变化，请重新输入并确认后再发送。" );
        }

        var submitGestureStarted = false;
        try
        {
            var center = ReadWindowCenter(before.Handle);
            var parameters = new DesktopVisualExecuteActionParams(
                Guid.NewGuid(),
                Guid.NewGuid(),
                DesktopVisualActions.Click,
                "发送按钮",
                "local-uia",
                center.X,
                center.Y,
                null,
                null,
                null,
                before.ProcessId,
                before.ProcessName,
                before.WindowTitle,
                true);
            try
            {
                var sendTarget = ResolveActionTarget(parameters, before.Handle);
                if (sendTarget.AutomationElement is not null)
                {
                    submitGestureStarted = true;
                    _ = ActivateAutomationTarget(sendTarget.AutomationElement);
                }
            }
            catch (DesktopVisualException exception) when (
                exception.Code == "desktop_visual_target_not_found")
            {
                // Some messaging clients expose the editor but not the Send
                // button. Enter is attempted only for the already-bound draft;
                // if it inserts a newline instead, readback below reports a
                // failure and the draft will never be submitted twice.
            }
            if (!submitGestureStarted)
            {
                editor.SetFocus();
                submitGestureStarted = true;
                SendInputs(KeyPress(DesktopVisualKeys.Enter));
            }
        }
        finally
        {
            // Once a submit gesture has occurred, never retain a reusable token:
            // the message may have been sent even if UIA readback becomes stale.
            if (submitGestureStarted) pendingTextSubmission = null;
        }

        if (!WaitForSubmittedText(editor, binding.Text, cancellationToken))
            throw new DesktopVisualException(
                "desktop_message_submit_unverified",
                "已执行发送操作，但消息输入框没有确认草稿已经提交，请勿重复发送。",
                true);

        var after = ReadForeground();
        EnsureSafeDesktop(after);
        EnsureSameProcessAfterAction(before, after);
        return new DeterministicDesktopActionResult(
            command.Action,
            true,
            "messageSubmittedVerified",
            command.Target,
            after.WindowTitle);
    }

    private DeterministicDesktopActionResult? TryEnterKnownMessagingText(
        DeterministicDesktopActionCommand command,
        ForegroundSource before,
        string text)
    {
        if (!LooksLikeMessageInputTarget(command.Target) ||
            text.Any(character => character is '\r' or '\n' or '\t') ||
            !TryReadWindowRectangle(before.Handle, out var rectangle) ||
            ResolveKnownMessagingPoint(
                before.ProcessName,
                command.Target,
                rectangle.Left,
                rectangle.Top,
                rectangle.Right,
                rectangle.Bottom) is not { } point)
            return null;

        EnsureKnownMessagingWindowInFront(before);
        MoveCursorAndVerify(point);
        SendInputs(MouseClick());
        Thread.Sleep(35);
        var focused = ReadForeground();
        EnsureSafeDesktop(focused);
        EnsureSameProcessAfterAction(before, focused);
        SendInputs(UnicodeText(text));
        var after = ReadForeground();
        EnsureSafeDesktop(after);
        EnsureSameProcessAfterAction(before, after);
        pendingTextSubmission = new PendingTextSubmission(
            before.Handle,
            before.ProcessId,
            before.ProcessName,
            before.WindowTitle,
            command.Target,
            text,
            string.Empty,
            DateTimeOffset.UtcNow.Add(PendingTextSubmissionLifetime),
            VisualOnly: true);
        return new DeterministicDesktopActionResult(
            command.Action,
            true,
            "messageDraftVerified",
            command.Target,
            after.WindowTitle);
    }

    private DeterministicDesktopActionResult? TrySubmitKnownMessagingText(
        DeterministicDesktopActionCommand command,
        ForegroundSource before,
        PendingTextSubmission binding)
    {
        if (!TryReadWindowRectangle(before.Handle, out var rectangle) ||
            ResolveKnownMessagingPoint(
                before.ProcessName,
                "发送按钮",
                rectangle.Left,
                rectangle.Top,
                rectangle.Right,
                rectangle.Bottom) is not { } point)
            return null;

        EnsureKnownMessagingWindowInFront(before);
        MoveCursorAndVerify(point);
        EnsurePointBelongsToProcess(point, before.ProcessId);
        Thread.Sleep(80);
        var beforeMutation = TryCaptureMessagingMutation(rectangle);
        if (beforeMutation is null) return null;
        // Consume immediately before the irreversible click. A failed return or
        // stale screenshot can never make the same draft send twice.
        pendingTextSubmission = null;
        SendInputs(MouseButtonDown());
        Thread.Sleep(35);
        SendInputs(MouseButtonUp());
        Thread.Sleep(220);
        var after = ReadForeground();
        EnsureSafeDesktop(after);
        EnsureSameProcessAfterAction(before, after);
        var afterMutation = TryCaptureMessagingMutation(rectangle);
        if (afterMutation is null || !HasMeaningfulPixelChange(beforeMutation, afterMutation))
            throw new DesktopVisualException(
                "desktop_message_submit_unverified",
                "已点击微信发送按钮，但聊天区域没有确认消息已经发出，请勿重复发送。",
                true);
        return new DeterministicDesktopActionResult(
            command.Action,
            true,
            "messageSubmittedVerified",
            binding.Target,
            after.WindowTitle);
    }

    private static void EnsureKnownMessagingWindowInFront(ForegroundSource expected)
    {
        if (!IsWindowVisible(expected.Handle) || IsIconic(expected.Handle))
            throw new DesktopVisualException(
                "desktop_message_window_unavailable",
                "微信窗口当前不可见或已最小化，请恢复窗口后重试。",
                true);
        _ = BringWindowToTop(expected.Handle);
        _ = SetForegroundWindow(expected.Handle);
        Thread.Sleep(70);
        var actual = ReadForeground();
        if (actual.Handle != expected.Handle)
            throw new DesktopVisualException(
                "desktop_message_window_not_foreground",
                "微信窗口没有成功切到前台，已停止操作。",
                true);
        EnsureSameProcessAfterAction(expected, actual);
    }

    private static void EnsurePointBelongsToProcess(MessagingLayoutPoint point, int expectedProcessId)
    {
        var targetWindow = WindowFromPoint(new POINT(point.X, point.Y));
        if (targetWindow == 0)
            throw new DesktopVisualException(
                "desktop_message_target_not_found",
                "发送按钮位置没有命中任何窗口。",
                true);
        _ = GetWindowThreadProcessId(targetWindow, out var processId);
        if (processId != checked((uint)expectedProcessId))
            throw new DesktopVisualException(
                "desktop_message_target_occluded",
                "发送按钮被其他窗口遮挡，已停止操作。",
                true);
    }

    private static MessagingMutationSample? TryCaptureMessagingMutation(RECT rectangle)
    {
        try
        {
            var windowWidth = rectangle.Right - rectangle.Left;
            var windowHeight = rectangle.Bottom - rectangle.Top;
            var left = rectangle.Left + checked((int)Math.Round(windowWidth * 0.33));
            var top = rectangle.Top + checked((int)Math.Round(windowHeight * 0.54));
            var width = rectangle.Right - left;
            var height = rectangle.Bottom - top;
            if (width < 200 || height < 160) return null;
            using var bitmap = new System.Drawing.Bitmap(
                width,
                height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    left,
                    top,
                    0,
                    0,
                    new System.Drawing.Size(width, height),
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }
            var data = bitmap.LockBits(
                new System.Drawing.Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var rowBytes = checked(width * 4);
                var pixels = new byte[checked(rowBytes * height)];
                for (var row = 0; row < height; row++)
                {
                    Marshal.Copy(
                        data.Scan0 + (row * data.Stride),
                        pixels,
                        row * rowBytes,
                        rowBytes);
                }
                return new MessagingMutationSample(width, height, pixels);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or ExternalException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static bool HasMeaningfulPixelChange(
        MessagingMutationSample before,
        MessagingMutationSample after)
    {
        if (before.Width != after.Width || before.Height != after.Height ||
            before.Pixels.Length != after.Pixels.Length || before.Pixels.Length % 4 != 0)
            return false;
        var changedPixels = 0;
        var requiredPixels = Math.Max(180, (before.Width * before.Height) / 2_500);
        for (var index = 0; index < before.Pixels.Length; index += 4)
        {
            var difference =
                Math.Abs(before.Pixels[index] - after.Pixels[index]) +
                Math.Abs(before.Pixels[index + 1] - after.Pixels[index + 1]) +
                Math.Abs(before.Pixels[index + 2] - after.Pixels[index + 2]);
            if (difference < 48) continue;
            changedPixels++;
            if (changedPixels >= requiredPixels) return true;
        }
        return false;
    }

    private static void MoveCursorAndVerify(MessagingLayoutPoint point)
    {
        if (!SetCursorPos(point.X, point.Y))
            throw NativeFailure("desktop_visual_cursor_failed", "无法定位到通信软件目标区域。", true);
        if (!GetCursorPos(out var cursor) || Math.Abs(cursor.X - point.X) > 2 || Math.Abs(cursor.Y - point.Y) > 2)
            throw new DesktopVisualException(
                "desktop_visual_cursor_verification_failed",
                "通信软件目标位置没有通过光标验证。",
                true);
    }

    internal static MessagingLayoutPoint? ResolveKnownMessagingPoint(
        string processName,
        string target,
        int left,
        int top,
        int right,
        int bottom)
    {
        var width = right - left;
        var height = bottom - top;
        if (width < 720 || height < 600 || right <= left || bottom <= top) return null;
        var normalizedProcess = Regex.Replace(
            processName.Normalize(NormalizationForm.FormKC),
            "[\\s\\p{P}\\p{S}]",
            string.Empty,
            RegexOptions.CultureInvariant).ToLowerInvariant();
        if (!normalizedProcess.Contains("weixin", StringComparison.Ordinal) &&
            !normalizedProcess.Contains("wechat", StringComparison.Ordinal))
            return null;

        var normalizedTarget = NormalizeControlText(target);
        var isSubmit = normalizedTarget.Contains("发送", StringComparison.OrdinalIgnoreCase) ||
            normalizedTarget.Contains("发出", StringComparison.OrdinalIgnoreCase);
        var isMessageInput = LooksLikeMessageInputTarget(target);
        if (!isSubmit && !isMessageInput) return null;
        var relativeX = isSubmit ? 0.94 : 0.62;
        var y = isSubmit
            ? bottom - Math.Clamp(checked((int)Math.Round(height * 0.05)), 30, 46)
            : top + checked((int)Math.Round(height * 0.78));
        return new MessagingLayoutPoint(
            left + checked((int)Math.Round(width * relativeX)),
            y);
    }

    private static bool TryReadWindowRectangle(nint handle, out RECT rectangle)
    {
        if (GetWindowRect(handle, out rectangle) &&
            rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top)
            return true;
        rectangle = default;
        return false;
    }

    private PendingTextSubmission GetValidatedPendingSubmission(ForegroundSource foreground)
    {
        var binding = pendingTextSubmission;
        if (binding is null)
            throw new DesktopVisualException(
                "desktop_message_draft_missing",
                "当前没有由 Velia 写入并验证、可以发送的消息草稿。" );
        if (binding.ExpiresAtUtc < DateTimeOffset.UtcNow ||
            binding.WindowHandle != foreground.Handle ||
            binding.ProcessId != foreground.ProcessId ||
            !StringComparer.OrdinalIgnoreCase.Equals(binding.ProcessName, foreground.ProcessName) ||
            !StringComparer.Ordinal.Equals(binding.WindowTitle, foreground.WindowTitle) ||
            !IsMessagingProcess(foreground.ProcessName))
        {
            pendingTextSubmission = null;
            throw new DesktopVisualException(
                "desktop_message_draft_binding_changed",
                "消息窗口或接收对象可能已经变化，请重新输入后再发送。" );
        }
        return binding;
    }

    private DesktopVisualExecuteActionResult ExecuteLocked(DesktopVisualExecuteActionParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ValidateParameters(parameters);
        var binding = TakeTargetBinding(parameters.OperationId, parameters.TargetToken);
        var before = ReadForeground();
        EnsureSafeDesktop(before);
        EnsureExpectedForeground(binding.Source, parameters);
        EnsureExpectedForeground(before, parameters);
        if (before.Handle != binding.Source.Handle)
            throw new DesktopVisualException("desktop_visual_target_changed", "前台窗口在截图后发生变化，已取消桌面动作。", true);

        if (parameters.Action == DesktopVisualActions.AppendText)
            return AppendVerifiedText(parameters, before);

        var target = ResolveActionTarget(parameters, binding.Source.Handle);
        var point = target.Point;
        if (!SetCursorPos(point.X, point.Y))
            throw NativeFailure("desktop_visual_cursor_failed", "无法定位到目标屏幕位置。", true);
        if (!GetCursorPos(out var cursor) || Math.Abs(cursor.X - point.X) > 2 || Math.Abs(cursor.Y - point.Y) > 2)
            throw new DesktopVisualException("desktop_visual_cursor_verification_failed", "光标位置没有通过写后验证。", true);

        var verification = "inputAccepted";
        switch (parameters.Action)
        {
            case DesktopVisualActions.Click:
                if (target.AutomationElement is not null)
                    verification = ActivateAutomationTarget(target.AutomationElement);
                else SendInputs(MouseClick());
                break;
            case DesktopVisualActions.Type:
                SendInputs(MouseClick());
                SendInputs(UnicodeText(parameters.Text!));
                break;
            case DesktopVisualActions.Scroll:
                verification = TryScrollWithUiAutomation(
                    before.Handle,
                    parameters.ScrollDelta!.Value,
                    out var scrollVerification)
                        ? scrollVerification
                        : DispatchVerifiedMouseWheel(parameters.ScrollDelta.Value);
                break;
            case DesktopVisualActions.Press:
                SendInputs(KeyPress(parameters.Key!));
                break;
            default:
                throw new DesktopVisualException("desktop_visual_action_not_allowed", "桌面视觉动作不在安全白名单中。");
        }

        var after = ReadForeground();
        EnsureSafeDesktop(after);
        if (parameters.Action != DesktopVisualActions.Click || target.AutomationElement is null)
            EnsureSameProcessAfterAction(before, after);
        return new DesktopVisualExecuteActionResult(
            parameters.OperationId,
            parameters.Action,
            true,
            point.X,
            point.Y,
            after.ProcessId,
            after.ProcessName,
            after.WindowTitle,
            verification);
    }

    private static void ValidateParameters(DesktopVisualExecuteActionParams parameters)
    {
        if (parameters.OperationId == Guid.Empty || parameters.TargetToken == Guid.Empty ||
            string.IsNullOrWhiteSpace(parameters.Target) || parameters.Target.Length > 240 ||
            string.IsNullOrWhiteSpace(parameters.DisplayId) || parameters.DisplayId.Length > 128)
            throw new DesktopVisualException("invalid_desktop_visual_target", "桌面视觉目标标识无效。");
        if (!DesktopVisualActions.All.Contains(parameters.Action) ||
            parameters.ScreenX is < MinimumCoordinate or > MaximumCoordinate ||
            parameters.ScreenY is < MinimumCoordinate or > MaximumCoordinate)
            throw new DesktopVisualException("invalid_desktop_visual_action", "桌面视觉坐标或动作无效。");
        if (parameters.ExpectedProcessId <= 0 || string.IsNullOrWhiteSpace(parameters.ExpectedProcessName) || parameters.ExpectedProcessName.Length > 128 || parameters.ExpectedWindowTitle.Length > 512)
            throw new DesktopVisualException("invalid_desktop_visual_target", "桌面视觉前台窗口目标无效。");
        switch (parameters.Action)
        {
            case DesktopVisualActions.Type when parameters.Text is not { Length: >= 1 and <= MaximumTypedCharacters }:
                throw new DesktopVisualException("invalid_desktop_visual_input", "桌面视觉输入文字必须为 1 到 1024 个字符。");
            case DesktopVisualActions.AppendText when parameters.Text is not { Length: >= 1 and <= MaximumVerifiedAppendCharacters }:
                throw new DesktopVisualException("invalid_desktop_visual_input", "文档追加文字必须为 1 到 512 个字符。");
            case DesktopVisualActions.Press when parameters.Key is null || !DesktopVisualKeys.All.Contains(parameters.Key):
                throw new DesktopVisualException("invalid_desktop_visual_key", "桌面视觉按键不在安全白名单中。");
            case DesktopVisualActions.Scroll when parameters.ScrollDelta is null or 0 or < -5 or > 5:
                throw new DesktopVisualException("invalid_desktop_visual_scroll", "桌面视觉滚动幅度必须在 -5 到 5 之间且不能为 0。");
        }
    }

    private static DesktopVisualExecuteActionResult AppendVerifiedText(
        DesktopVisualExecuteActionParams parameters,
        ForegroundSource before)
    {
        var text = parameters.Text!.Trim();
        if (text.Length == 0 ||
            text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new DesktopVisualException("invalid_desktop_visual_input", "待写入文字包含无效控制字符。");

        var editor = ResolveEditableSurface(before.Handle);
        try
        {
            editor.SetFocus();
            var editorBounds = editor.Current.BoundingRectangle;
            var editorPoint = new POINT(
                checked((int)Math.Round(editorBounds.X + (editorBounds.Width / 2))),
                checked((int)Math.Round(editorBounds.Y + (editorBounds.Height / 2))));
            if (!SetCursorPos(editorPoint.X, editorPoint.Y))
                throw NativeFailure("desktop_visual_cursor_failed", "无法定位到当前文档编辑区域。", true);
            SendInputs(MouseClick());
            Thread.Sleep(45);
            var focused = AutomationElement.FocusedElement;
            if (focused.Current.ProcessId != before.ProcessId)
                throw new DesktopVisualException(
                    "desktop_visual_editor_focus_failed",
                    "文本光标没有进入当前文档编辑区域。",
                    true);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            throw new DesktopVisualException(
                "desktop_visual_editor_focus_failed",
                "Windows 未能聚焦当前文档编辑区域。",
                true,
                exception);
        }

        _ = TryReadEditableTail(editor, out var beforeTail);
        var expectedTail = NormalizeLineEndings(text);
        var directWriteChanged = false;
        var valuePatternAttempted = TryAppendWithValuePattern(editor, text);
        var directWriteSucceeded = valuePatternAttempted &&
            WaitForExpectedEditableTail(editor, beforeTail, expectedTail, out directWriteChanged);
        var nativeMessageAttempted = false;
        if (!directWriteSucceeded && !directWriteChanged)
        {
            nativeMessageAttempted = TryAppendWithNativeEditMessage(editor, before.ProcessId, beforeTail, text);
            directWriteSucceeded = nativeMessageAttempted &&
                WaitForExpectedEditableTail(editor, beforeTail, expectedTail, out directWriteChanged);
        }
        if (!directWriteSucceeded && !directWriteChanged)
        {
            // TextPattern is read-only and some modern Notepad providers accept
            // Range.Select() without moving the real keyboard caret.  Keep the
            // editor focused and use the native end-of-document shortcut instead.
            SendInputs(ModifiedKeyPress(VirtualKeyControl, VirtualKeyEnd));
            Thread.Sleep(35);
            SendInputs(KeyPress(DesktopVisualKeys.Enter));
            Thread.Sleep(20);
            SendTextWithNewlines(text);
        }

        var verified = false;
        var tailReadable = false;
        var tailChanged = false;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            Thread.Sleep(50);
            var afterAttempt = ReadForeground();
            EnsureSafeDesktop(afterAttempt);
            EnsureSameProcessAfterAction(before, afterAttempt);
            var currentEditor = ResolveEditableSurface(afterAttempt.Handle);
            if (TryReadEditableTail(currentEditor, out var tail))
            {
                tailReadable = true;
                tailChanged |= !StringComparer.Ordinal.Equals(
                    NormalizeLineEndings(beforeTail),
                    NormalizeLineEndings(tail));
                if (NormalizeLineEndings(tail).TrimEnd().EndsWith(expectedTail.TrimEnd(), StringComparison.Ordinal))
                {
                    verified = true;
                    break;
                }
            }
        }
        if (!verified)
        {
            throw new DesktopVisualException(
                !tailReadable
                    ? "desktop_visual_append_readback_unavailable"
                    : tailChanged
                        ? "desktop_visual_append_mismatch"
                        : "desktop_visual_append_no_change",
                !tailReadable
                    ? "Windows 无法回读当前文档内容，已停止并且不会报告成功。"
                    : tailChanged
                        ? "当前文档发生了变化，但文末内容与待写入答案不一致。"
                        : "Windows 已聚焦当前文档，但输入后文末内容没有变化。",
                true);
        }

        var after = ReadForeground();
        EnsureSafeDesktop(after);
        EnsureSameProcessAfterAction(before, after);
        var center = ReadWindowCenter(after.Handle);
        return new DesktopVisualExecuteActionResult(
            parameters.OperationId,
            parameters.Action,
            true,
            center.X,
            center.Y,
            after.ProcessId,
            after.ProcessName,
            after.WindowTitle,
            "uiaTextAppended");
    }

    private static AutomationElement ResolveEditableSurface(nint foregroundHandle)
    {
        if (foregroundHandle == 0 || !IsWindowVisible(foregroundHandle) || IsIconic(foregroundHandle))
        {
            throw new DesktopVisualException("desktop_visual_editor_not_found", "当前窗口没有可访问的文档编辑区域。", true);
        }
        try
        {
            var root = AutomationElement.FromHandle(foregroundHandle);
            var elements = root.FindAll(
                TreeScope.Descendants,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
            var candidates = new List<EditableCandidate>();
            for (var index = 0; index < elements.Count; index++)
            {
                try
                {
                    var current = elements[index].Current;
                    var bounds = current.BoundingRectangle;
                    if (current.IsOffscreen || !current.IsEnabled || current.IsPassword || !IsUsableBounds(bounds)) continue;
                    if (!SupportsWritableText(elements[index])) continue;
                    candidates.Add(new EditableCandidate(bounds, elements[index]));
                }
                catch (ElementNotAvailableException) { }
            }
            var selected = candidates
                .OrderByDescending(candidate => candidate.Bounds.Width * candidate.Bounds.Height)
                .FirstOrDefault();
            if (selected is null)
            {
                throw new DesktopVisualException(
                    "desktop_visual_editor_not_found",
                    "当前窗口没有唯一可写且可验证的文档编辑区域。",
                    true);
            }
            return selected.Element;
        }
        catch (DesktopVisualException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            throw new DesktopVisualException(
                "desktop_visual_editor_not_found",
                "Windows 未能访问当前文档编辑区域。",
                true,
                exception);
        }
    }

    private static AutomationElement ResolveInputSurface(nint foregroundHandle, string target)
    {
        if (foregroundHandle == 0 || !IsWindowVisible(foregroundHandle) || IsIconic(foregroundHandle))
            throw new DesktopVisualException("desktop_text_input_not_found", "当前窗口没有可访问的输入框。", true);
        try
        {
            var root = AutomationElement.FromHandle(foregroundHandle);
            var elements = root.FindAll(
                TreeScope.Descendants,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
            var candidates = new List<InputCandidate>();
            for (var index = 0; index < elements.Count; index++)
            {
                try
                {
                    var current = elements[index].Current;
                    var bounds = current.BoundingRectangle;
                    if (current.IsOffscreen || !current.IsEnabled || current.IsPassword ||
                        !IsUsableBounds(bounds) || !SupportsWritableText(elements[index]))
                        continue;
                    candidates.Add(new InputCandidate(
                        new AccessibleInputDescriptor(
                            current.Name ?? string.Empty,
                            current.AutomationId ?? string.Empty,
                            current.HelpText ?? string.Empty,
                            current.ControlType?.ProgrammaticName ?? string.Empty,
                            current.HasKeyboardFocus,
                            bounds.X,
                            bounds.Y,
                            bounds.Width,
                            bounds.Height),
                        elements[index]));
                }
                catch (ElementNotAvailableException) { }
            }
            var selected = SelectInputCandidateIndex(
                target,
                candidates.Select(candidate => candidate.Descriptor).ToArray());
            if (selected is not int indexValue)
                throw new DesktopVisualException(
                    "desktop_text_input_not_found",
                    "当前窗口没有唯一可写且可验证的目标输入框。",
                    true);
            return candidates[indexValue].Element;
        }
        catch (DesktopVisualException) { throw; }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            throw new DesktopVisualException(
                "desktop_text_input_not_found",
                "Windows 未能访问当前窗口的输入框。",
                true,
                exception);
        }
    }

    internal static int? SelectInputCandidateIndex(
        string target,
        IReadOnlyList<AccessibleInputDescriptor> candidates)
    {
        if (candidates.Count == 0) return null;
        var working = candidates
            .Select((descriptor, index) => new { Descriptor = descriptor, Index = index })
            .ToArray();

        var editControls = working
            .Where(item => StringComparer.Ordinal.Equals(
                item.Descriptor.ControlTypeName,
                ControlType.Edit.ProgrammaticName))
            .ToArray();
        if (editControls.Length > 0) working = editControls;

        var searchRequested = LooksLikeSearchInputTarget(target);
        if (searchRequested)
        {
            var search = working.Where(item => LooksLikeSearchInput(item.Descriptor)).ToArray();
            if (search.Length == 1) return search[0].Index;
            if (search.Length > 1) working = search;
            var focusedSearch = working.Where(item => item.Descriptor.HasKeyboardFocus).ToArray();
            if (focusedSearch.Length == 1) return focusedSearch[0].Index;
        }
        else if (LooksLikeMessageInputTarget(target))
        {
            var nonSearch = working.Where(item => !LooksLikeSearchInput(item.Descriptor)).ToArray();
            if (nonSearch.Length > 0) working = nonSearch;
            var message = working.Where(item => LooksLikeMessageInput(item.Descriptor)).ToArray();
            if (message.Length == 1) return message[0].Index;
            if (message.Length > 1) working = message;
            var focusedMessage = working.Where(item => item.Descriptor.HasKeyboardFocus).ToArray();
            if (focusedMessage.Length == 1) return focusedMessage[0].Index;
        }
        else
        {
            var focused = working.Where(item => item.Descriptor.HasKeyboardFocus).ToArray();
            if (focused.Length == 1) return focused[0].Index;
        }

        if (!IsGenericInputTarget(target))
        {
            var selected = SelectUniqueControlIndex(
                target,
                working.Select(item => new AccessibleControlDescriptor(
                    item.Descriptor.Name,
                    item.Descriptor.AutomationId,
                    item.Descriptor.HelpText,
                    string.Empty,
                    item.Descriptor.ControlTypeName)).ToArray());
            if (selected is int selectedIndex) return working[selectedIndex].Index;
        }

        if (LooksLikeMessageInputTarget(target) && working.Length > 1)
        {
            var bottomRanked = working
                .OrderByDescending(item => item.Descriptor.Y + item.Descriptor.Height)
                .ThenByDescending(item => item.Descriptor.Area)
                .ToArray();
            var bottomGap =
                (bottomRanked[0].Descriptor.Y + bottomRanked[0].Descriptor.Height) -
                (bottomRanked[1].Descriptor.Y + bottomRanked[1].Descriptor.Height);
            if (bottomGap >= 24) return bottomRanked[0].Index;
        }

        var ranked = working
            .OrderByDescending(item => item.Descriptor.Area)
            .ThenByDescending(item => item.Descriptor.Y)
            .ToArray();
        if (ranked.Length == 1) return ranked[0].Index;
        return ranked[0].Descriptor.Area >= ranked[1].Descriptor.Area * 1.25
            ? ranked[0].Index
            : null;
    }

    private static bool IsGenericInputTarget(string target)
    {
        var normalized = NormalizeControlText(target);
        return normalized is "当前输入框" or "输入框" or "消息输入框" or "聊天输入框" or
            "微信" or "QQ" or "企业微信" or "钉钉" or "飞书";
    }

    private static bool LooksLikeSearchInputTarget(string target) =>
        target.Contains("搜索", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("查找", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeMessageInputTarget(string target) =>
        target.Contains("消息", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("聊天", StringComparison.OrdinalIgnoreCase) ||
        target is "微信" or "QQ" or "企业微信" or "钉钉" or "飞书";

    private static bool LooksLikeSearchInput(AccessibleInputDescriptor descriptor)
    {
        var metadata = NormalizeControlText(
            descriptor.Name + descriptor.AutomationId + descriptor.HelpText);
        return metadata.Contains("搜索", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("查找", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("search", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("find", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeMessageInput(AccessibleInputDescriptor descriptor)
    {
        var metadata = NormalizeControlText(
            descriptor.Name + descriptor.AutomationId + descriptor.HelpText);
        if (metadata.Contains("记录", StringComparison.OrdinalIgnoreCase) ||
            metadata.Contains("历史", StringComparison.OrdinalIgnoreCase) ||
            metadata.Contains("history", StringComparison.OrdinalIgnoreCase) ||
            metadata.Contains("list", StringComparison.OrdinalIgnoreCase))
            return false;
        return metadata.Contains("输入消息", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("消息输入", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("聊天输入", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("发送", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("messageinput", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("chatinput", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("inputmsg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryAppendInputText(AutomationElement element, string previous, string text)
    {
        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternValue) ||
                patternValue is not ValuePattern valuePattern || valuePattern.Current.IsReadOnly)
                return false;
            valuePattern.SetValue(previous + text);
            return true;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static bool WaitForEnteredText(
        AutomationElement editor,
        string previous,
        string text,
        CancellationToken cancellationToken,
        out string verified)
    {
        verified = previous;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(40);
            if (!TryReadEditableText(editor, out var current)) continue;
            verified = current;
            if (!StringComparer.Ordinal.Equals(
                    NormalizeLineEndings(previous),
                    NormalizeLineEndings(current)) &&
                ContainsStagedText(current, text))
                return true;
        }
        return false;
    }

    private static bool WaitForSubmittedText(
        AutomationElement editor,
        string text,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(50);
            if (TryReadEditableText(editor, out var current) && !ContainsStagedText(current, text))
                return true;
        }
        return false;
    }

    private static bool ContainsStagedText(string current, string text) =>
        NormalizeLineEndings(current).TrimEnd('\r', '\n', '\a')
            .EndsWith(NormalizeLineEndings(text).TrimEnd('\r', '\n'), StringComparison.Ordinal);

    internal static bool IsMessagingProcess(string processName)
    {
        var normalized = Regex.Replace(
            processName.Normalize(NormalizationForm.FormKC),
            "[\\s\\p{P}\\p{S}]",
            string.Empty,
            RegexOptions.CultureInvariant).ToLowerInvariant();
        return normalized.Contains("weixin", StringComparison.Ordinal) ||
               normalized.Contains("wechat", StringComparison.Ordinal) ||
               normalized is "qq" or "qqnt" ||
               normalized.Contains("wxwork", StringComparison.Ordinal) ||
               normalized.Contains("wecom", StringComparison.Ordinal) ||
               normalized.Contains("dingtalk", StringComparison.Ordinal) ||
               normalized.Contains("feishu", StringComparison.Ordinal) ||
               normalized is "lark";
    }

    internal static bool MatchesMessagingApplication(string application, string processName)
    {
        var normalizedProcess = Regex.Replace(
            processName.Normalize(NormalizationForm.FormKC),
            "[\\s\\p{P}\\p{S}]",
            string.Empty,
            RegexOptions.CultureInvariant).ToLowerInvariant();
        return application switch
        {
            "微信" => (normalizedProcess.Contains("weixin", StringComparison.Ordinal) ||
                     normalizedProcess.Contains("wechat", StringComparison.Ordinal)) &&
                    !normalizedProcess.Contains("wxwork", StringComparison.Ordinal) &&
                    !normalizedProcess.Contains("wecom", StringComparison.Ordinal),
            "QQ" => normalizedProcess is "qq" or "qqnt" ||
                    normalizedProcess.StartsWith("qq", StringComparison.Ordinal),
            "企业微信" => normalizedProcess.Contains("wxwork", StringComparison.Ordinal) ||
                      normalizedProcess.Contains("wecom", StringComparison.Ordinal),
            "钉钉" => normalizedProcess.Contains("dingtalk", StringComparison.Ordinal),
            "飞书" => normalizedProcess.Contains("feishu", StringComparison.Ordinal) ||
                    normalizedProcess is "lark",
            _ => false,
        };
    }

    private static void EnsureExpectedMessagingApplication(
        DeterministicDesktopActionCommand command,
        ForegroundSource foreground)
    {
        if (string.IsNullOrEmpty(command.Application)) return;
        if (MatchesMessagingApplication(command.Application, foreground.ProcessName)) return;
        throw new DesktopVisualException(
            "desktop_text_input_wrong_application",
            $"当前前台窗口不是{command.Application}，已停止输入。");
    }

    private static bool SupportsWritableText(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternValue) &&
            textPatternValue is TextPattern)
            return true;
        return element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternValue) &&
            valuePatternValue is ValuePattern valuePattern && !valuePattern.Current.IsReadOnly;
    }

    private static bool TryReadEditableTail(AutomationElement element, out string tail)
    {
        tail = string.Empty;
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternValue) &&
                textPatternValue is TextPattern textPattern)
            {
                var range = textPattern.DocumentRange.Clone();
                range.MoveEndpointByRange(
                    TextPatternRangeEndpoint.Start,
                    textPattern.DocumentRange,
                    TextPatternRangeEndpoint.End);
                range.MoveEndpointByUnit(
                    TextPatternRangeEndpoint.Start,
                    TextUnit.Character,
                    -MaximumVerificationTailCharacters);
                tail = range.GetText(MaximumVerificationTailCharacters);
                return true;
            }
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternValue) &&
                valuePatternValue is ValuePattern valuePattern)
            {
                var value = valuePattern.Current.Value;
                tail = value.Length <= MaximumVerificationTailCharacters
                    ? value
                    : value[^MaximumVerificationTailCharacters..];
                return true;
            }
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
        return false;
    }

    private static bool TryReadEditableText(AutomationElement element, out string text)
    {
        text = string.Empty;
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && value is ValuePattern valuePattern)
            {
                text = valuePattern.Current.Value;
                return text.Length <= MaximumValuePatternDocumentCharacters;
            }
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) && pattern is TextPattern textPattern)
            {
                text = textPattern.DocumentRange.GetText(MaximumValuePatternDocumentCharacters + 1);
                return text.Length <= MaximumValuePatternDocumentCharacters;
            }
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException) { }
        return false;
    }

    private static bool TrySetEditableText(AutomationElement element, int expectedProcessId, string text)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) &&
                value is ValuePattern valuePattern && !valuePattern.Current.IsReadOnly)
            {
                valuePattern.SetValue(text);
                return true;
            }
            var nativeHandle = (nint)element.Current.NativeWindowHandle;
            if (nativeHandle == 0)
            {
                var bounds = element.Current.BoundingRectangle;
                if (!IsUsableBounds(bounds)) return false;
                nativeHandle = WindowFromPoint(new POINT(
                    checked((int)Math.Round(bounds.X + (bounds.Width / 2))),
                    checked((int)Math.Round(bounds.Y + (bounds.Height / 2)))));
            }
            if (nativeHandle == 0) return false;
            _ = GetWindowThreadProcessId(nativeHandle, out var processId);
            if (processId != checked((uint)expectedProcessId)) return false;
            _ = SendMessage(nativeHandle, EditSetSelection, 0, unchecked((nint)(-1)));
            _ = SendMessageText(nativeHandle, EditReplaceSelection, 1, text);
            return true;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException) { return false; }
    }

    private static bool WaitForExpectedEditableText(AutomationElement element, string expected)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Thread.Sleep(50);
            if (TryReadEditableText(element, out var value) &&
                StringComparer.Ordinal.Equals(NormalizeLineEndings(value), NormalizeLineEndings(expected)))
                return true;
        }
        return false;
    }

    private static string AppendDocumentText(string current, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new DesktopVisualException("document_text_invalid", "要写入的文字为空。" );
        if (current.Length + text.Length + Environment.NewLine.Length > MaximumValuePatternDocumentCharacters)
            throw new DesktopVisualException("document_too_large", "当前文档过长，无法安全追加。" );
        var separator = current.Length == 0 || current.EndsWith('\n') || current.EndsWith('\r')
            ? string.Empty
            : Environment.NewLine;
        return current + separator + text;
    }

    private static string ReplaceDocumentText(string current, string match, string replacement)
    {
        if (string.IsNullOrWhiteSpace(match) || !current.Contains(match, StringComparison.Ordinal))
            throw new DesktopVisualException("document_text_not_found", $"当前文档中没有找到“{match}”。" );
        return current.Replace(match, replacement, StringComparison.Ordinal);
    }

    private static bool HasNonEmptyTextSelection(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternValue) ||
                patternValue is not TextPattern textPattern)
                return false;
            return textPattern.GetSelection().Any(range =>
                range.CompareEndpoints(
                    TextPatternRangeEndpoint.Start,
                    range,
                    TextPatternRangeEndpoint.End) != 0);
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static bool TryReadSelectedText(AutomationElement element, out string selectedText)
    {
        selectedText = string.Empty;
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternValue) ||
                patternValue is not TextPattern textPattern)
                return false;
            var selections = textPattern.GetSelection();
            if (selections.Length != 1) return false;
            selectedText = selections[0].GetText(MaximumValuePatternDocumentCharacters).TrimEnd('\r', '\n');
            return selectedText.Length > 0;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static bool WaitForEditableTextChange(
        AutomationElement element,
        string before,
        out string after)
    {
        after = before;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Thread.Sleep(50);
            if (!TryReadEditableText(element, out var current)) continue;
            after = current;
            if (!StringComparer.Ordinal.Equals(
                    NormalizeLineEndings(before),
                    NormalizeLineEndings(current)))
                return true;
        }
        return false;
    }

    private static bool TryAppendWithValuePattern(AutomationElement element, string text)
    {
        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternValue) ||
                patternValue is not ValuePattern valuePattern || valuePattern.Current.IsReadOnly)
                return false;
            var current = valuePattern.Current.Value;
            if (current.Length + text.Length + Environment.NewLine.Length > MaximumValuePatternDocumentCharacters)
                return false;
            var separator = current.Length == 0 || current.EndsWith('\n') || current.EndsWith('\r')
                ? string.Empty
                : Environment.NewLine;
            valuePattern.SetValue(current + separator + text);
            return true;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static bool TryAppendWithNativeEditMessage(
        AutomationElement element,
        int expectedProcessId,
        string currentTail,
        string text)
    {
        try
        {
            var nativeHandle = (nint)element.Current.NativeWindowHandle;
            if (nativeHandle == 0)
            {
                var bounds = element.Current.BoundingRectangle;
                if (!IsUsableBounds(bounds)) return false;
                nativeHandle = WindowFromPoint(new POINT(
                    checked((int)Math.Round(bounds.X + (bounds.Width / 2))),
                    checked((int)Math.Round(bounds.Y + (bounds.Height / 2)))));
            }
            if (nativeHandle == 0)
                return false;
            _ = GetWindowThreadProcessId(nativeHandle, out var processIdValue);
            if (processIdValue != checked((uint)expectedProcessId)) return false;

            var separator = currentTail.Length == 0 || currentTail.EndsWith('\n') || currentTail.EndsWith('\r')
                ? string.Empty
                : Environment.NewLine;
            _ = SendMessage(nativeHandle, EditSetSelection, unchecked((nint)(-1)), unchecked((nint)(-1)));
            _ = SendMessageText(nativeHandle, EditReplaceSelection, (nint)1, separator + text);
            return true;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static bool WaitForExpectedEditableTail(
        AutomationElement element,
        string beforeTail,
        string expectedTail,
        out bool changed)
    {
        changed = false;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            Thread.Sleep(50);
            if (!TryReadEditableTail(element, out var tail)) continue;
            var normalizedTail = NormalizeLineEndings(tail);
            changed |= !StringComparer.Ordinal.Equals(NormalizeLineEndings(beforeTail), normalizedTail);
            if (normalizedTail.TrimEnd().EndsWith(expectedTail.TrimEnd(), StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static void SendTextWithNewlines(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') continue;
                inputs.AddRange(KeyPress(DesktopVisualKeys.Enter));
            }
            else if (character == '\n')
            {
                inputs.AddRange(KeyPress(DesktopVisualKeys.Enter));
            }
            else if (character == '\t')
            {
                inputs.AddRange(KeyPress(DesktopVisualKeys.Tab));
            }
            else
            {
                inputs.AddRange(UnicodeText(character.ToString()));
            }
        }
        SendInputs([.. inputs]);
    }

    private static void EnsureExpectedForeground(ForegroundSource actual, DesktopVisualExecuteActionParams expected)
    {
        EnsureExpectedForeground(actual.ProcessId, actual.ProcessName, actual.WindowTitle, expected);
    }

    private static ResolvedActionTarget ResolveActionTarget(DesktopVisualExecuteActionParams parameters, nint foregroundHandle)
    {
        if (parameters.Action != DesktopVisualActions.Click)
            return new ResolvedActionTarget(new POINT(parameters.ScreenX, parameters.ScreenY), null);

        var menuTarget = IsMenuTarget(parameters.Target);
        if (foregroundHandle == 0 || !IsWindowVisible(foregroundHandle) || IsIconic(foregroundHandle))
        {
            if (parameters.StructuredTargetOnly || menuTarget)
                throw new DesktopVisualException("desktop_visual_target_not_found", "未能找到可访问的目标控件，已停止桌面动作。", true);
            return new ResolvedActionTarget(new POINT(parameters.ScreenX, parameters.ScreenY), null);
        }
        try
        {
            var root = AutomationElement.FromHandle(foregroundHandle);
            var elements = root.FindAll(
                TreeScope.Descendants,
                new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)));
            var candidates = new List<AutomationCandidate>();
            for (var index = 0; index < elements.Count; index++)
            {
                try
                {
                    var current = elements[index].Current;
                    var candidateBounds = current.BoundingRectangle;
                    var descriptor = DescribeControl(current);
                    var canScrollIntoView = SupportsScrollIntoView(elements[index]);
                    if (!descriptor.HasIdentity || !current.IsEnabled ||
                        (current.IsOffscreen && !canScrollIntoView) ||
                        (!IsUsableBounds(candidateBounds) && !canScrollIntoView)) continue;
                    candidates.Add(new AutomationCandidate(
                        descriptor,
                        current.ControlType,
                        candidateBounds,
                        elements[index],
                        current.IsOffscreen));
                }
                catch (ElementNotAvailableException)
                {
                    // Controls can disappear while UI Automation is enumerating stale nodes.
                }
            }
            var eligibleCandidates = menuTarget
                ? candidates.Where(candidate => candidate.ControlType == ControlType.MenuItem).ToArray()
                : candidates.ToArray();
            var selectedName = menuTarget
                ? SelectUniqueMenuName(parameters.Target, eligibleCandidates.Select(candidate => candidate.Descriptor.Name))
                : null;
            var selectedIndex = menuTarget
                ? null
                : SelectUniqueControlIndex(parameters.Target, eligibleCandidates.Select(candidate => candidate.Descriptor).ToArray());
            var matches = menuTarget
                ? selectedName is null
                    ? []
                    : eligibleCandidates.Where(candidate => StringComparer.Ordinal.Equals(
                        NormalizeControlText(candidate.Descriptor.Name), NormalizeControlText(selectedName))).ToArray()
                : selectedIndex is int selectedCandidateIndex
                    ? [eligibleCandidates[selectedCandidateIndex]]
                    : [];
            if (matches.Length != 1 && menuTarget)
            {
                var processMenuElements = AutomationElement.RootElement.FindAll(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ProcessIdProperty, parameters.ExpectedProcessId),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)));
                var processMenuCandidates = new List<AutomationCandidate>();
                for (var index = 0; index < processMenuElements.Count; index++)
                {
                    try
                    {
                        var current = processMenuElements[index].Current;
                        var bounds = current.BoundingRectangle;
                        if (string.IsNullOrWhiteSpace(current.Name) || current.IsOffscreen || !current.IsEnabled ||
                            !IsUsableBounds(bounds)) continue;
                        processMenuCandidates.Add(new AutomationCandidate(
                            DescribeControl(current),
                            current.ControlType,
                            bounds,
                            processMenuElements[index],
                            false));
                    }
                    catch (ElementNotAvailableException) { }
                }
                selectedName = SelectUniqueMenuName(
                    parameters.Target,
                    processMenuCandidates.Select(candidate => candidate.Descriptor.Name));
                matches = selectedName is null
                    ? []
                    : processMenuCandidates.Where(candidate => StringComparer.Ordinal.Equals(
                        NormalizeControlText(candidate.Descriptor.Name), NormalizeControlText(selectedName))).ToArray();
            }
            if (matches.Length != 1)
            {
                if (parameters.StructuredTargetOnly || menuTarget)
                    throw new DesktopVisualException("desktop_visual_target_not_found", "未能找到唯一可访问的目标控件。", true);
                return new ResolvedActionTarget(new POINT(parameters.ScreenX, parameters.ScreenY), null);
            }
            var selectedBounds = matches[0].IsOffscreen || !IsUsableBounds(matches[0].Bounds)
                ? ScrollAutomationElementIntoView(matches[0].Element)
                : matches[0].Bounds;
            return new ResolvedActionTarget(
                new POINT(
                    checked((int)Math.Round(selectedBounds.X + (selectedBounds.Width / 2))),
                    checked((int)Math.Round(selectedBounds.Y + (selectedBounds.Height / 2)))),
                matches[0].Element);
        }
        catch (DesktopVisualException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            if (parameters.StructuredTargetOnly || menuTarget)
                throw new DesktopVisualException("desktop_visual_target_not_found", "未能访问前台窗口的目标控件。", true, exception);
            return new ResolvedActionTarget(new POINT(parameters.ScreenX, parameters.ScreenY), null);
        }
    }

    private static string ActivateAutomationTarget(AutomationElement element)
    {
        if (element.Current.ControlType == ControlType.MenuItem)
            return ActivateMenu(element);
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern) &&
                invokePattern is InvokePattern invokable)
            {
                invokable.Invoke();
                return "uiaInvokeDispatched";
            }
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern) &&
                selectionPattern is SelectionItemPattern selectable)
            {
                selectable.Select();
                if (!WaitForAutomationState(
                        () => selectable.Current.IsSelected,
                        expected: true))
                    throw new DesktopVisualException(
                        "desktop_visual_uia_activation_unverified",
                        "Windows 没有确认目标选项已经选中。",
                        true);
                return "uiaSelectionVerified";
            }
            if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern) &&
                expandPattern is ExpandCollapsePattern expandable)
            {
                var before = expandable.Current.ExpandCollapseState;
                var expected = before == ExpandCollapseState.Expanded
                    ? ExpandCollapseState.Collapsed
                    : ExpandCollapseState.Expanded;
                if (expected == ExpandCollapseState.Collapsed) expandable.Collapse();
                else expandable.Expand();
                if (!WaitForAutomationState(
                        () => expandable.Current.ExpandCollapseState == expected,
                        expected: true))
                    throw new DesktopVisualException(
                        "desktop_visual_uia_activation_unverified",
                        "Windows 没有确认目标控件的展开状态已经变化。",
                        true);
                return expected == ExpandCollapseState.Expanded
                    ? "uiaExpansionVerified"
                    : "uiaCollapseVerified";
            }
            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern) &&
                togglePattern is TogglePattern toggleable)
            {
                var before = toggleable.Current.ToggleState;
                toggleable.Toggle();
                if (!WaitForAutomationState(
                        () => toggleable.Current.ToggleState != before,
                        expected: true))
                    throw new DesktopVisualException(
                        "desktop_visual_uia_activation_unverified",
                        "Windows 没有确认目标开关状态已经变化。",
                        true);
                return "uiaToggleVerified";
            }
            if (element.Current.ControlType == ControlType.Edit)
            {
                element.SetFocus();
                if (!WaitForAutomationState(
                        () => element.Current.HasKeyboardFocus,
                        expected: true))
                    throw new DesktopVisualException(
                        "desktop_visual_uia_activation_unverified",
                        "Windows 没有确认目标输入框已经获得焦点。",
                        true);
                return "uiaFocusVerified";
            }
            SendInputs(MouseClick());
            return "uiaClicked";
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            throw new DesktopVisualException(
                "desktop_visual_uia_activation_failed",
                "Windows 未能激活目标控件。",
                true,
                exception);
        }
    }

    private static string ActivateMenu(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern) &&
                expandPattern is ExpandCollapsePattern expandable)
            {
                if (expandable.Current.ExpandCollapseState != ExpandCollapseState.Expanded) expandable.Expand();
                if (!WaitForAutomationState(
                        () => expandable.Current.ExpandCollapseState == ExpandCollapseState.Expanded,
                        expected: true,
                        elementUnavailableIsSuccess: true))
                    throw new DesktopVisualException(
                        "desktop_visual_menu_activation_failed",
                        "Windows 没有确认目标菜单已经展开。",
                        true);
                return "menuExpanded";
            }
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern) &&
                invokePattern is InvokePattern invokable)
            {
                invokable.Invoke();
                return "menuInvoked";
            }
            throw new DesktopVisualException(
                "desktop_visual_menu_activation_unavailable",
                "当前菜单不支持可靠的原生展开动作，已停止桌面操作。",
                true);
        }
        catch (DesktopVisualException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            throw new DesktopVisualException(
                "desktop_visual_menu_activation_failed",
                "Windows 未能展开目标菜单，已停止桌面操作。",
                true,
                exception);
        }
    }

    private static bool IsMenuTarget(string target)
    {
        if (Regex.IsMatch(target, "菜单|菜单栏|下拉菜单|菜单项", RegexOptions.CultureInvariant)) return true;
        var label = NormalizeMenuText(ExtractMenuLabel(target));
        return label is "文件" or "编辑" or "查看" or "格式" or "工具" or "窗口" or "帮助";
    }

    internal static string? SelectUniqueMenuName(string target, IEnumerable<string> menuNames)
    {
        var candidates = menuNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new { Original = name, Normalized = NormalizeMenuText(name) })
            .Where(candidate => candidate.Normalized.Length > 0)
            .ToArray();
        var extracted = NormalizeMenuText(ExtractMenuLabel(target));
        var exact = candidates.Where(candidate => StringComparer.OrdinalIgnoreCase.Equals(candidate.Normalized, extracted)).ToArray();
        if (exact.Length == 1) return exact[0].Original;
        if (exact.Length > 1) return null;

        var normalizedTarget = NormalizeMenuText(target);
        var contained = candidates
            .Where(candidate => candidate.Normalized.Length >= 2 &&
                normalizedTarget.Contains(candidate.Normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return contained.Length == 1 ? contained[0].Original : null;
    }

    internal static string? SelectUniqueControlName(string target, IEnumerable<string> controlNames)
    {
        var candidates = controlNames
            .Select(name => new AccessibleControlDescriptor(name, string.Empty, string.Empty, string.Empty, string.Empty))
            .ToArray();
        var selected = SelectUniqueControlIndex(target, candidates);
        return selected is int index ? candidates[index].Name : null;
    }

    internal static int? SelectUniqueControlIndex(
        string target,
        IReadOnlyList<AccessibleControlDescriptor> candidates)
    {
        if (string.IsNullOrWhiteSpace(target) || candidates.Count == 0) return null;
        var query = NormalizeControlText(ExtractControlLabel(target));
        if (query.Length == 0) return null;
        var normalizedTarget = NormalizeControlText(target);
        var requestedType = GetRequestedControlType(target);
        var requestedSemantics = GetSemanticKeys(query + normalizedTarget);
        var ranked = candidates
            .Select((candidate, index) => new
            {
                Index = index,
                Score = ScoreControlCandidate(
                    candidate,
                    query,
                    normalizedTarget,
                    requestedType,
                    requestedSemantics),
            })
            .Where(candidate => candidate.Score >= 76)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Index)
            .ToArray();
        if (ranked.Length == 0) return null;
        if (ranked.Length > 1 && ranked[0].Score - ranked[1].Score < 8) return null;
        return ranked[0].Index;
    }

    private static int ScoreControlCandidate(
        AccessibleControlDescriptor candidate,
        string query,
        string normalizedTarget,
        string? requestedType,
        HashSet<string> requestedSemantics)
    {
        var fields = new (string Value, int Exact, int Semantic)[]
        {
            (candidate.Name, 120, 98),
            (candidate.AutomationId, 110, 94),
            (candidate.HelpText, 104, 90),
            (candidate.AccessKey, 98, 86),
        };
        var best = 0;
        foreach (var field in fields)
        {
            var normalized = NormalizeControlText(field.Value);
            if (normalized.Length == 0) continue;
            if (StringComparer.OrdinalIgnoreCase.Equals(normalized, query))
                best = Math.Max(best, field.Exact);
            else if (query.Length >= 2 && normalized.Contains(query, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, field.Exact - 18);
            else if (normalized.Length >= 2 && query.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, field.Exact - 24);
            else if (normalized.Length >= 2 && normalizedTarget.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, field.Exact - 30);

            if (requestedSemantics.Count > 0 &&
                GetSemanticKeys(normalized).Overlaps(requestedSemantics))
                best = Math.Max(best, field.Semantic);
        }
        if (best == 0) return 0;
        if (requestedType is not null)
            best += StringComparer.Ordinal.Equals(candidate.ControlTypeName, requestedType) ? 14 : -30;
        return best;
    }

    private static AccessibleControlDescriptor DescribeControl(AutomationElement.AutomationElementInformation current) =>
        new(
            current.Name ?? string.Empty,
            current.AutomationId ?? string.Empty,
            current.HelpText ?? string.Empty,
            current.AccessKey ?? string.Empty,
            current.ControlType?.ProgrammaticName ?? string.Empty);

    private static string? GetRequestedControlType(string target)
    {
        var value = target.Normalize(NormalizationForm.FormKC);
        if (Regex.IsMatch(value, "输入框|文本框|搜索框|编辑框", RegexOptions.CultureInvariant))
            return ControlType.Edit.ProgrammaticName;
        if (Regex.IsMatch(value, "复选框|勾选框", RegexOptions.CultureInvariant))
            return ControlType.CheckBox.ProgrammaticName;
        if (Regex.IsMatch(value, "单选框", RegexOptions.CultureInvariant))
            return ControlType.RadioButton.ProgrammaticName;
        if (Regex.IsMatch(value, "组合框|下拉框", RegexOptions.CultureInvariant))
            return ControlType.ComboBox.ProgrammaticName;
        if (Regex.IsMatch(value, "标签页", RegexOptions.CultureInvariant))
            return ControlType.TabItem.ProgrammaticName;
        if (Regex.IsMatch(value, "树节点|树项目", RegexOptions.CultureInvariant))
            return ControlType.TreeItem.ProgrammaticName;
        if (Regex.IsMatch(value, "列表项", RegexOptions.CultureInvariant))
            return ControlType.ListItem.ProgrammaticName;
        if (Regex.IsMatch(value, "链接", RegexOptions.CultureInvariant))
            return ControlType.Hyperlink.ProgrammaticName;
        if (Regex.IsMatch(value, "按钮|图标", RegexOptions.CultureInvariant))
            return ControlType.Button.ProgrammaticName;
        return null;
    }

    private static HashSet<string> GetSemanticKeys(string value)
    {
        var normalized = NormalizeControlText(value).ToLowerInvariant();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, aliases) in ControlSemanticAliases)
        {
            if (aliases.Any(alias => normalized.Contains(alias, StringComparison.Ordinal)))
                keys.Add(key);
        }
        return keys;
    }

    private static string ExtractMenuLabel(string target)
    {
        var value = target.Normalize(NormalizationForm.FormKC).Trim();
        var actionMatches = Regex.Matches(value, "(?:点击|单击|点开|打开|展开|选择|按下)\\s*(?<target>.+)", RegexOptions.CultureInvariant);
        if (actionMatches.Count > 0) value = actionMatches[^1].Groups["target"].Value;
        value = value.Split(['的', '之'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? value;
        value = Regex.Replace(value, "(?:下拉菜单|菜单栏|菜单项|菜单|选项|功能)\\s*[\\p{P}\\p{S}\\s]*$", string.Empty, RegexOptions.CultureInvariant);
        return value.Trim();
    }

    private static string ExtractControlLabel(string target)
    {
        var value = target.Normalize(NormalizationForm.FormKC).Trim();
        var actionMatches = Regex.Matches(
            value,
            "(?:点击|单击|点开|打开|展开|选择|按下)\\s*(?<target>.+)",
            RegexOptions.CultureInvariant);
        if (actionMatches.Count > 0) value = actionMatches[^1].Groups["target"].Value;
        value = value.Split(['的', '之'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? value;
        value = Regex.Replace(
            value,
            "(?:下拉菜单|菜单栏|菜单项|菜单|按钮|标签页|标签|列表项|选项|图标|链接|输入框|文本框|页面|页码|页|控件|功能)\\s*[\\p{P}\\p{S}\\s]*$",
            string.Empty,
            RegexOptions.CultureInvariant);
        return value.Trim();
    }

    private static string NormalizeMenuText(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        normalized = Regex.Replace(normalized, "\\s*[（(]\\s*[A-Za-z]\\s*[)）]\\s*$", string.Empty, RegexOptions.CultureInvariant);
        return Regex.Replace(normalized, "[\\s\\p{P}\\p{S}]", string.Empty, RegexOptions.CultureInvariant);
    }

    private static string NormalizeControlText(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        normalized = Regex.Replace(normalized, "\\s*[（(]\\s*[A-Za-z]\\s*[)）]\\s*$", string.Empty, RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, "[\\s\\p{P}\\p{S}]", string.Empty, RegexOptions.CultureInvariant);
        var ordinal = Regex.Match(normalized, "^第?(?<number>[零〇一二两三四五六七八九十百]+)(?:个)?$", RegexOptions.CultureInvariant);
        return ordinal.Success && TryParseChineseOrdinal(ordinal.Groups["number"].Value, out var number)
            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : normalized;
    }

    private static bool TryParseChineseOrdinal(string value, out int number)
    {
        number = 0;
        if (value is "零" or "〇") return true;
        if (value == "十")
        {
            number = 10;
            return true;
        }
        var digits = new Dictionary<char, int>
        {
            ['零'] = 0, ['〇'] = 0, ['一'] = 1, ['二'] = 2, ['两'] = 2, ['三'] = 3,
            ['四'] = 4, ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9,
        };
        if (value.Length == 1 && digits.TryGetValue(value[0], out number)) return true;
        var ten = value.IndexOf('十');
        if (ten >= 0 && value.IndexOf('百') < 0)
        {
            var tens = ten == 0 ? 1 : digits.GetValueOrDefault(value[ten - 1]);
            var ones = ten == value.Length - 1 ? 0 : digits.GetValueOrDefault(value[ten + 1]);
            number = (tens * 10) + ones;
            return number is > 0 and < 100;
        }
        return false;
    }

    private static bool IsUsableBounds(Rect bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
        bounds.Width >= 2 && bounds.Height >= 2 &&
        bounds.X is >= MinimumCoordinate and <= MaximumCoordinate &&
        bounds.Y is >= MinimumCoordinate and <= MaximumCoordinate;

    private static bool SupportsScrollIntoView(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var pattern) &&
                pattern is ScrollItemPattern;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static Rect ScrollAutomationElementIntoView(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var pattern) ||
                pattern is not ScrollItemPattern scrollItem)
                throw new DesktopVisualException(
                    "desktop_visual_target_not_found",
                    "目标控件当前不在可见区域，也不支持原生滚入视野。",
                    true);
            scrollItem.ScrollIntoView();
            for (var attempt = 0; attempt < 12; attempt++)
            {
                Thread.Sleep(40);
                var current = element.Current;
                if (!current.IsOffscreen && IsUsableBounds(current.BoundingRectangle))
                    return current.BoundingRectangle;
            }
            throw new DesktopVisualException(
                "desktop_visual_target_not_found",
                "Windows 没有确认目标控件已经滚入可见区域。",
                true);
        }
        catch (DesktopVisualException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            throw new DesktopVisualException(
                "desktop_visual_target_not_found",
                "滚动过程中目标控件已经失效，请重新查看当前窗口。",
                true,
                exception);
        }
    }

    private static bool TryScrollWithUiAutomation(
        nint foregroundHandle,
        int scrollDelta,
        out string verification)
    {
        verification = string.Empty;
        try
        {
            var root = AutomationElement.FromHandle(foregroundHandle);
            var scroller = ResolveVerticalScroller(root);
            if (scroller is null ||
                !scroller.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) ||
                pattern is not ScrollPattern scroll ||
                !scroll.Current.VerticallyScrollable)
                return false;

            var before = scroll.Current.VerticalScrollPercent;
            var amount = scrollDelta > 0
                ? ScrollAmount.LargeDecrement
                : ScrollAmount.LargeIncrement;
            scroll.ScrollVertical(amount);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                Thread.Sleep(35);
                var after = scroll.Current.VerticalScrollPercent;
                if (IsVerticalScrollVerified(before, after, scrollDelta))
                {
                    verification = Math.Abs(after - before) >= 0.01
                        ? "uiaScrollVerified"
                        : scrollDelta > 0 ? "uiaScrollTopBoundary" : "uiaScrollBottomBoundary";
                    return true;
                }
            }
            throw new DesktopVisualException(
                "desktop_visual_scroll_unverified",
                "Windows 没有确认当前区域已经滚动。",
                true);
        }
        catch (DesktopVisualException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    private static AutomationElement? ResolveVerticalScroller(AutomationElement root)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused.Current.ProcessId == root.Current.ProcessId)
            {
                var current = focused;
                for (var depth = 0; depth < 16 && current is not null; depth++)
                {
                    if (HasVerticalScrollPattern(current)) return current;
                    current = TreeWalker.ControlViewWalker.GetParent(current);
                }
            }
            if (HasVerticalScrollPattern(root)) return root;
            var candidates = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
            AutomationElement? best = null;
            var bestArea = 0d;
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                var current = candidate.Current;
                var bounds = current.BoundingRectangle;
                if (current.IsOffscreen || !current.IsEnabled || !IsUsableBounds(bounds) ||
                    !HasVerticalScrollPattern(candidate)) continue;
                var area = bounds.Width * bounds.Height;
                if (area <= bestArea) continue;
                best = candidate;
                bestArea = area;
            }
            return best;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    private static bool HasVerticalScrollPattern(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) &&
                pattern is ScrollPattern scroll && scroll.Current.VerticallyScrollable;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return false;
        }
    }

    internal static bool IsVerticalScrollVerified(double before, double after, int scrollDelta)
    {
        if (!double.IsFinite(before) || !double.IsFinite(after) || scrollDelta == 0) return false;
        if (scrollDelta > 0)
            return after < before - 0.01 || before <= 0.01 && after <= 0.01;
        return after > before + 0.01 || before >= 99.99 && after >= 99.99;
    }

    private static string DispatchVerifiedMouseWheel(int scrollDelta)
    {
        SendInputs(MouseWheelInput(scrollDelta * WheelDelta));
        return "mouseWheelDispatched";
    }

    private static bool WaitForAutomationState(
        Func<bool> predicate,
        bool expected,
        bool elementUnavailableIsSuccess = false)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                if (predicate() == expected) return true;
            }
            catch (ElementNotAvailableException)
            {
                return elementUnavailableIsSuccess;
            }
            catch (COMException)
            {
                return elementUnavailableIsSuccess;
            }
            Thread.Sleep(35);
        }
        return false;
    }

    private TargetBinding TakeTargetBinding(Guid operationId, Guid targetToken)
    {
        var now = DateTimeOffset.UtcNow;
        PruneTargetBindings(now);
        if (!targetBindings.Remove(targetToken, out var binding) ||
            binding.OperationId != operationId || binding.ExpiresAtUtc < now)
            throw new DesktopVisualException("desktop_visual_target_binding_invalid", "截图绑定已经失效，请重新读取当前窗口。", true);
        return binding;
    }

    private void PruneTargetBindings(DateTimeOffset now)
    {
        foreach (var token in targetBindings
                     .Where(pair => pair.Value.ExpiresAtUtc < now)
                     .Select(pair => pair.Key)
                     .ToArray())
            targetBindings.Remove(token);
        while (targetBindings.Count >= 64)
        {
            var oldest = targetBindings.MinBy(pair => pair.Value.ExpiresAtUtc).Key;
            targetBindings.Remove(oldest);
        }
    }

    internal static void EnsureExpectedForeground(
        int actualProcessId,
        string actualProcessName,
        string actualWindowTitle,
        DesktopVisualExecuteActionParams expected)
    {
        if (actualProcessId != expected.ExpectedProcessId ||
            !StringComparer.OrdinalIgnoreCase.Equals(actualProcessName, expected.ExpectedProcessName) ||
            (expected.ExpectedWindowTitle.Length > 0 && !StringComparer.Ordinal.Equals(actualWindowTitle, expected.ExpectedWindowTitle)))
            throw new DesktopVisualException("desktop_visual_target_changed", "前台窗口在截图后发生变化，已取消桌面动作。", true);
    }

    private static void EnsureSameProcessAfterAction(ForegroundSource before, ForegroundSource after)
    {
        if (before.ProcessId != after.ProcessId ||
            !StringComparer.OrdinalIgnoreCase.Equals(before.ProcessName, after.ProcessName))
            throw new DesktopVisualException(
                "desktop_visual_target_changed_after_action",
                "动作后前台应用发生变化，已停止后续桌面动作且不会报告成功。",
                true);
    }

    private static POINT ReadWindowCenter(nint handle)
    {
        if (GetWindowRect(handle, out var rectangle) && rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top)
            return new POINT(
                rectangle.Left + ((rectangle.Right - rectangle.Left) / 2),
                rectangle.Top + ((rectangle.Bottom - rectangle.Top) / 2));
        if (GetCursorPos(out var cursor)) return cursor;
        return new POINT(0, 0);
    }

    private static void EnsureSafeDesktop(ForegroundSource source)
    {
        var process = source.ProcessName.ToLowerInvariant();
        var title = source.WindowTitle;
        if (process is "consent" or "credentialuibroker" or "logonui" or "winlogon" ||
            title.Contains("用户帐户控制", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("User Account Control", StringComparison.OrdinalIgnoreCase))
            throw new DesktopVisualException("desktop_visual_secure_desktop", "当前窗口是 UAC 或安全桌面，Velia 不会自动操作。", false);
    }

    private static ForegroundSource ReadForeground()
    {
        var handle = GetForegroundWindow();
        if (handle == 0) throw new DesktopVisualException("desktop_visual_foreground_unavailable", "没有可操作的前台窗口。", true);
        return ReadWindow(handle);
    }

    private static ForegroundSource ReadForegroundForBinding(string? preferredProcessName = null)
    {
        var foregroundHandle = GetForegroundWindow();
        if (foregroundHandle == 0)
            throw new DesktopVisualException("desktop_visual_foreground_unavailable", "没有可操作的前台窗口。", true);
        if (IsWindowVisible(foregroundHandle) && !IsIconic(foregroundHandle))
        {
            var visibleForeground = ReadWindow(foregroundHandle);
            if (!IsIgnoredBindingProcess(visibleForeground.ProcessName)) return visibleForeground;
        }

        _ = GetWindowThreadProcessId(foregroundHandle, out var hiddenProcessId);
        var candidates = new List<nint>();
        _ = EnumWindows((candidate, parameter) =>
        {
            if (IsWindowVisible(candidate) && !IsIconic(candidate))
            {
                _ = GetWindowThreadProcessId(candidate, out var candidateProcessId);
                if (candidateProcessId != 0 && candidateProcessId != hiddenProcessId &&
                    !string.IsNullOrWhiteSpace(ReadNativeWindowTitle(candidate)))
                {
                    candidates.Add(candidate);
                }
            }
            return true;
        }, 0);

        foreach (var candidate in candidates.Take(32))
        {
            var candidateSource = ReadWindow(candidate);
            if (IsIgnoredBindingProcess(candidateSource.ProcessName)) continue;
            if (preferredProcessName is not null &&
                !candidateSource.ProcessName.Equals(preferredProcessName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryActivateWindow(candidate, foregroundHandle)) continue;
            Thread.Sleep(80);
            var activated = ReadForeground();
            if (activated.ProcessId == candidateSource.ProcessId &&
                IsWindowVisible(activated.Handle) && !IsIconic(activated.Handle))
            {
                return activated;
            }
        }

        throw new DesktopVisualException(
            "desktop_visual_foreground_unavailable",
            "Velia 隐藏后没有找到可操作的前台窗口。",
            true);
    }

    private static ForegroundSource ReadWindow(nint handle)
    {
        _ = GetWindowThreadProcessId(handle, out var processIdValue);
        var processId = checked((int)processIdValue);
        string processName;
        string windowTitle;
        try
        {
            var process = Process.GetProcessById(processId);
            processName = process.ProcessName;
            windowTitle = ReadNativeWindowTitle(handle);
            if (windowTitle.Length == 0) windowTitle = process.MainWindowTitle.Trim();
        }
        catch (ArgumentException exception) { throw new DesktopVisualException("desktop_visual_foreground_unavailable", "前台应用已经关闭。", true, exception); }
        return new ForegroundSource(handle, processId, processName[..Math.Min(processName.Length, 128)], windowTitle[..Math.Min(windowTitle.Length, 512)]);
    }

    private static string ReadNativeWindowTitle(nint handle)
    {
        var length = Math.Min(512, Math.Max(0, GetWindowTextLength(handle)));
        if (length == 0) return string.Empty;
        var buffer = Marshal.AllocHGlobal((length + 1) * sizeof(char));
        try
        {
            var copied = GetWindowText(handle, buffer, length + 1);
            return copied <= 0 ? string.Empty : (Marshal.PtrToStringUni(buffer, copied) ?? string.Empty).Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryActivateWindow(nint candidate, nint currentForeground)
    {
        var currentThreadId = GetCurrentThreadId();
        var foregroundThreadId = GetWindowThreadProcessId(currentForeground, out _);
        var candidateThreadId = GetWindowThreadProcessId(candidate, out _);
        var attachedForeground = false;
        var attachedCandidate = false;
        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                attachedForeground = AttachThreadInput(currentThreadId, foregroundThreadId, true);
            if (candidateThreadId != 0 && candidateThreadId != currentThreadId && candidateThreadId != foregroundThreadId)
                attachedCandidate = AttachThreadInput(currentThreadId, candidateThreadId, true);
            _ = BringWindowToTop(candidate);
            _ = SetActiveWindow(candidate);
            return SetForegroundWindow(candidate);
        }
        finally
        {
            if (attachedCandidate) _ = AttachThreadInput(currentThreadId, candidateThreadId, false);
            if (attachedForeground) _ = AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }
    }

    private static bool IsIgnoredBindingProcess(string processName) =>
        StringComparer.OrdinalIgnoreCase.Equals(processName, "codex-computer-use") ||
        StringComparer.OrdinalIgnoreCase.Equals(processName, "Velia") ||
        StringComparer.OrdinalIgnoreCase.Equals(processName, "Velia.Core");

    private static INPUT[] MouseClick() =>
    [
        new(InputMouse, new MOUSEINPUT(0, 0, 0, MouseLeftDown, 0, 0)),
        new(InputMouse, new MOUSEINPUT(0, 0, 0, MouseLeftUp, 0, 0)),
    ];

    private static INPUT[] MouseButtonDown() =>
    [new(InputMouse, new MOUSEINPUT(0, 0, 0, MouseLeftDown, 0, 0))];

    private static INPUT[] MouseButtonUp() =>
    [new(InputMouse, new MOUSEINPUT(0, 0, 0, MouseLeftUp, 0, 0))];

    private static INPUT[] MouseWheelInput(int delta) =>
    [new(InputMouse, new MOUSEINPUT(0, 0, unchecked((uint)delta), MouseWheel, 0, 0))];

    private static INPUT[] UnicodeText(string text)
    {
        var inputs = new INPUT[text.Length * 2];
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            inputs[index * 2] = new(InputKeyboard, new KEYBDINPUT(0, value, KeyboardUnicode, 0, 0));
            inputs[(index * 2) + 1] = new(InputKeyboard, new KEYBDINPUT(0, value, KeyboardUnicode | KeyboardKeyUp, 0, 0));
        }
        return inputs;
    }

    private static INPUT[] KeyPress(string key)
    {
        var virtualKey = key switch
        {
            DesktopVisualKeys.Enter => 0x0d,
            DesktopVisualKeys.Escape => 0x1b,
            DesktopVisualKeys.Tab => 0x09,
            DesktopVisualKeys.Space => 0x20,
            DesktopVisualKeys.Backspace => 0x08,
            DesktopVisualKeys.ArrowUp => 0x26,
            DesktopVisualKeys.ArrowDown => 0x28,
            DesktopVisualKeys.ArrowLeft => 0x25,
            DesktopVisualKeys.ArrowRight => 0x27,
            _ => throw new DesktopVisualException("desktop_visual_key_not_allowed", "桌面视觉按键不在安全白名单中。"),
        };
        return
        [
            new(InputKeyboard, new KEYBDINPUT((ushort)virtualKey, 0, 0, 0, 0)),
            new(InputKeyboard, new KEYBDINPUT((ushort)virtualKey, 0, KeyboardKeyUp, 0, 0)),
        ];
    }

    private static INPUT[] KeyPressVirtualKey(ushort virtualKey) =>
    [
        new(InputKeyboard, new KEYBDINPUT(virtualKey, 0, 0, 0, 0)),
        new(InputKeyboard, new KEYBDINPUT(virtualKey, 0, KeyboardKeyUp, 0, 0)),
    ];

    private static INPUT[] ModifiedKeyPress(ushort modifier, ushort key) =>
    [
        new(InputKeyboard, new KEYBDINPUT(modifier, 0, 0, 0, 0)),
        new(InputKeyboard, new KEYBDINPUT(key, 0, 0, 0, 0)),
        new(InputKeyboard, new KEYBDINPUT(key, 0, KeyboardKeyUp, 0, 0)),
        new(InputKeyboard, new KEYBDINPUT(modifier, 0, KeyboardKeyUp, 0, 0)),
    ];

    private static bool WaitForClipboardChange(
        uint previousSequence,
        int maximumWaitMilliseconds,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, maximumWaitMilliseconds / 20);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetClipboardSequenceNumber() != previousSequence) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static void SendInputs(INPUT[] inputs)
    {
        if (inputs.Length is 0 or > 2_100 || SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw NativeFailure("desktop_visual_input_failed", "Windows 拒绝了桌面输入动作。", true);
    }

    private static DesktopVisualException NativeFailure(string code, string message, bool retryable) =>
        new(code, message, retryable, new Win32Exception(Marshal.GetLastWin32Error()));

    private sealed record ForegroundSource(nint Handle, int ProcessId, string ProcessName, string WindowTitle);
    private sealed record TargetBinding(Guid OperationId, ForegroundSource Source, DateTimeOffset ExpiresAtUtc);
    private sealed record EditableCandidate(Rect Bounds, AutomationElement Element);
    internal sealed record AccessibleInputDescriptor(
        string Name,
        string AutomationId,
        string HelpText,
        string ControlTypeName,
        bool HasKeyboardFocus,
        double X,
        double Y,
        double Width,
        double Height)
    {
        public double Area => Math.Max(0, Width) * Math.Max(0, Height);
    }

    private sealed record InputCandidate(
        AccessibleInputDescriptor Descriptor,
        AutomationElement Element);

    internal readonly record struct MessagingLayoutPoint(int X, int Y);

    internal sealed record MessagingMutationSample(int Width, int Height, byte[] Pixels);

    private sealed record PendingTextSubmission(
        nint WindowHandle,
        int ProcessId,
        string ProcessName,
        string WindowTitle,
        string Target,
        string Text,
        string VerifiedText,
        DateTimeOffset ExpiresAtUtc,
        bool VisualOnly);
    internal sealed record AccessibleControlDescriptor(
        string Name,
        string AutomationId,
        string HelpText,
        string AccessKey,
        string ControlTypeName)
    {
        public bool HasIdentity =>
            !string.IsNullOrWhiteSpace(Name) ||
            !string.IsNullOrWhiteSpace(AutomationId) ||
            !string.IsNullOrWhiteSpace(HelpText) ||
            !string.IsNullOrWhiteSpace(AccessKey);
    }

    private sealed record AutomationCandidate(
        AccessibleControlDescriptor Descriptor,
        ControlType ControlType,
        Rect Bounds,
        AutomationElement Element,
        bool IsOffscreen);
    private sealed record ResolvedActionTarget(POINT Point, AutomationElement? AutomationElement);

    private readonly record struct POINT(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT(int left, int top, int right, int bottom)
    {
        public readonly int Left = left;
        public readonly int Top = top;
        public readonly int Right = right;
        public readonly int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MOUSEINPUT Mouse;
        [FieldOffset(8)] public KEYBDINPUT Keyboard;

        public INPUT(uint type, MOUSEINPUT mouse)
        {
            Type = type;
            Mouse = mouse;
            Keyboard = default;
        }

        public INPUT(uint type, KEYBDINPUT keyboard)
        {
            Type = type;
            Mouse = default;
            Keyboard = keyboard;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MOUSEINPUT(int dx, int dy, uint mouseData, uint flags, uint time, nint extraInfo)
    {
        public readonly int Dx = dx;
        public readonly int Dy = dy;
        public readonly uint MouseData = mouseData;
        public readonly uint Flags = flags;
        public readonly uint Time = time;
        public readonly nint ExtraInfo = extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KEYBDINPUT(ushort virtualKey, ushort scanCode, uint flags, uint time, nint extraInfo)
    {
        public readonly ushort VirtualKey = virtualKey;
        public readonly ushort ScanCode = scanCode;
        public readonly uint Flags = flags;
        public readonly uint Time = time;
        public readonly nint ExtraInfo = extraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, nint text, int maximumCount);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint sourceThread, uint targetThread, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out RECT rectangle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wordParameter, nint longParameter);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageText(nint window, uint message, nint wordParameter, string longParameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint inputCount, INPUT[] inputs, int inputSize);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}

internal sealed class DesktopVisualException(
    string code,
    string message,
    bool retryable = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
