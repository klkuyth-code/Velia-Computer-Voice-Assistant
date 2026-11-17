using System.Text;
using System.Text.Json;

namespace Velia.Core.Providers.Live;

internal interface ILiveTimingSink
{
    void Record(
        Guid sessionId,
        string eventName,
        long sessionElapsedMs,
        long? turnElapsedMs = null,
        IReadOnlyDictionary<string, object?>? details = null);
}

internal sealed class FileLiveTimingSink(string path) : ILiveTimingSink
{
    private const long MaximumBytes = 2 * 1024 * 1024;
    private readonly object gate = new();
    private readonly string filePath = Path.GetFullPath(path);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public void Record(
        Guid sessionId,
        string eventName,
        long sessionElapsedMs,
        long? turnElapsedMs = null,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        if (eventName.Length is < 1 or > 80 || sessionElapsedMs < 0 || turnElapsedMs < 0) return;
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                schema = "velia-live-timing/v1",
                atUtc = DateTimeOffset.UtcNow,
                sessionId,
                eventName,
                sessionElapsedMs,
                turnElapsedMs,
                details,
            });
            lock (gate)
            {
                var directory = Path.GetDirectoryName(filePath)!;
                Directory.CreateDirectory(directory);
                if (File.Exists(filePath) && new FileInfo(filePath).Length > MaximumBytes)
                    File.WriteAllText(filePath, string.Empty, Utf8WithoutBom);
                File.AppendAllText(filePath, line + Environment.NewLine, Utf8WithoutBom);
            }
        }
        catch
        {
            // Diagnostics must never interrupt Live audio or local command execution.
        }
    }
}
