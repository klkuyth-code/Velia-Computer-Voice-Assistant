using Velia.Core.Rpc;
using Velia.Protocol;

namespace Velia.Core.WindowsControl;

internal sealed class WindowsTimerController(ICoreEventSink events, IWindowsTimerAlert? alert = null) : IWindowsTimerController
{
    private readonly object gate = new();
    private ActiveTimer? active;
    private bool disposed;

    public WindowsControlResult Start(int durationSeconds)
    {
        if (durationSeconds is < 1 or > 86_400)
            throw new WindowsControlException("invalid_timer_duration", "计时器时长必须在 1 秒到 24 小时之间。" );
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active is not null)
                throw new WindowsControlException("timer_already_active", "已有一个计时器正在运行，请先取消后再设置新的计时器。" );
            var id = Guid.NewGuid();
            var dueAt = DateTimeOffset.UtcNow.AddSeconds(durationSeconds);
            var timer = new System.Threading.Timer(OnElapsed, id, TimeSpan.FromSeconds(durationSeconds), Timeout.InfiniteTimeSpan);
            active = new ActiveTimer(id, durationSeconds, dueAt, timer);
            return new WindowsControlResult(
                WindowsControlActions.StartTimer,
                true,
                "timerStarted",
                TimerId: id,
                DurationSeconds: durationSeconds,
                DueAtUtc: dueAt);
        }
    }

    public WindowsControlResult Cancel()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active is null)
                return new WindowsControlResult(WindowsControlActions.CancelTimer, true, "noActiveTimer");
            var cancelled = active;
            active = null;
            cancelled.Timer.Dispose();
            return new WindowsControlResult(
                WindowsControlActions.CancelTimer,
                true,
                "timerCancelled",
                TimerId: cancelled.Id,
                DurationSeconds: cancelled.DurationSeconds,
                DueAtUtc: cancelled.DueAtUtc);
        }
    }

    private void OnElapsed(object? state)
    {
        if (state is not Guid id) return;
        ActiveTimer? elapsed;
        lock (gate)
        {
            if (disposed || active?.Id != id) return;
            elapsed = active;
            active = null;
        }
        elapsed.Timer.Dispose();
        alert?.Play();
        _ = PublishElapsedAsync(elapsed);
    }

    private async Task PublishElapsedAsync(ActiveTimer timer)
    {
        try
        {
            await events.PublishAsync(new CoreEvent(
                timer.Id,
                1,
                DateTimeOffset.UtcNow,
                CoreEventTypes.Notification,
                "system",
                Notification: new CoreNotification("timerElapsed", "Velia 计时器", "计时结束。")), CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            active?.Timer.Dispose();
            active = null;
        }
    }

    private sealed record ActiveTimer(Guid Id, int DurationSeconds, DateTimeOffset DueAtUtc, System.Threading.Timer Timer);
}
