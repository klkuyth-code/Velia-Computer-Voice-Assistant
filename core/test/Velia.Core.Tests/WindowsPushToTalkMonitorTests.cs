using System.Collections.Concurrent;
using System.Threading.Channels;
using Velia.Core.Input;
using Velia.Core.Rpc;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class WindowsPushToTalkMonitorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"velia-input-monitor-{Guid.NewGuid():N}");

    [Fact]
    public async Task PublishesEveryQueuedEdgeFromTheSuppressedShortcut()
    {
        var shortcutEdges = new ConcurrentQueue<PhysicalKeyEdge>();
        shortcutEdges.Enqueue(PhysicalKeyEdge.Pressed);
        shortcutEdges.Enqueue(PhysicalKeyEdge.Released);
        var sink = new RecordingInputEventSink();
        var settings = new VoiceShortcutSettings(Path.Combine(root, "settings.json"));
        settings.Set(VoiceShortcutIds.CtrlShiftSpace);
        using var monitor = new WindowsPushToTalkMonitor(
            sink,
            settings,
            readCtrlShiftSpaceEdge: () => shortcutEdges.TryDequeue(out var edge) ? edge : null,
            pollingInterval: TimeSpan.FromMilliseconds(1));
        monitor.Start();

        var pressed = await sink.ReadAsync(TimeSpan.FromSeconds(2));
        var released = await sink.ReadAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(VoiceInputShortcuts.PushToTalk, pressed.Shortcut);
        Assert.Equal("Ctrl+Shift+Space", pressed.Key);
        Assert.Equal(InputEventEdges.Pressed, pressed.Edge);
        Assert.Equal(InputEventEdges.Released, released.Edge);
        Assert.True(released.Sequence > pressed.Sequence);
    }

    [Fact]
    public async Task PublishesEveryQueuedLongConversationEdgeWithoutPollingTheChord()
    {
        var longConversationEdges = new ConcurrentQueue<PhysicalKeyEdge>();
        longConversationEdges.Enqueue(PhysicalKeyEdge.Pressed);
        longConversationEdges.Enqueue(PhysicalKeyEdge.Released);
        var sink = new RecordingInputEventSink();
        var settings = new VoiceShortcutSettings(Path.Combine(root, "settings.json"));
        using var monitor = new WindowsPushToTalkMonitor(
            sink,
            settings,
            readLongConversationEdge: () => longConversationEdges.TryDequeue(out var edge) ? edge : null,
            pollingInterval: TimeSpan.FromMilliseconds(1));
        monitor.Start();

        var pressed = await sink.ReadAsync(TimeSpan.FromSeconds(2));
        var released = await sink.ReadAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(VoiceInputShortcuts.LongConversation, pressed.Shortcut);
        Assert.Equal("Ctrl+Alt+L", pressed.Key);
        Assert.Equal(InputEventEdges.Pressed, pressed.Edge);
        Assert.Equal(InputEventEdges.Released, released.Edge);
        Assert.True(released.Sequence > pressed.Sequence);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class RecordingInputEventSink : IInputEventSink
    {
        private readonly Channel<InputEvent> events = Channel.CreateUnbounded<InputEvent>();

        public Task PublishAsync(InputEvent inputEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!events.Writer.TryWrite(inputEvent))
                throw new InvalidOperationException("Could not record the input event.");
            return Task.CompletedTask;
        }

        public async Task<InputEvent> ReadAsync(TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            return await events.Reader.ReadAsync(cancellation.Token);
        }
    }
}
