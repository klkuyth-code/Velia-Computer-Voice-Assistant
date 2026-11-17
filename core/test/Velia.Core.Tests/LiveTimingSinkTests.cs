using System.Text.Json;
using Velia.Core.Providers.Live;
using Xunit;

namespace Velia.Core.Tests;

public sealed class LiveTimingSinkTests
{
    [Fact]
    public void WritesOnlyStructuredTimingMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"velia-live-timing-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "live-timing.jsonl");
        try
        {
            var sessionId = Guid.NewGuid();
            var sink = new FileLiveTimingSink(path);

            sink.Record(sessionId, "first_output_audio", 1_250, 820, new Dictionary<string, object?>
            {
                ["ok"] = true,
            });

            var line = Assert.Single(File.ReadAllLines(path));
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Assert.Equal("velia-live-timing/v1", root.GetProperty("schema").GetString());
            Assert.Equal(sessionId, root.GetProperty("sessionId").GetGuid());
            Assert.Equal("first_output_audio", root.GetProperty("eventName").GetString());
            Assert.Equal(1_250, root.GetProperty("sessionElapsedMs").GetInt64());
            Assert.Equal(820, root.GetProperty("turnElapsedMs").GetInt64());
            Assert.True(root.GetProperty("details").GetProperty("ok").GetBoolean());
            Assert.DoesNotContain("transcript", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("audioBase64", line, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
