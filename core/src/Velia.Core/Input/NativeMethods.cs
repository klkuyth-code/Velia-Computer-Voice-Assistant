using System.Runtime.InteropServices;

namespace Velia.Core.Input;

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);
}
