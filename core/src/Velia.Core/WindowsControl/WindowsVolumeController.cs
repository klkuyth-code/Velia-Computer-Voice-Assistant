using System.Runtime.InteropServices;

namespace Velia.Core.WindowsControl;

internal sealed class WindowsVolumeController : IWindowsVolumeController
{
    private readonly object gate = new();

    public WindowsControlResult Set(int percentage)
    {
        if (percentage is < 0 or > 100)
            throw new WindowsControlException("invalid_volume", "音量必须在 0 到 100 之间。" );
        lock (gate)
        {
            return WithEndpoint(endpoint =>
            {
                ThrowIfFailed(endpoint.SetMasterVolumeLevelScalar(percentage / 100f, Guid.Empty));
                var state = Read(endpoint);
                if (Math.Abs(state.Percentage - percentage) > 1)
                    throw new WindowsControlException("volume_verification_failed", "系统音量没有通过写后验证。", true);
                return new WindowsControlResult(WindowsControlActions.SetVolume, true, "volumeSet", VolumePercent: state.Percentage, Muted: state.Muted);
            });
        }
    }

    public WindowsControlResult Adjust(int delta)
    {
        if (delta is 0 or < -100 or > 100)
            throw new WindowsControlException("invalid_volume_adjustment", "音量调整值必须在 -100 到 100 之间且不能为 0。" );
        lock (gate)
        {
            return WithEndpoint(endpoint =>
            {
                var before = Read(endpoint);
                var target = Math.Clamp(before.Percentage + delta, 0, 100);
                ThrowIfFailed(endpoint.SetMasterVolumeLevelScalar(target / 100f, Guid.Empty));
                var after = Read(endpoint);
                if (Math.Abs(after.Percentage - target) > 1)
                    throw new WindowsControlException("volume_verification_failed", "系统音量没有通过写后验证。", true);
                return new WindowsControlResult(WindowsControlActions.AdjustVolume, true, "volumeAdjusted", VolumePercent: after.Percentage, Muted: after.Muted);
            });
        }
    }

    public WindowsControlResult SetMuted(bool muted)
    {
        lock (gate)
        {
            return WithEndpoint(endpoint =>
            {
                ThrowIfFailed(endpoint.SetMute(muted, Guid.Empty));
                var state = Read(endpoint);
                if (state.Muted != muted)
                    throw new WindowsControlException("volume_verification_failed", "系统静音状态没有通过写后验证。", true);
                return new WindowsControlResult(
                    muted ? WindowsControlActions.Mute : WindowsControlActions.Unmute,
                    true,
                    muted ? "muted" : "unmuted",
                    VolumePercent: state.Percentage,
                    Muted: state.Muted);
            });
        }
    }

    private static T WithEndpoint<T>(Func<IAudioEndpointVolume, T> action)
    {
        object? enumeratorObject = null;
        IMMDevice? device = null;
        object? endpointObject = null;
        try
        {
            var type = Type.GetTypeFromCLSID(DeviceEnumeratorClassId, throwOnError: true)!;
            enumeratorObject = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Audio device enumerator is unavailable.");
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            ThrowIfFailed(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device));
            var interfaceId = typeof(IAudioEndpointVolume).GUID;
            ThrowIfFailed(device.Activate(ref interfaceId, ClsCtx.All, 0, out endpointObject));
            return action((IAudioEndpointVolume)endpointObject);
        }
        catch (WindowsControlException) { throw; }
        catch (Exception exception) when (exception is COMException or InvalidCastException or TypeLoadException or InvalidOperationException)
        {
            throw new WindowsControlException("volume_control_unavailable", "无法访问 Windows 系统音量。", true, exception);
        }
        finally
        {
            Release(endpointObject);
            Release(device);
            Release(enumeratorObject);
        }
    }

    private static VolumeState Read(IAudioEndpointVolume endpoint)
    {
        ThrowIfFailed(endpoint.GetMasterVolumeLevelScalar(out var scalar));
        ThrowIfFailed(endpoint.GetMute(out var muted));
        return new VolumeState(Math.Clamp((int)Math.Round(scalar * 100, MidpointRounding.AwayFromZero), 0, 100), muted);
    }

    private static void ThrowIfFailed(int hResult)
    {
        if (hResult < 0) Marshal.ThrowExceptionForHR(hResult);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.FinalReleaseComObject(value);
    }

    private sealed record VolumeState(int Percentage, bool Muted);

    private static readonly Guid DeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private enum EDataFlow { Render, Capture, All }
    private enum ERole { Console, Multimedia, Communications }
    [Flags] private enum ClsCtx : uint { InprocServer = 1, InprocHandler = 2, LocalServer = 4, RemoteServer = 16, All = 23 }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, ClsCtx classContext, nint activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out nint properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float level, Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channelNumber, float level, Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channelNumber, float level, Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channelNumber, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channelNumber, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(Guid eventContext);
        [PreserveSig] int VolumeStepDown(Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float minimumDecibels, out float maximumDecibels, out float incrementDecibels);
    }
}
