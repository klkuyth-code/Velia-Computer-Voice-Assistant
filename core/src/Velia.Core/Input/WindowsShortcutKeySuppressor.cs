using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Velia.Core.Input;

internal sealed class WindowsShortcutKeySuppressor : IDisposable
{
    private const int LowLevelKeyboardHook = 13;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyAlt = 0x12;
    private const int VirtualKeySpace = 0x20;
    private const uint KeyDown = 0x0100;
    private const uint KeyUp = 0x0101;
    private const uint SystemKeyDown = 0x0104;
    private const uint SystemKeyUp = 0x0105;
    private const uint Quit = 0x0012;

    private readonly Func<bool> isEnabled;
    private readonly ManualResetEventSlim started = new(false);
    private readonly ShortcutKeySuppressionState suppression = new();
    private readonly LongConversationShortcutState longConversation = new();
    private readonly ConcurrentQueue<PhysicalKeyEdge> shortcutEdges = new();
    private readonly ConcurrentQueue<PhysicalKeyEdge> longConversationEdges = new();
    private readonly HookProcedure hookProcedure;
    private Thread? thread;
    private nint hook;
    private uint threadId;
    private Exception? startupFailure;

    public bool IsShortcutDown => suppression.IsShortcutDown;

    public PhysicalKeyEdge? ReadShortcutEdge() =>
        shortcutEdges.TryDequeue(out var edge) ? edge : null;

    public PhysicalKeyEdge? ReadLongConversationEdge() =>
        longConversationEdges.TryDequeue(out var edge) ? edge : null;

    public WindowsShortcutKeySuppressor(Func<bool> isEnabled)
    {
        this.isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        hookProcedure = HookCallback;
    }

    public void Start()
    {
        if (thread is not null) throw new InvalidOperationException("Shortcut suppression has already started.");
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Velia shortcut suppression",
        };
        thread.Start();
        if (!started.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("Timed out while starting shortcut suppression.");
        if (startupFailure is not null) throw new InvalidOperationException("Could not install shortcut suppression.", startupFailure);
    }

    public void Dispose()
    {
        var running = thread;
        thread = null;
        if (running is not null && threadId != 0)
        {
            _ = PostThreadMessage(threadId, Quit, 0, 0);
            _ = running.Join(TimeSpan.FromSeconds(2));
        }
        started.Dispose();
    }

    private void Run()
    {
        try
        {
            threadId = GetCurrentThreadId();
            _ = PeekMessage(out _, 0, 0, 0, 0);
            hook = SetWindowsHookEx(LowLevelKeyboardHook, hookProcedure, GetModuleHandle(null), 0);
            if (hook == 0)
                throw new InvalidOperationException($"SetWindowsHookEx failed with Windows error {Marshal.GetLastPInvokeError()}.");
            started.Set();
            while (true)
            {
                var result = GetMessage(out _, 0, 0, 0);
                if (result > 0) continue;
                if (result == 0) break;
                throw new InvalidOperationException($"GetMessage failed with Windows error {Marshal.GetLastPInvokeError()}.");
            }
        }
        catch (Exception exception)
        {
            startupFailure = exception;
            started.Set();
        }
        finally
        {
            if (hook != 0) _ = UnhookWindowsHookEx(hook);
            hook = 0;
        }
    }

    private nint HookCallback(int code, nuint message, nint data)
    {
        var messageId = checked((uint)message);
        if (code >= 0 && messageId is KeyDown or KeyUp or SystemKeyDown or SystemKeyUp)
        {
            var input = Marshal.PtrToStructure<LowLevelKeyboardInput>(data);
            var virtualKey = checked((int)input.VirtualKey);
            var isKeyUp = messageId is KeyUp or SystemKeyUp;
            var wasDown = suppression.IsShortcutDown;
            var shouldSuppress = suppression.ShouldSuppress(
                    virtualKey,
                    isKeyUp,
                    IsKeyDown(VirtualKeyControl),
                    IsKeyDown(VirtualKeyShift),
                    isEnabled());
            var isDown = suppression.IsShortcutDown;
            if (wasDown != isDown)
                shortcutEdges.Enqueue(isDown ? PhysicalKeyEdge.Pressed : PhysicalKeyEdge.Released);
            if (LongConversationShortcutState.IsRelevantKey(virtualKey) &&
                longConversation.Sample(
                    virtualKey,
                    isKeyUp,
                    IsKeyDown(VirtualKeyControl),
                    IsKeyDown(VirtualKeyAlt)) is { } longConversationEdge)
                longConversationEdges.Enqueue(longConversationEdge);
            if (shouldSuppress) return 1;
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProcedure(int code, nuint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, HookProcedure procedure, nint module, uint threadId);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(
        out NativeMessage message,
        nint window,
        uint minimum,
        uint maximum,
        uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint word, nint value);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint Word;
        public nint Value;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }
}

internal sealed class LongConversationShortcutState
{
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyAlt = 0x12;
    private const int VirtualKeyL = 0x4c;
    private const int VirtualKeyLeftControl = 0xa2;
    private const int VirtualKeyRightControl = 0xa3;
    private const int VirtualKeyLeftAlt = 0xa4;
    private const int VirtualKeyRightAlt = 0xa5;
    private bool shortcutDown;
    private int controlKeysDown;
    private int altKeysDown;

    public static bool IsRelevantKey(int virtualKey) => virtualKey is
        VirtualKeyControl or VirtualKeyAlt or VirtualKeyL or
        VirtualKeyLeftControl or VirtualKeyRightControl or
        VirtualKeyLeftAlt or VirtualKeyRightAlt;

    public PhysicalKeyEdge? Sample(
        int virtualKey,
        bool isKeyUp,
        bool controlDown,
        bool altDown)
    {
        UpdateModifierState(virtualKey, isKeyUp);
        if (isKeyUp && shortcutDown && IsChordModifier(virtualKey))
        {
            shortcutDown = false;
            return PhysicalKeyEdge.Released;
        }
        if (virtualKey != VirtualKeyL) return null;
        if (!isKeyUp)
        {
            if (shortcutDown) return null;
            if (!(controlDown || controlKeysDown != 0) || !(altDown || altKeysDown != 0)) return null;
            shortcutDown = true;
            return PhysicalKeyEdge.Pressed;
        }
        if (!shortcutDown) return null;
        shortcutDown = false;
        return PhysicalKeyEdge.Released;
    }

    private static bool IsChordModifier(int virtualKey) => virtualKey is
        VirtualKeyControl or VirtualKeyAlt or
        VirtualKeyLeftControl or VirtualKeyRightControl or
        VirtualKeyLeftAlt or VirtualKeyRightAlt;

    private void UpdateModifierState(int virtualKey, bool isKeyUp)
    {
        var controlBit = virtualKey switch
        {
            VirtualKeyControl => 1,
            VirtualKeyLeftControl => 2,
            VirtualKeyRightControl => 4,
            _ => 0,
        };
        var altBit = virtualKey switch
        {
            VirtualKeyAlt => 1,
            VirtualKeyLeftAlt => 2,
            VirtualKeyRightAlt => 4,
            _ => 0,
        };
        if (controlBit != 0)
            controlKeysDown = isKeyUp ? controlKeysDown & ~controlBit : controlKeysDown | controlBit;
        if (altBit != 0)
            altKeysDown = isKeyUp ? altKeysDown & ~altBit : altKeysDown | altBit;
    }
}

internal sealed class ShortcutKeySuppressionState
{
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeySpace = 0x20;
    private const int VirtualKeyLeftShift = 0xa0;
    private const int VirtualKeyRightShift = 0xa1;
    private const int VirtualKeyLeftControl = 0xa2;
    private const int VirtualKeyRightControl = 0xa3;
    private bool suppressingSpace;
    private int shortcutDown;
    private int controlKeysDown;
    private int shiftKeysDown;

    public bool IsShortcutDown => Volatile.Read(ref shortcutDown) != 0;

    public bool ShouldSuppress(int virtualKey, bool isKeyUp, bool controlDown, bool shiftDown, bool enabled)
    {
        UpdateModifierState(virtualKey, isKeyUp);
        if (isKeyUp && suppressingSpace && IsChordModifier(virtualKey))
        {
            Volatile.Write(ref shortcutDown, 0);
            return false;
        }
        if (virtualKey != VirtualKeySpace) return false;
        if (!isKeyUp && suppressingSpace) return true;
        var controlIsDown = controlDown || controlKeysDown != 0;
        var shiftIsDown = shiftDown || shiftKeysDown != 0;
        if (!isKeyUp && enabled && controlIsDown && shiftIsDown)
        {
            suppressingSpace = true;
            Volatile.Write(ref shortcutDown, 1);
            return true;
        }
        if (isKeyUp && suppressingSpace)
        {
            suppressingSpace = false;
            Volatile.Write(ref shortcutDown, 0);
            return true;
        }
        return false;
    }

    private static bool IsChordModifier(int virtualKey) => virtualKey is
        VirtualKeyControl or VirtualKeyShift or
        VirtualKeyLeftControl or VirtualKeyRightControl or
        VirtualKeyLeftShift or VirtualKeyRightShift;

    private void UpdateModifierState(int virtualKey, bool isKeyUp)
    {
        var controlBit = virtualKey switch
        {
            VirtualKeyControl => 1,
            VirtualKeyLeftControl => 2,
            VirtualKeyRightControl => 4,
            _ => 0,
        };
        var shiftBit = virtualKey switch
        {
            VirtualKeyShift => 1,
            VirtualKeyLeftShift => 2,
            VirtualKeyRightShift => 4,
            _ => 0,
        };
        if (controlBit != 0)
            controlKeysDown = isKeyUp ? controlKeysDown & ~controlBit : controlKeysDown | controlBit;
        if (shiftBit != 0)
            shiftKeysDown = isKeyUp ? shiftKeysDown & ~shiftBit : shiftKeysDown | shiftBit;
    }
}
