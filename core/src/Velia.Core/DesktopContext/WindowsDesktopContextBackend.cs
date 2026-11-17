using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Velia.Protocol;

namespace Velia.Core.DesktopContext;

internal sealed class WindowsDesktopContextBackend : IDesktopContextBackend
{
    private const int MaximumTextCharacters = 32_768;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyAlt = 0x12;
    private const int VirtualKeySpace = 0x20;
    private const int VirtualKeyLeft = 0x25;
    private const int VirtualKeyL = 0x4c;
    private const int VirtualKeyF8 = 0x77;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const string CanIncludeInClipboardHistory = "CanIncludeInClipboardHistory";
    private const string ExcludeClipboardContentFromMonitorProcessing = "ExcludeClipboardContentFromMonitorProcessing";
    internal static int NativeInputSize { get; } = Marshal.SizeOf<Input>();

    public async Task<DesktopContextObservation> CaptureAsync(string mode, CancellationToken cancellationToken)
    {
        var source = ReadForeground();
        if (mode == DesktopContextModes.Clipboard)
        {
            var clipboardText = RunSta(ReadClipboardText);
            return Observation(source, DesktopContextKinds.ClipboardText, true, clipboardText);
        }

        var snapshot = RunSta(CaptureClipboard);
        var snapshotSequence = GetClipboardSequenceNumber();
        if (snapshotSequence != snapshot.SequenceNumber)
            throw new DesktopContextException("clipboard_changed", "读取前剪贴板内容发生变化，请重试。", true);
        DesktopPayload payload = new(null, null);
        var restored = false;
        Exception? captureFailure = null;
        try
        {
            await WaitForShortcutKeysReleasedAsync(cancellationToken).ConfigureAwait(false);
            EnsureCaptureSourceUnchanged(ReadForeground(), source);
            var before = RunSta(SetClipboardProbe);
            SendCopyShortcut();
            var changed = await WaitForClipboardChangeAsync(before, cancellationToken).ConfigureAwait(false);
            if (changed)
            {
                await WaitForClipboardStableAsync(cancellationToken).ConfigureAwait(false);
                payload = RunSta(ReadClipboardPayload);
            }
        }
        catch (Exception exception)
        {
            captureFailure = exception;
        }
        finally
        {
            restored = await TryRestoreClipboardAsync(snapshot).ConfigureAwait(false);
        }
        if (!restored)
            throw new DesktopContextException("clipboard_restore_failed", "未能验证剪贴板恢复，已停止读取上下文。");
        if (captureFailure is not null) ExceptionDispatchInfo.Capture(captureFailure).Throw();
        if (GetForegroundWindow() != source.Handle)
            throw new DesktopContextException("foreground_changed", "读取期间前台窗口发生变化，请重新选择后再试。");
        if (payload.Files is { Count: > 0 })
            return Observation(source, DesktopContextKinds.SelectedFiles, true, files: payload.Files);
        if (!string.IsNullOrWhiteSpace(payload.Text))
            return Observation(source, DesktopContextKinds.SelectedText, true, payload.Text);
        if (mode == DesktopContextModes.Auto && !string.IsNullOrWhiteSpace(snapshot.Text))
            return Observation(source, DesktopContextKinds.ClipboardText, true, snapshot.Text);
        return Observation(source, DesktopContextKinds.None, true);
    }

    public async Task<DesktopContextReplaceObservation> ReplaceSelectionAsync(
        DesktopContextTarget target,
        string text,
        CancellationToken cancellationToken)
    {
        var source = ReadForeground();
        EnsureTargetUnchanged(source, target);
        if (target.ControlHandle == 0 || string.IsNullOrEmpty(target.Text) || target.Kind != DesktopContextKinds.SelectedText)
            throw new DesktopContextException("context_not_replaceable", "原选中文字没有可验证的输入目标。");

        var snapshot = RunSta(CaptureClipboard);
        if (GetClipboardSequenceNumber() != snapshot.SequenceNumber)
            throw new DesktopContextException("clipboard_changed", "替换前剪贴板内容发生变化，请重试。", true);
        var restored = false;
        string? copied = null;
        Exception? replaceFailure = null;
        try
        {
            await WaitForShortcutKeysReleasedAsync(cancellationToken).ConfigureAwait(false);
            EnsureTargetUnchanged(ReadForeground(), target);
            RunSta(() => SetPrivateClipboardText(text));
            EnsureTargetUnchanged(ReadForeground(), target);
            SendPasteShortcut();
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            EnsureTargetUnchanged(ReadForeground(), target);

            SelectPreviousText(text, cancellationToken);
            await Task.Delay(75, cancellationToken).ConfigureAwait(false);
            EnsureTargetUnchanged(ReadForeground(), target);
            var beforeVerificationCopy = RunSta(SetClipboardProbe);
            SendCopyShortcut();
            if (!await WaitForClipboardChangeAsync(beforeVerificationCopy, cancellationToken).ConfigureAwait(false))
                throw new DesktopContextException("write_verification_failed", "写入后无法重新读取选中文字，不能确认替换成功。");
            await WaitForClipboardStableAsync(cancellationToken).ConfigureAwait(false);
            copied = RunSta(ReadClipboardText);
        }
        catch (Exception exception)
        {
            replaceFailure = exception;
        }
        finally
        {
            restored = await TryRestoreClipboardAsync(snapshot).ConfigureAwait(false);
        }
        if (!restored)
            throw new DesktopContextException("clipboard_restore_failed", "替换后未能验证剪贴板恢复，请先检查剪贴板内容。");
        if (replaceFailure is not null) ExceptionDispatchInfo.Capture(replaceFailure).Throw();

        if (copied is null || !StringComparer.Ordinal.Equals(NormalizeLineEndings(copied), NormalizeLineEndings(text)))
            throw new DesktopContextException("write_verification_failed", "写入后的文字与目标内容不一致，不能确认替换成功。");
        return new DesktopContextReplaceObservation(
            new DesktopContextSourceResult(source.ProcessName, source.Title),
            true);
    }

    private static DesktopContextObservation Observation(
        ForegroundSource source, string kind, bool restored, string? text = null, IReadOnlyList<string>? files = null)
    {
        if (text?.Length > MaximumTextCharacters)
            throw new DesktopContextException("desktop_context_too_large", "选中或剪贴板文字超过 32768 个字符，请缩小范围后重试。");
        return new(kind,
            new DesktopContextSourceResult(source.ProcessName, source.Title),
            restored, source.Handle, source.FocusHandle, source.ProcessId,
            text, files);
    }

    private static void EnsureTargetUnchanged(ForegroundSource source, DesktopContextTarget target)
    {
        if (source.Handle != target.WindowHandle || source.FocusHandle != target.ControlHandle || source.ProcessId != target.ProcessId)
            throw new DesktopContextException("desktop_target_changed", "原窗口或输入位置已经变化，已取消替换。");
    }

    private static void EnsureCaptureSourceUnchanged(ForegroundSource current, ForegroundSource expected)
    {
        if (current.Handle != expected.Handle || current.FocusHandle != expected.FocusHandle || current.ProcessId != expected.ProcessId)
            throw new DesktopContextException("foreground_changed", "快捷键松开前窗口或输入位置发生变化，请重新选择后再试。", true);
    }

    private static ForegroundSource ReadForeground()
    {
        var handle = GetForegroundWindow();
        if (handle == 0) throw new DesktopContextException("foreground_unavailable", "没有可读取的前台窗口。");
        var threadId = GetWindowThreadProcessId(handle, out var processIdValue);
        var processId = checked((int)processIdValue);
        string processName;
        try { processName = Process.GetProcessById(processId).ProcessName; }
        catch (ArgumentException) { throw new DesktopContextException("foreground_unavailable", "前台应用已经关闭。"); }
        var length = Math.Min(GetWindowTextLength(handle), 512);
        var buffer = new char[length + 1];
        _ = GetWindowText(handle, buffer, buffer.Length);
        var threadInfo = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var focusHandle = GetGUIThreadInfo(threadId, ref threadInfo) ? threadInfo.Focus : 0;
        return new ForegroundSource(
            handle,
            focusHandle,
            processId,
            processName[..Math.Min(processName.Length, 128)],
            new string(buffer).TrimEnd('\0'));
    }

    private static ClipboardSnapshot CaptureClipboard()
    {
        var sequenceBefore = GetClipboardSequenceNumber();
        var source = Clipboard.GetDataObject();
        var clone = new DataObject();
        var formats = source?.GetFormats(false) ?? [];
        foreach (var format in formats)
        {
            try
            {
                var value = source!.GetData(format, false);
                if (value is not null) clone.SetData(format, false, CloneClipboardValue(value));
            }
            catch (DesktopContextException) { throw; }
            catch (Exception exception) when (exception is ExternalException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                throw new DesktopContextException(
                    "clipboard_snapshot_unsafe",
                    "当前剪贴板包含无法安全保存的格式，请先复制一段普通文字后重试。");
            }
        }
        var text = ReadClipboardText();
        var sequenceAfter = GetClipboardSequenceNumber();
        if (sequenceBefore != sequenceAfter)
            throw new DesktopContextException("clipboard_changed", "保存剪贴板期间内容发生变化，请重试。", true);
        return new ClipboardSnapshot(clone, formats.Order(StringComparer.Ordinal).ToArray(), text, sequenceAfter);
    }

    private static bool RestoreClipboard(ClipboardSnapshot snapshot)
    {
        if (snapshot.Formats.Length == 0) Clipboard.Clear();
        else Clipboard.SetDataObject(snapshot.Data, true, 5, 20);
        return ClipboardMatchesSnapshot(snapshot);
    }

    private static bool ClipboardMatchesSnapshot(ClipboardSnapshot snapshot)
    {
        var restored = Clipboard.GetDataObject();
        var formats = (restored?.GetFormats(false) ?? []).Order(StringComparer.Ordinal).ToArray();
        return ClipboardFormatsMatch(snapshot.Formats, formats, Clipboard.ContainsImage()) &&
            StringComparer.Ordinal.Equals(ReadClipboardText(), snapshot.Text);
    }

    internal static bool ClipboardFormatsMatch(
        IReadOnlyCollection<string> expected,
        IReadOnlyCollection<string> restored,
        bool restoredContainsImage)
    {
        var restoredSet = restored.ToHashSet(StringComparer.Ordinal);
        var expectsImage = false;
        foreach (var format in expected)
        {
            if (IsImageClipboardFormat(format))
            {
                expectsImage = true;
                continue;
            }
            if (!restoredSet.Contains(format)) return false;
        }
        return !expectsImage || restoredContainsImage || restored.Any(IsImageClipboardFormat);
    }

    private static bool IsImageClipboardFormat(string format) =>
        StringComparer.Ordinal.Equals(format, DataFormats.Bitmap) ||
        StringComparer.Ordinal.Equals(format, DataFormats.Dib) ||
        StringComparer.Ordinal.Equals(format, "System.Drawing.Bitmap") ||
        StringComparer.Ordinal.Equals(format, "Format17");

    private static bool TryRestoreClipboard(ClipboardSnapshot snapshot)
    {
        try { return RestoreClipboard(snapshot); }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> TryRestoreClipboardAsync(ClipboardSnapshot snapshot)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var restored = RunSta(() => TryRestoreClipboard(snapshot));
            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            if (restored && RunSta(() => ClipboardMatchesSnapshot(snapshot))) return true;
        }
        return false;
    }

    private static object CloneClipboardValue(object value) => value switch
    {
        string text => text,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or char or float or double or decimal => value,
        string[] paths => paths.ToArray(),
        StringCollection paths => paths.Cast<string>().ToArray(),
        byte[] bytes => bytes.ToArray(),
        MemoryStream stream => new MemoryStream(stream.ToArray(), writable: false),
        Stream stream when stream.CanRead => CopyStream(stream),
        System.Drawing.Bitmap bitmap => new System.Drawing.Bitmap(bitmap),
        System.Drawing.Image image => new System.Drawing.Bitmap(image),
        _ => throw new DesktopContextException(
            "clipboard_snapshot_unsafe",
            "当前剪贴板包含无法安全保存的格式，请先复制一段普通文字后重试。"),
    };

    private static MemoryStream CopyStream(Stream source)
    {
        var position = source.CanSeek ? source.Position : (long?)null;
        try
        {
            if (source.CanSeek) source.Position = 0;
            var copy = new MemoryStream();
            source.CopyTo(copy);
            copy.Position = 0;
            return copy;
        }
        finally
        {
            if (position is not null) source.Position = position.Value;
        }
    }

    private static string? ReadClipboardText() =>
        Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : null;

    private static bool SetPrivateClipboardText(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, false, text);
        data.SetData(CanIncludeInClipboardHistory, false, new MemoryStream(BitConverter.GetBytes(0u), writable: false));
        data.SetData(ExcludeClipboardContentFromMonitorProcessing, false, new MemoryStream(BitConverter.GetBytes(1u), writable: false));
        Clipboard.SetDataObject(data, true, 5, 20);
        if (!StringComparer.Ordinal.Equals(ReadClipboardText(), text))
            throw new DesktopContextException("clipboard_write_failed", "无法准备经过验证的替换文字。");
        return true;
    }

    private static uint SetClipboardProbe()
    {
        var probe = $"velia-private-probe:{Guid.NewGuid():N}";
        SetPrivateClipboardText(probe);
        var sequence = GetClipboardSequenceNumber();
        if (sequence == 0)
            throw new DesktopContextException("clipboard_write_failed", "无法建立安全的剪贴板读取探针。");
        return sequence;
    }

    private static DesktopPayload ReadClipboardPayload()
    {
        IReadOnlyList<string>? files = null;
        if (Clipboard.ContainsFileDropList())
        {
            StringCollection collection = Clipboard.GetFileDropList();
            files = collection.Cast<string>().Where(path => !string.IsNullOrWhiteSpace(path)).Take(32).ToArray();
        }
        return new DesktopPayload(ReadClipboardText(), files);
    }

    private static async Task<bool> WaitForClipboardChangeAsync(uint before, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            if (GetClipboardSequenceNumber() != before) return true;
        }
        return false;
    }

    private static async Task WaitForClipboardStableAsync(CancellationToken cancellationToken)
    {
        var observed = GetClipboardSequenceNumber();
        var stableSamples = 0;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            var current = GetClipboardSequenceNumber();
            if (current == observed)
            {
                stableSamples++;
                if (stableSamples >= 3) return;
            }
            else
            {
                observed = current;
                stableSamples = 0;
            }
        }
        throw new DesktopContextException(
            "clipboard_unstable",
            "目标应用仍在更新剪贴板，请稍后重试。",
            true);
    }

    private static async Task WaitForShortcutKeysReleasedAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsShortcutKeyDown()) return;
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }
        throw new DesktopContextException(
            "shortcut_keys_still_pressed",
            "语音快捷键尚未完全松开，请松开按键后重试。",
            true);
    }

    private static bool IsShortcutKeyDown() =>
        IsKeyDown(VirtualKeyControl) || IsKeyDown(VirtualKeyShift) || IsKeyDown(VirtualKeyAlt) ||
        IsKeyDown(VirtualKeySpace) || IsKeyDown(VirtualKeyL) || IsKeyDown(VirtualKeyF8);

    private static bool IsKeyDown(int virtualKey) =>
        (Velia.Core.Input.NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static void SendCopyShortcut() => SendChord(0x11, 0x43);

    private static void SendPasteShortcut() => SendChord(0x11, 0x56);

    private static void SendChord(ushort modifier, ushort key)
    {
        var inputs = new[]
        {
            KeyInput(modifier, false), KeyInput(key, false), KeyInput(key, true), KeyInput(modifier, true),
        };
        SendKeyInputs(inputs);
    }

    private static void SelectPreviousText(string text, CancellationToken cancellationToken)
    {
        var elements = StringInfo.GetTextElementEnumerator(NormalizeLineEndings(text));
        var count = 0;
        while (elements.MoveNext()) count++;
        if (count == 0) throw new DesktopContextException("invalid_replacement_text", "替换文字不能为空。");

        SendKeyInputs([KeyInput(0x10, false)]);
        try
        {
            var remaining = count;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = Math.Min(remaining, 256);
                var inputs = new Input[current * 2];
                for (var index = 0; index < current; index++)
                {
                    inputs[index * 2] = KeyInput(VirtualKeyLeft, false);
                    inputs[(index * 2) + 1] = KeyInput(VirtualKeyLeft, true);
                }
                SendKeyInputs(inputs);
                remaining -= current;
            }
        }
        finally
        {
            SendKeyInputs([KeyInput(0x10, true)]);
        }
    }

    private static void SendKeyInputs(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, NativeInputSize) != (uint)inputs.Length)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new DesktopContextException(
                "keyboard_input_failed",
                $"无法向原窗口发送受控键盘操作（Windows 错误 {error}）。");
        }
    }

    private static string NormalizeLineEndings(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

    internal static uint KeyboardInputFlags(ushort key, bool up) =>
        (key == VirtualKeyLeft ? KeyEventExtendedKey : 0u) |
        (up ? KeyEventKeyUp : 0u);

    private static Input KeyInput(ushort key, bool up) => new()
    {
        Type = 1,
        Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = KeyboardInputFlags(key, up) } },
    };

    private static T RunSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() => { try { result = action(); } catch (Exception exception) { failure = exception; } })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
        return result!;
    }

    private sealed record ForegroundSource(nint Handle, nint FocusHandle, int ProcessId, string ProcessName, string Title);
    private sealed record ClipboardSnapshot(DataObject Data, string[] Formats, string? Text, uint SequenceNumber);
    private sealed record DesktopPayload(string? Text, IReadOnlyList<string>? Files);

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect
    { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public Rect CaretRect;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, char[] text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
