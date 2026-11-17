using Velia.Core.Rpc;
using Velia.Protocol;

namespace Velia.Core.Input;

internal sealed class WindowsPushToTalkMonitor(
    IInputEventSink events,
    VoiceShortcutSettings settings,
    Func<PhysicalKeyEdge?>? readCtrlShiftSpaceEdge = null,
    Func<PhysicalKeyEdge?>? readLongConversationEdge = null,
    TimeSpan? pollingInterval = null) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly PhysicalKeyEdgeDetector detector = new();
    private readonly TimeSpan interval = pollingInterval ?? TimeSpan.FromMilliseconds(8);
    private Task? worker;
    private long sequence;
    private string? sampledShortcutId;

    public void Start()
    {
        if (worker is not null) throw new InvalidOperationException("Push-to-talk monitoring has already started.");
        worker = Task.Run(RunAsync);
    }

    public void Dispose()
    {
        cancellation.Cancel();
        try { worker?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        cancellation.Dispose();
    }

    private async Task RunAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            var shortcut = settings.Current;
            if (!StringComparer.Ordinal.Equals(sampledShortcutId, shortcut.Id))
            {
                sampledShortcutId = shortcut.Id;
                detector.Reset();
            }
            if (readCtrlShiftSpaceEdge is not null)
            {
                while (readCtrlShiftSpaceEdge() is { } directEdge)
                {
                    if (shortcut.Id == VoiceShortcutIds.CtrlShiftSpace)
                    {
                        await PublishAsync(
                            VoiceInputShortcuts.PushToTalk,
                            shortcut.Key,
                            directEdge,
                            cancellation.Token).ConfigureAwait(false);
                    }
                }
            }
            if ((shortcut.Id != VoiceShortcutIds.CtrlShiftSpace || readCtrlShiftSpaceEdge is null) &&
                detector.Sample(shortcut.IsDown()) is { } sampledEdge)
            {
                await PublishAsync(
                    VoiceInputShortcuts.PushToTalk,
                    shortcut.Key,
                    sampledEdge,
                    cancellation.Token).ConfigureAwait(false);
            }
            if (readLongConversationEdge is not null)
            {
                while (readLongConversationEdge() is { } longConversationEdge)
                {
                    await PublishAsync(
                        VoiceInputShortcuts.LongConversation,
                        "Ctrl+Alt+L",
                        longConversationEdge,
                        cancellation.Token).ConfigureAwait(false);
                }
            }
            await Task.Delay(interval, cancellation.Token).ConfigureAwait(false);
        }
    }

    private Task PublishAsync(string shortcut, string key, PhysicalKeyEdge edge, CancellationToken cancellationToken) =>
        events.PublishAsync(new InputEvent(
            Guid.NewGuid(),
            Interlocked.Increment(ref sequence),
            DateTimeOffset.UtcNow,
            shortcut,
            key,
            edge == PhysicalKeyEdge.Pressed ? InputEventEdges.Pressed : InputEventEdges.Released), cancellationToken);

}
