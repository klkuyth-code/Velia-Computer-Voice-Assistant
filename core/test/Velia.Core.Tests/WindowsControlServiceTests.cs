using Velia.Core.Rpc;
using Velia.Core.WindowsControl;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class WindowsControlServiceTests
{
    [Fact]
    public async Task RoutesOnlyFixedCommandsToTheirBoundedControllers()
    {
        var applications = new FakeApplications();
        var volume = new FakeVolume();
        using var timers = new FakeTimers();
        var service = new WindowsControlService(applications, volume, timers);

        await service.ExecuteAsync(new WindowsControlCommand(WindowsControlActions.OpenApplication, ApplicationId: WindowsApplicationIds.Notepad), CancellationToken.None);
        await service.ExecuteAsync(new WindowsControlCommand(WindowsControlActions.MinimizeApplication, ApplicationId: WindowsApplicationIds.Chrome), CancellationToken.None);
        await service.ExecuteAsync(new WindowsControlCommand(WindowsControlActions.RestoreApplication, ApplicationId: WindowsApplicationIds.Chrome), CancellationToken.None);
        await service.ExecuteAsync(new WindowsControlCommand(WindowsControlActions.MaximizeApplication, ApplicationId: WindowsApplicationIds.Chrome), CancellationToken.None);
        await service.ExecuteAsync(new WindowsControlCommand(WindowsControlActions.CloseApplication, ApplicationId: WindowsApplicationIds.Chrome), CancellationToken.None);
        await service.ExecuteAsync(new WindowsControlCommand(WindowsControlActions.SetVolume, Value: 30), CancellationToken.None);
        await service.ExecuteAsync(new WindowsControlCommand(WindowsControlActions.StartTimer, DurationSeconds: 10), CancellationToken.None);

        Assert.Equal([WindowsApplicationIds.Notepad], applications.Opened);
        Assert.Equal([WindowsApplicationIds.Chrome], applications.Minimized);
        Assert.Equal([WindowsApplicationIds.Chrome], applications.Restored);
        Assert.Equal([WindowsApplicationIds.Chrome], applications.Maximized);
        Assert.Equal([WindowsApplicationIds.Chrome], applications.Closed);
        Assert.Equal([30], volume.SetValues);
        Assert.Equal([10], timers.Started);
    }

    [Fact]
    public async Task RejectsUnexpectedFieldsBeforeBackends()
    {
        var applications = new FakeApplications();
        var volume = new FakeVolume();
        using var timers = new FakeTimers();
        var service = new WindowsControlService(applications, volume, timers);

        var exception = await Assert.ThrowsAsync<WindowsControlException>(() => service.ExecuteAsync(
            new WindowsControlCommand(WindowsControlActions.OpenApplication, ApplicationId: "arbitrary", Value: 1),
            CancellationToken.None));

        Assert.Equal("invalid_windows_control", exception.Code);
        Assert.Empty(applications.Opened);
    }

    [Fact]
    public async Task TimerPublishesOneBoundedElapsedNotification()
    {
        var sink = new RecordingSink();
        var alert = new FakeTimerAlert();
        using var timer = new WindowsTimerController(sink, alert);

        var started = timer.Start(1);
        var coreEvent = await sink.WaitAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(started.TimerId, coreEvent.OperationId);
        Assert.Equal(CoreEventTypes.Notification, coreEvent.Type);
        Assert.Equal("timerElapsed", coreEvent.Notification?.Kind);
        Assert.Equal("计时结束。", coreEvent.Notification?.Message);
        Assert.Equal(1, alert.CallCount);
    }

    [Fact]
    public void TimerDoesNotSilentlyReplaceAnActiveTimer()
    {
        var sink = new RecordingSink();
        using var timer = new WindowsTimerController(sink);
        timer.Start(60);

        var exception = Assert.Throws<WindowsControlException>(() => timer.Start(10));
        var cancelled = timer.Cancel();

        Assert.Equal("timer_already_active", exception.Code);
        Assert.Equal("timerCancelled", cancelled.State);
    }

    [Fact]
    public async Task ApplicationControllerRejectsUnknownAndExplorerCloseWithoutSideEffects()
    {
        var applications = new WindowsApplicationController();
        var unknown = await Assert.ThrowsAsync<WindowsControlException>(() =>
            applications.OpenAsync("arbitrary", CancellationToken.None));
        var explorer = await Assert.ThrowsAsync<WindowsControlException>(() =>
            applications.CloseAsync(WindowsApplicationIds.FileExplorer, CancellationToken.None));

        Assert.Equal("application_not_allowlisted", unknown.Code);
        Assert.Equal("application_close_rejected", explorer.Code);
    }

    private sealed class FakeApplications : IWindowsApplicationController
    {
        public List<string> Opened { get; } = [];
        public List<string> Closed { get; } = [];
        public List<string> Minimized { get; } = [];
        public List<string> Restored { get; } = [];
        public List<string> Maximized { get; } = [];
        public Task<WindowsControlResult> OpenAsync(string applicationId, CancellationToken cancellationToken)
        {
            Opened.Add(applicationId);
            return Task.FromResult(new WindowsControlResult(WindowsControlActions.OpenApplication, true, "opened", applicationId));
        }
        public Task<WindowsControlResult> CloseAsync(string applicationId, CancellationToken cancellationToken)
        {
            Closed.Add(applicationId);
            return Task.FromResult(new WindowsControlResult(WindowsControlActions.CloseApplication, true, "closed", applicationId));
        }
        public Task<WindowsControlResult> MinimizeAsync(string applicationId, CancellationToken cancellationToken)
        {
            Minimized.Add(applicationId);
            return Task.FromResult(new WindowsControlResult(WindowsControlActions.MinimizeApplication, true, "minimized", applicationId));
        }
        public Task<WindowsControlResult> RestoreAsync(string applicationId, CancellationToken cancellationToken)
        {
            Restored.Add(applicationId);
            return Task.FromResult(new WindowsControlResult(WindowsControlActions.RestoreApplication, true, "restored", applicationId));
        }
        public Task<WindowsControlResult> MaximizeAsync(string applicationId, CancellationToken cancellationToken)
        {
            Maximized.Add(applicationId);
            return Task.FromResult(new WindowsControlResult(WindowsControlActions.MaximizeApplication, true, "maximized", applicationId));
        }
    }

    private sealed class FakeVolume : IWindowsVolumeController
    {
        public List<int> SetValues { get; } = [];
        public WindowsControlResult Set(int percentage)
        {
            SetValues.Add(percentage);
            return new WindowsControlResult(WindowsControlActions.SetVolume, true, "volumeSet", VolumePercent: percentage);
        }
        public WindowsControlResult Adjust(int delta) => new(WindowsControlActions.AdjustVolume, true, "volumeAdjusted", VolumePercent: delta);
        public WindowsControlResult SetMuted(bool muted) => new(muted ? WindowsControlActions.Mute : WindowsControlActions.Unmute, true, muted ? "muted" : "unmuted", Muted: muted);
    }

    private sealed class FakeTimers : IWindowsTimerController
    {
        public List<int> Started { get; } = [];
        public WindowsControlResult Start(int durationSeconds)
        {
            Started.Add(durationSeconds);
            return new WindowsControlResult(WindowsControlActions.StartTimer, true, "timerStarted", TimerId: Guid.NewGuid(), DurationSeconds: durationSeconds);
        }
        public WindowsControlResult Cancel() => new(WindowsControlActions.CancelTimer, true, "timerCancelled");
        public void Dispose() { }
    }

    private sealed class RecordingSink : ICoreEventSink
    {
        private readonly TaskCompletionSource<CoreEvent> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PublishAsync(CoreEvent coreEvent, CancellationToken cancellationToken)
        {
            completion.TrySetResult(coreEvent);
            return Task.CompletedTask;
        }
        public Task<CoreEvent> WaitAsync() => completion.Task;
    }

    private sealed class FakeTimerAlert : IWindowsTimerAlert
    {
        public int CallCount { get; private set; }
        public void Play() => CallCount++;
    }
}
