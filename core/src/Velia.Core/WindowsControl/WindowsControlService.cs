namespace Velia.Core.WindowsControl;

internal sealed class WindowsControlService(
    IWindowsApplicationController applications,
    IWindowsVolumeController volume,
    IWindowsTimerController timers) : IWindowsControlService
{
    public Task<WindowsControlResult> ExecuteAsync(
        WindowsControlCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return command.Action switch
        {
            WindowsControlActions.OpenApplication when command.ApplicationId is not null && command.Value is null && command.DurationSeconds is null =>
                applications.OpenAsync(command.ApplicationId, cancellationToken),
            WindowsControlActions.CloseApplication when command.ApplicationId is not null && command.Value is null && command.DurationSeconds is null =>
                applications.CloseAsync(command.ApplicationId, cancellationToken),
            WindowsControlActions.MinimizeApplication when command.ApplicationId is not null && command.Value is null && command.DurationSeconds is null =>
                applications.MinimizeAsync(command.ApplicationId, cancellationToken),
            WindowsControlActions.RestoreApplication when command.ApplicationId is not null && command.Value is null && command.DurationSeconds is null =>
                applications.RestoreAsync(command.ApplicationId, cancellationToken),
            WindowsControlActions.MaximizeApplication when command.ApplicationId is not null && command.Value is null && command.DurationSeconds is null =>
                applications.MaximizeAsync(command.ApplicationId, cancellationToken),
            WindowsControlActions.SetVolume when command.ApplicationId is null && command.Value is not null && command.DurationSeconds is null =>
                Task.FromResult(volume.Set(command.Value.Value)),
            WindowsControlActions.AdjustVolume when command.ApplicationId is null && command.Value is not null && command.DurationSeconds is null =>
                Task.FromResult(volume.Adjust(command.Value.Value)),
            WindowsControlActions.Mute when command.ApplicationId is null && command.Value is null && command.DurationSeconds is null =>
                Task.FromResult(volume.SetMuted(true)),
            WindowsControlActions.Unmute when command.ApplicationId is null && command.Value is null && command.DurationSeconds is null =>
                Task.FromResult(volume.SetMuted(false)),
            WindowsControlActions.StartTimer when command.ApplicationId is null && command.Value is null && command.DurationSeconds is not null =>
                Task.FromResult(timers.Start(command.DurationSeconds.Value)),
            WindowsControlActions.CancelTimer when command.ApplicationId is null && command.Value is null && command.DurationSeconds is null =>
                Task.FromResult(timers.Cancel()),
            _ => throw new WindowsControlException("invalid_windows_control", "Windows 控制参数无效。"),
        };
    }
}
