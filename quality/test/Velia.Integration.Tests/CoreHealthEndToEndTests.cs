using System.Diagnostics;
using System.Text.Json;
using Velia.Protocol;
using Xunit;

namespace Velia.Integration.Tests;

public sealed class CoreHealthEndToEndTests
{
    // Core deliberately warms the installed-application index before accepting
    // requests. A machine with many shortcuts can exceed a short cold-start budget.
    private static readonly TimeSpan CoreResponseTimeout = TimeSpan.FromSeconds(25);

    [Fact]
    public async Task CoreIsReadyBeforeAndAfterRestart()
    {
        var workspaceRoot = RequireWorkspaceRoot();
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"velia-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            await using (var core = await CoreTestClient.StartAsync(workspaceRoot, temporaryDirectory))
            {
                var health = await core.RequestAsync("system.health", new Dictionary<string, object?>());
                Assert.Equal("ready", health.GetProperty("status").GetString());
            }

            await using (var restartedCore = await CoreTestClient.StartAsync(workspaceRoot, temporaryDirectory))
            {
                var health = await restartedCore.RequestAsync("system.health", new Dictionary<string, object?>());
                Assert.Equal("ready", health.GetProperty("status").GetString());
            }
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static string RequireWorkspaceRoot()
    {
        var value = Environment.GetEnvironmentVariable("VELIA_WORKSPACE_ROOT");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("VELIA_WORKSPACE_ROOT is required for integration tests.");
        }

        return Path.GetFullPath(value);
    }

    private sealed class CoreTestClient : IAsyncDisposable
    {
        private readonly Process process;

        private CoreTestClient(Process process) => this.process = process;

        public static async Task<CoreTestClient> StartAsync(string workspaceRoot, string dataDirectory)
        {
            var dotnet = Path.Combine(workspaceRoot, ".tools", "dotnet", "dotnet.exe");
            var core = Path.Combine(
                workspaceRoot,
                "core",
                "src",
                "Velia.Core",
                "bin",
                "Debug",
                "net10.0-windows",
                "Velia.Core.dll");
            if (!File.Exists(dotnet) || !File.Exists(core))
            {
                throw new FileNotFoundException("Velia integration runtime is not built.");
            }

            var start = new ProcessStartInfo
            {
                FileName = dotnet,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(core);
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["VELIA_TEST_DATA_DIR"] = dataDirectory;

            var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start Velia Core.");
            var client = new CoreTestClient(process);
            var health = await client.RequestAsync("system.health", new Dictionary<string, object?>());
            Assert.Equal("ready", health.GetProperty("status").GetString());
            return client;
        }

        public async Task<JsonElement> RequestAsync(string method, IReadOnlyDictionary<string, object?> parameters)
        {
            var id = Guid.NewGuid();
            var request = JsonSerializer.Serialize(new
            {
                schema = "velia-rpc-request/v1",
                id,
                method,
                @params = parameters,
            }, JsonDefaults.Strict);
            await process.StandardInput.WriteLineAsync(request);
            await process.StandardInput.FlushAsync();

            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(CoreResponseTimeout);
            if (line is null)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Velia Core closed its output. {error}");
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
            {
                var error = root.GetProperty("error");
                throw new InvalidOperationException(
                    $"{error.GetProperty("code").GetString()}: {error.GetProperty("message").GetString()}");
            }

            return root.GetProperty("result").Clone();
        }

        public async ValueTask DisposeAsync()
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (TimeoutException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            process.Dispose();
        }
    }
}
