using System.Text.Json;
using System.Collections.Concurrent;
using Velia.Protocol;

namespace Velia.Core.Rpc;

internal sealed class LineRpcServer(TextReader reader, LineMessageWriter writer, CoreRpcDispatcher dispatcher)
{
    // A one-shot JPEG is Base64-encoded inside a vision RPC request. The renderer and
    // VisionService apply the tighter type-specific limits before provider use.
    private const int MaxMessageCharacters = 8_500_000;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var inFlight = new ConcurrentDictionary<Task, byte>();
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            if (line is null) break;

            var task = ProcessAndWriteAsync(line, cancellationToken);
            inFlight.TryAdd(task, 0);
            _ = task.ContinueWith(
                completed => inFlight.TryRemove(completed, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        while (!inFlight.IsEmpty)
        {
            try { await Task.WhenAll(inFlight.Keys).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { /* Each request already has an RPC failure boundary. */ }
        }
    }

    private async Task ProcessAndWriteAsync(string line, CancellationToken cancellationToken)
    {
        var response = await ProcessLineAsync(line, cancellationToken).ConfigureAwait(false);
        await writer.WriteResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RpcResponse> ProcessLineAsync(string line, CancellationToken cancellationToken)
    {
        if (line.Length is 0 or > MaxMessageCharacters)
        {
            return RpcResponse.Failure(Guid.Empty, "invalid_message_size", "RPC message size is invalid.");
        }

        RpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<RpcRequest>(line, JsonDefaults.Strict);
        }
        catch (JsonException)
        {
            return RpcResponse.Failure(Guid.Empty, "invalid_json", "RPC request is not valid protocol JSON.");
        }

        if (request is null || request.Schema != RpcSchema.RequestV1)
        {
            return RpcResponse.Failure(request?.Id ?? Guid.Empty, "unsupported_schema", "RPC schema is unsupported.");
        }

        if (!RpcMethods.All.Contains(request.Method))
        {
            return RpcResponse.Failure(request.Id, "unknown_method", "RPC method is not allowlisted.");
        }

        try
        {
            return await dispatcher.DispatchAsync(request, cancellationToken);
        }
        catch (JsonException)
        {
            return RpcResponse.Failure(request.Id, "invalid_params", "RPC parameters do not match the method schema.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"RPC failure {request.Id}: {exception.GetType().Name}");
            return RpcResponse.Failure(request.Id, "internal_error", "Velia Core could not complete the request.", true);
        }
    }
}
