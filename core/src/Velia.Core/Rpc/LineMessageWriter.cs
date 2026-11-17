using System.Text.Json;
using Velia.Protocol;

namespace Velia.Core.Rpc;

internal interface ICoreEventSink
{
    Task PublishAsync(CoreEvent coreEvent, CancellationToken cancellationToken);
}

internal interface IInputEventSink
{
    Task PublishAsync(InputEvent inputEvent, CancellationToken cancellationToken);
}

internal interface ILiveEventSink
{
    Task PublishAsync(LiveEvent liveEvent, CancellationToken cancellationToken);
}

internal sealed class LineMessageWriter(TextWriter writer) : ICoreEventSink, IInputEventSink, ILiveEventSink, IDisposable
{
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public Task WriteResponseAsync(RpcResponse response, CancellationToken cancellationToken) =>
        WriteAsync(response, cancellationToken);

    public Task PublishAsync(CoreEvent coreEvent, CancellationToken cancellationToken) =>
        WriteAsync(coreEvent, cancellationToken);

    public Task PublishAsync(InputEvent inputEvent, CancellationToken cancellationToken) =>
        WriteAsync(inputEvent, cancellationToken);

    public Task PublishAsync(LiveEvent liveEvent, CancellationToken cancellationToken) =>
        WriteAsync(liveEvent, cancellationToken);

    public void Dispose() => writeGate.Dispose();

    private async Task WriteAsync<T>(T value, CancellationToken cancellationToken)
    {
        var output = JsonSerializer.Serialize(value, JsonDefaults.Strict);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(output.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }
}
