using System.Text.Json;
using Velia.Core.Rpc;
using Velia.Core.Security;
using Velia.Core.Conversations;
using Velia.Core.Providers;
using Velia.Core.Providers.Chat;
using Velia.Core.Providers.Asr;
using Velia.Core.Providers.Vision;
using Velia.Core.Providers.Live;
using Velia.Core.Documents;
using Velia.Core.Input;
using Velia.Core.DesktopContext;
using Velia.Core.Web;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class RpcServerTests
{
    [Fact]
    public async Task HealthRequestReturnsReadyWithoutExposingSystemAccess()
    {
        var response = await RunAsync(
            "{\"schema\":\"velia-rpc-request/v1\",\"id\":\"1196ea21-1692-46a8-b87b-92c608429ead\",\"method\":\"system.health\",\"params\":{}}");

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("velia-core", response.GetProperty("result").GetProperty("service").GetString());
        Assert.Equal("ready", response.GetProperty("result").GetProperty("status").GetString());
    }

    [Fact]
    public async Task AdditionalTopLevelPropertyIsRejected()
    {
        var response = await RunAsync(
            "{\"schema\":\"velia-rpc-request/v1\",\"id\":\"1196ea21-1692-46a8-b87b-92c608429ead\",\"method\":\"system.health\",\"params\":{},\"shell\":\"cmd.exe\"}");

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_json", response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownMethodIsRejected()
    {
        var response = await RunAsync(
            "{\"schema\":\"velia-rpc-request/v1\",\"id\":\"1196ea21-1692-46a8-b87b-92c608429ead\",\"method\":\"system.runShell\",\"params\":{}}");

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown_method", response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ToolResultForAnUnknownOperationIsRejectedWithoutExecution()
    {
        var response = await RunAsync(
            "{\"schema\":\"velia-rpc-request/v1\",\"id\":\"1196ea21-1692-46a8-b87b-92c608429ead\",\"method\":\"conversation.submitToolResult\",\"params\":{\"operationId\":\"88dbc31f-e8e4-4500-8af5-cad59b87acdf\",\"toolCallId\":\"call_1\",\"ok\":true,\"result\":{\"page\":{}}}}" );

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.False(response.GetProperty("result").GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task ConversationSubmitTextAcceptsTheCurrentMissionParameters()
    {
        var response = await RunAsync(
            "{\"schema\":\"velia-rpc-request/v1\",\"id\":\"1196ea21-1692-46a8-b87b-92c608429ead\",\"method\":\"conversation.submitText\",\"params\":{\"text\":\"你好\",\"origin\":\"voice\",\"relationship\":\"auto\"}}");

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("provider_not_configured", response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task VisionTestWithoutASecretReturnsASafeNotConfiguredResult()
    {
        var response = await RunAsync(
            "{\"schema\":\"velia-rpc-request/v1\",\"id\":\"1196ea21-1692-46a8-b87b-92c608429ead\",\"method\":\"vision.test\",\"params\":{}}");

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("error", response.GetProperty("result").GetProperty("state").GetString());
        Assert.Equal("vision_not_configured", response.GetProperty("result").GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task WebTestWithoutASecretReturnsASafeNotConfiguredResult()
    {
        var response = await RunAsync(
            "{\"schema\":\"velia-rpc-request/v1\",\"id\":\"1196ea21-1692-46a8-b87b-92c608429ead\",\"method\":\"web.test\",\"params\":{}}" );

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("error", response.GetProperty("result").GetProperty("state").GetString());
        Assert.Equal("web_not_configured", response.GetProperty("result").GetProperty("errorCode").GetString());
        Assert.False(response.GetProperty("result").TryGetProperty("secret", out _));
    }

    [Fact]
    public async Task WebTestRejectsArbitraryEndpointParameters()
    {
        var response = await RunAsync(
            "{\"schema\":\"velia-rpc-request/v1\",\"id\":\"1196ea21-1692-46a8-b87b-92c608429ead\",\"method\":\"web.test\",\"params\":{\"endpoint\":\"https://attacker.invalid\"}}" );

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_params", response.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task DocumentOpenExposesNoPathOrWriteMethod()
    {
        var response = await RunAsync(
            "{\"schema\":\"velia-rpc-request/v1\",\"id\":\"1196ea21-1692-46a8-b87b-92c608429ead\",\"method\":\"document.open\",\"params\":{\"name\":\"test.txt\",\"kind\":\"text\",\"text\":\"只读内容\",\"originalBytes\":20,\"pageCount\":null,\"truncated\":false}}");

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("test.txt", response.GetProperty("result").GetProperty("name").GetString());
        Assert.False(response.GetProperty("result").TryGetProperty("path", out _));
    }

    [Fact]
    public async Task DesktopContextCaptureRejectsAnOperationThatIsNotActive()
    {
        var response = await RunAsync(
            "{\"schema\":\"velia-rpc-request/v1\",\"id\":\"1196ea21-1692-46a8-b87b-92c608429ead\",\"method\":\"desktopContext.capture\",\"params\":{\"operationId\":\"2196ea21-1692-46a8-b87b-92c608429ead\",\"mode\":\"selection\"}}");

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("context_operation_inactive", response.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("desktopVisual.getForeground", "{\"operationId\":\"2196ea21-1692-46a8-b87b-92c608429ead\"}")]
    [InlineData("desktopVisual.executeAction", "{\"operationId\":\"2196ea21-1692-46a8-b87b-92c608429ead\",\"action\":\"click\",\"target\":\"确定按钮\",\"displayId\":\"1\",\"screenX\":100,\"screenY\":100,\"text\":null,\"key\":null,\"scrollDelta\":null,\"expectedProcessId\":123,\"expectedProcessName\":\"notepad\",\"expectedWindowTitle\":\"测试窗口\"}")]
    public async Task DesktopVisualRejectsAnOperationThatIsNotActive(string method, string parameters)
    {
        var response = await RunAsync(
            $"{{\"schema\":\"velia-rpc-request/v1\",\"id\":\"1196ea21-1692-46a8-b87b-92c608429ead\",\"method\":\"{method}\",\"params\":{parameters}}}");

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("desktop_visual_operation_inactive", response.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<JsonElement> RunAsync(string input)
    {
        var reader = new StringReader(input + Environment.NewLine);
        var writer = new StringWriter();
        var secrets = new MemorySecretStore();
        var chatProviders = new StubChatProviderFactory();
        var providerConfiguration = new ProviderConfigurationService(secrets, chatProviders);
        using var lineWriter = new LineMessageWriter(writer);
        var conversations = new ConversationService(chatProviders, providerConfiguration, lineWriter);
        var asr = new AsrService(secrets, new StubAsrProvider());
        var vision = new VisionService(secrets, new StubVisionProvider());
        var documents = new DocumentService(secrets, chatProviders);
        var settingsPath = Path.Combine(Path.GetTempPath(), $"velia-test-{Guid.NewGuid():N}", "settings.json");
        var voiceShortcuts = new VoiceShortcutSettings(settingsPath);
        await using var live = new GeminiLiveService(
            secrets, new StubLiveSocketFactory(), lineWriter, conversations);
        var desktopContext = new DesktopContextService(new StubDesktopContextBackend());
        using var firecrawlHttpClient = new HttpClient();
        var firecrawl = new FirecrawlService(firecrawlHttpClient, secrets);
        var server = new LineRpcServer(
            reader,
            lineWriter,
            new CoreRpcDispatcher(
                secrets,
                providerConfiguration,
                conversations,
                asr,
                vision,
                documents,
                voiceShortcuts,
                live,
                desktopContext,
                firecrawl));

        await server.RunAsync(CancellationToken.None);
        using var document = JsonDocument.Parse(writer.ToString());
        return document.RootElement.Clone();
    }

    private sealed class StubLiveSocketFactory : ILiveSocketFactory
    {
        public ILiveSocket Create() => throw new InvalidOperationException("Live socket must not be used by these tests.");
    }

    private sealed class StubDesktopContextBackend : IDesktopContextBackend
    {
        public Task<DesktopContextObservation> CaptureAsync(string mode, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Desktop context must not be used by these tests.");

        public Task<DesktopContextReplaceObservation> ReplaceSelectionAsync(
            DesktopContextTarget target,
            string text,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Desktop context must not be used by these tests.");
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public bool IsConfigured(string provider) => values.ContainsKey(provider);

        public void Save(string provider, string secret) => values[provider] = secret;

        public string? Load(string provider) => values.GetValueOrDefault(provider);

        public void Remove(string provider) => values.Remove(provider);
    }

    private sealed class StubChatProviderFactory : IChatProviderFactory
    {
        public IStreamingChatProvider Create(
            string profileId,
            int maximumOutputTokens = 4096,
            bool thinkingEnabled = true) =>
            new StubChatProvider();
    }

    private sealed class StubChatProvider : IStreamingChatProvider
    {
        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "ok");
            yield return new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop");
        }
    }

    private sealed class StubAsrProvider : IAsrProvider
    {
        public Task<AsrTranscription> TranscribeAsync(ReadOnlyMemory<byte> wavAudio, CancellationToken cancellationToken) =>
            Task.FromResult(new AsrTranscription("测试", 100, 1));
    }

    private sealed class StubVisionProvider : IVisionProvider
    {
        public Task<VisionProviderResult> AnalyzeAsync(ReadOnlyMemory<byte> jpegImage, string prompt, string model, CancellationToken cancellationToken) =>
            Task.FromResult(new VisionProviderResult("测试", 1));

        public Task<long> TestAsync(CancellationToken cancellationToken) => Task.FromResult(1L);
    }
}
