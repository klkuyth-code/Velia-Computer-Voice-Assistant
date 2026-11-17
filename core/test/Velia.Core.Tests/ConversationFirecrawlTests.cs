using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Velia.Core.Conversations;
using Velia.Core.Providers;
using Velia.Core.Providers.Chat;
using Velia.Core.Rpc;
using Velia.Core.Security;
using Velia.Core.Web;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class ConversationFirecrawlTests
{
    [Fact]
    public async Task ExecutesWebResearchInsideCoreAndReleasesTheOperation()
    {
        var provider = new SequencedProvider(
        [
            [
                ToolCall("web_1", "web_research", """{"action":"search","query":"Velia Firecrawl","limit":3}"""),
                CompletedWithTools(),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "已经找到公开资料。"),
                Completed(),
            ],
        ]);
        var sink = new RecordingEventSink();
        var firecrawl = new FakeFirecrawlService
        {
            Result = new FirecrawlSearchResult(
                FirecrawlActions.Search,
                "firecrawl",
                true,
                [new FirecrawlSearchItem(Guid.NewGuid(), "Velia", "公开资料", "https://openai.com/research/")]),
        };
        var service = CreateService(provider, sink, firecrawl);

        var submitted = service.Submit("请联网搜索 Velia Firecrawl");
        var terminal = await sink.WaitForTerminalAsync();

        Assert.Equal("completed", terminal.Type);
        var execution = Assert.Single(firecrawl.Executions);
        Assert.Equal(submitted.OperationId, execution.OperationId);
        Assert.Equal(FirecrawlActions.Search, execution.Command.Action);
        Assert.Equal("Velia Firecrawl", execution.Command.Query);
        Assert.Equal(3, execution.Command.Limit);
        Assert.Equal(submitted.OperationId, Assert.Single(firecrawl.Released));
        Assert.Contains(sink.Events, static item =>
            item.Type == "tool" && item.ToolCall?.Name == "web_research" && item.ToolCall.Id == "web_1");
        var result = Assert.Single(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "web_1");
        Assert.Contains("\"ok\":true", result.Content, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"firecrawl\"", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeniesWebResearchWhenTheUserDidNotExplicitlyRequestNetworkAccess()
    {
        var provider = new SequencedProvider(
        [
            [
                ToolCall("web_denied", "web_research", """{"action":"search","query":"Velia Firecrawl","limit":3}"""),
                CompletedWithTools(),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有联网。"),
                Completed(),
            ],
        ]);
        var sink = new RecordingEventSink();
        var firecrawl = new FakeFirecrawlService();
        var service = CreateService(provider, sink, firecrawl);

        var submitted = service.Submit("帮我整理一下 Velia Firecrawl");
        await sink.WaitForTerminalAsync();

        Assert.Empty(firecrawl.Executions);
        Assert.Equal(submitted.OperationId, Assert.Single(firecrawl.Released));
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
        var denied = Assert.Single(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "web_denied");
        Assert.Contains("tool_consent_denied", denied.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsExtraWebFieldsWithoutEchoingTheirSecretValue()
    {
        const string InjectedSecret = "never-echo-this-header-value";
        var provider = new SequencedProvider(
        [
            [
                ToolCall(
                    "web_injected",
                    "web_research",
                    "{\"action\":\"search\",\"query\":\"Velia Firecrawl\",\"limit\":3," +
                    "\"headers\":{\"Authorization\":\"" + InjectedSecret + "\"}}"),
                CompletedWithTools(),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "参数已拒绝。"),
                Completed(),
            ],
        ]);
        var sink = new RecordingEventSink();
        var firecrawl = new FakeFirecrawlService();
        var service = CreateService(provider, sink, firecrawl);

        service.Submit("请联网搜索 Velia Firecrawl");
        await sink.WaitForTerminalAsync();

        Assert.Empty(firecrawl.Executions);
        Assert.DoesNotContain(sink.Events, static item => item.Type == "tool");
        var rejected = Assert.Single(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "web_injected");
        Assert.Contains("invalid_web_research", rejected.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(InjectedSecret, rejected.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowsBrowserFallbackAfterFirecrawlFailureForAnExplicitNetworkRequest()
    {
        var provider = new SequencedProvider(
        [
            [
                ToolCall("web_failed", "web_research", """{"action":"search","query":"Velia 最新消息","limit":3}"""),
                CompletedWithTools(),
            ],
            [
                ToolCall("browser_fallback", "browser", """{"method":"getPage","params":{}}"""),
                CompletedWithTools(),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "已通过当前浏览器页面完成。"),
                Completed(),
            ],
        ]);
        var sink = new RecordingEventSink();
        var firecrawl = new FakeFirecrawlService
        {
            Failure = new FirecrawlException("web_unavailable", "Firecrawl 暂时不可用。", true),
        };
        var service = CreateService(provider, sink, firecrawl);

        var submitted = service.Submit("请联网搜索 Velia 最新消息");
        var browserTool = await sink.WaitForToolAsync("browser_fallback");
        var accepted = service.SubmitToolResult(new SubmitToolResultParams(
            submitted.OperationId,
            browserTool.ToolCall!.Id!,
            true,
            JsonDocument.Parse("""{"page":{"title":"Velia 最新消息"}}""").RootElement.Clone()));
        var terminal = await sink.WaitForTerminalAsync();

        Assert.True(accepted.Accepted);
        Assert.Equal("completed", terminal.Type);
        Assert.Single(firecrawl.Executions);
        Assert.Equal(submitted.OperationId, Assert.Single(firecrawl.Released));
        var failedResult = Assert.Single(provider.Requests[1].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "web_failed");
        Assert.Contains("web_unavailable", failedResult.Content, StringComparison.Ordinal);
        Assert.Contains("browser", failedResult.Content, StringComparison.Ordinal);
        Assert.Contains(provider.Requests[2].Messages, static message =>
            message.Role == ChatRole.Tool && message.ToolCallId == "browser_fallback" && message.Content.Contains("Velia", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APolicyRejectedWebTargetCannotFallBackToBrowser()
    {
        var provider = new SequencedProvider(
        [
            [
                ToolCall("web_rejected", "web_research", """{"action":"scrape","url":"http://127.0.0.1/private"}"""),
                CompletedWithTools(),
            ],
            [
                ToolCall("browser_bypass", "browser", """{"method":"open","params":{"url":"http://127.0.0.1/private"}}"""),
                CompletedWithTools(),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "该地址已被安全策略拒绝。"),
                Completed(),
            ],
        ]);
        var sink = new RecordingEventSink();
        var firecrawl = new FakeFirecrawlService
        {
            Failure = new FirecrawlException("web_url_rejected", "不能抓取本机、局域网或保留地址。"),
        };
        var service = CreateService(provider, sink, firecrawl);

        service.Submit("请抓取并读取 http://127.0.0.1/private");
        await sink.WaitForTerminalAsync();

        Assert.Single(firecrawl.Executions);
        Assert.DoesNotContain(sink.Events, static item => item.ToolCall?.Id == "browser_bypass");
        var webResult = Assert.Single(provider.Requests[1].Messages, static message => message.ToolCallId == "web_rejected");
        Assert.Contains("web_url_rejected", webResult.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("可尝试使用 browser", webResult.Content, StringComparison.Ordinal);
        var browserResult = Assert.Single(provider.Requests[2].Messages, static message => message.ToolCallId == "browser_bypass");
        Assert.Contains("browser_fallback_rejected", browserResult.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExplicitBrowserDenialBlocksBrowserEvenForAWebRequest()
    {
        var provider = new SequencedProvider(
        [
            [
                ToolCall("browser_denied", "browser", """{"method":"open","params":{"url":"https://example.com"}}"""),
                CompletedWithTools(),
            ],
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "没有打开浏览器。"),
                Completed(),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink, new FakeFirecrawlService());

        service.Submit("请读取公开网页，但不要打开浏览器");
        await sink.WaitForTerminalAsync();

        Assert.DoesNotContain(sink.Events, static item => item.ToolCall?.Id == "browser_denied");
        var denied = Assert.Single(provider.Requests[1].Messages, static message => message.ToolCallId == "browser_denied");
        Assert.Contains("browser_user_denied", denied.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebResearchSchemaIsStrictAndDoesNotExposeArbitraryTransportFields()
    {
        var provider = new SequencedProvider(
        [
            [
                new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "完成。"),
                Completed(),
            ],
        ]);
        var sink = new RecordingEventSink();
        var service = CreateService(provider, sink, new FakeFirecrawlService());

        service.Submit("测试工具定义");
        await sink.WaitForTerminalAsync();

        var tool = Assert.Single(provider.Requests[0].Tools!, static item => item.Name == "web_research");
        Assert.False(tool.Parameters.GetProperty("additionalProperties").GetBoolean());
        var properties = tool.Parameters.GetProperty("properties");
        Assert.Equal(
            ["action", "query", "limit", "resultId", "url"],
            properties.EnumerateObject().Select(static item => item.Name));
        Assert.False(properties.TryGetProperty("headers", out _));
        Assert.False(properties.TryGetProperty("cookie", out _));
        Assert.False(properties.TryGetProperty("endpoint", out _));
        Assert.False(properties.TryGetProperty("script", out _));
    }

    private static ChatStreamEvent ToolCall(string id, string name, string arguments) => new(
        ChatStreamEventKind.ToolCallDelta,
        ToolCall: new ChatToolCallDelta(0, id, name, arguments));

    private static ChatStreamEvent CompletedWithTools() =>
        new(ChatStreamEventKind.Completed, FinishReason: "tool_calls");

    private static ChatStreamEvent Completed() =>
        new(ChatStreamEventKind.Completed, FinishReason: "stop");

    private static ConversationService CreateService(
        IStreamingChatProvider provider,
        ICoreEventSink sink,
        IFirecrawlService firecrawl)
    {
        var secrets = new MemorySecretStore();
        secrets.Save(ProviderIds.Llm, "test-key");
        var factory = new SingleProviderFactory(provider);
        var configuration = new ProviderConfigurationService(secrets, factory);
        return new ConversationService(
            factory,
            configuration,
            sink,
            firecrawlService: firecrawl);
    }

    private sealed class FakeFirecrawlService : IFirecrawlService
    {
        public List<(Guid OperationId, FirecrawlCommand Command)> Executions { get; } = [];

        public List<Guid> Released { get; } = [];

        public object Result { get; init; } = new FirecrawlSearchResult(
            FirecrawlActions.Search,
            "firecrawl",
            true,
            []);

        public FirecrawlException? Failure { get; init; }

        public Task<WebTestResult> TestAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<object> ExecuteAsync(
            Guid operationId,
            FirecrawlCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Executions.Add((operationId, command));
            return Failure is not null
                ? Task.FromException<object>(Failure)
                : Task.FromResult(Result);
        }

        public void Release(Guid operationId) => Released.Add(operationId);
    }

    private sealed class SingleProviderFactory(IStreamingChatProvider provider) : IChatProviderFactory
    {
        public IStreamingChatProvider Create(
            string profileId,
            int maximumOutputTokens = 4096,
            bool thinkingEnabled = true) => provider;
    }

    private sealed class SequencedProvider(IReadOnlyList<IReadOnlyList<ChatStreamEvent>> rounds)
        : IStreamingChatProvider
    {
        public List<ChatCompletionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(request);
            foreach (var item in rounds[index])
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }

    private sealed class RecordingEventSink : ICoreEventSink
    {
        private readonly ConcurrentQueue<CoreEvent> events = new();
        private readonly TaskCompletionSource<CoreEvent> terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CoreEvent[] Events => events.ToArray();

        public Task PublishAsync(CoreEvent coreEvent, CancellationToken cancellationToken)
        {
            events.Enqueue(coreEvent);
            if (coreEvent.Type is "completed" or "failed" or "cancelled")
            {
                terminal.TrySetResult(coreEvent);
            }
            return Task.CompletedTask;
        }

        public Task<CoreEvent> WaitForTerminalAsync() => terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public async Task<CoreEvent> WaitForToolAsync(string callId)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var existing = events.FirstOrDefault(item =>
                    item.Type == "tool" && item.ToolCall?.Id == callId);
                if (existing is not null) return existing;
                await Task.Delay(10);
            }
            throw new TimeoutException($"Tool event {callId} was not published.");
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public bool IsConfigured(string provider) => values.ContainsKey(provider);

        public void Save(string provider, string secret) => values[provider] = secret;

        public string? Load(string provider) => values.GetValueOrDefault(provider);

        public void Remove(string provider) => values.Remove(provider);
    }
}
