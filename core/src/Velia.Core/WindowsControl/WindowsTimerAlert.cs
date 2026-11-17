namespace Velia.Core.WindowsControl;

internal sealed class WindowsTimerAlert : IWindowsTimerAlert
{
    public void Play()
    {
        try
        {
            Console.Beep(880, 180);
            Console.Beep(1_100, 180);
            Console.Beep(1_320, 320);
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
        {
            // The bounded Core notification still provides a visible alert when a
            // sound device or the Windows beep service is unavailable.
        }
    }
}
