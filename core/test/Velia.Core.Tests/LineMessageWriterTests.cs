using System.Text.Json;
using Velia.Core.Rpc;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class LineMessageWriterTests
{
    [Fact]
    public async Task SerializesConcurrentResponsesAndEventsAsCompleteJsonLines()
    {
        var output = new StringWriter();
        using var writer = new LineMessageWriter(output);
        var operationId = Guid.NewGuid();
        var tasks = Enumerable.Range(1, 40).Select(index => index % 3 == 0
            ? writer.PublishAsync(
                new LiveEvent(operationId, index, DateTimeOffset.UtcNow, LiveEventTypes.Listening, LiveStates.Listening),
                CancellationToken.None)
            : index % 2 == 0
            ? writer.PublishAsync(
                new CoreEvent(operationId, index, DateTimeOffset.UtcNow, "content", "text", Delta: $"part-{index}"),
                CancellationToken.None)
            : writer.WriteResponseAsync(
                RpcResponse.Success(Guid.NewGuid(), new { index }),
                CancellationToken.None));

        await Task.WhenAll(tasks);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(40, lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var schema = document.RootElement.GetProperty("schema").GetString();
            Assert.True(schema is "velia-core-event/v1" or "velia-rpc-response/v1" or "velia-live-event/v1");
        }
    }
}
