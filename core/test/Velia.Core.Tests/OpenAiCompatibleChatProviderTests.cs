using System.Net;
using System.Text;
using System.Text.Json;
using Velia.Core.Providers.Chat;
using Xunit;

namespace Velia.Core.Tests;

public sealed class OpenAiCompatibleChatProviderTests
{
    [Fact]
    public async Task StreamsReasoningContentToolCallsAndUsageWithoutPuttingTheKeyInTheBody()
    {
        const string sse = """
            data: {"choices":[{"delta":{"reasoning_content":"先分析"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"weather_forecast","arguments":"{\"city\":\"上"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"海\"}"}}]},"finish_reason":"tool_calls"}]}

            data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":4,"total_tokens":14}}

            data: [DONE]

            """;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleChatProvider(
            client,
            () => "secret-api-key",
            new ChatProviderOptions(new Uri("https://api.example.test"), "model-v1"));
        var toolSchema = JsonDocument.Parse("{\"type\":\"object\",\"additionalProperties\":false}").RootElement.Clone();
        var request = new ChatCompletionRequest(
            [new ChatMessage(ChatRole.User, "上海天气")],
            [new ChatToolDefinition("weather_forecast", "查询天气", toolSchema)]);

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in provider.StreamAsync(request, CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        Assert.Equal(ChatStreamEventKind.ReasoningDelta, events[0].Kind);
        Assert.Equal("先分析", events[0].Text);
        Assert.Equal("weather_forecast", events[1].ToolCall?.Name);
        Assert.Equal("{\"city\":\"上", events[1].ToolCall?.ArgumentsFragment);
        Assert.Equal("海\"}", events[2].ToolCall?.ArgumentsFragment);
        Assert.Equal(ChatStreamEventKind.Completed, events[^1].Kind);
        Assert.Equal("tool_calls", events[^1].FinishReason);
        Assert.Equal(new ChatUsage(10, 4, 14), events[^1].Usage);

        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret-api-key", handler.AuthorizationParameter);
        Assert.Equal("https://api.example.test/chat/completions", handler.RequestUri?.AbsoluteUri);
        Assert.DoesNotContain("secret-api-key", handler.RequestBody, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("model-v1", payload.RootElement.GetProperty("model").GetString());
        Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("enabled", payload.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(4096, payload.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("auto", payload.RootElement.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task ClassifiesRateLimitAsRetryableAndCapsProviderFailureAtTheBoundary()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited", Encoding.UTF8, "text/plain"),
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleChatProvider(
            client,
            () => "key",
            new ChatProviderOptions(new Uri("https://api.example.test"), "model-v1"));

        var exception = await Assert.ThrowsAsync<ChatProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(
                               new ChatCompletionRequest([new ChatMessage(ChatRole.User, "你好")]),
                               CancellationToken.None))
            {
            }
        });

        Assert.True(exception.Retryable);
        Assert.Equal(429, exception.StatusCode);
    }

    [Fact]
    public async Task RejectsAStreamThatEndsWithoutTheDoneMarker()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleChatProvider(
            client,
            () => "key",
            new ChatProviderOptions(new Uri("https://api.example.test"), "model-v1"));

        var exception = await Assert.ThrowsAsync<ChatProviderException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(
                               new ChatCompletionRequest([new ChatMessage(ChatRole.User, "你好")]),
                               CancellationToken.None))
            {
            }
        });

        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task SerializesAssistantToolCallsAndToolResultsForContinuation()
    {
        const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleChatProvider(client, () => "key",
            new ChatProviderOptions(new Uri("https://api.example.test"), "model-v1"));
        var request = new ChatCompletionRequest(
        [
            new ChatMessage(ChatRole.User, "读页面"),
            new ChatMessage(ChatRole.Assistant, string.Empty, ToolCalls:
                [new ChatAssistantToolCall("call_1", "browser", "{\"method\":\"getPage\",\"params\":{}}")]),
            new ChatMessage(ChatRole.Tool, "{\"ok\":true}", "call_1"),
        ]);

        await foreach (var _ in provider.StreamAsync(request, CancellationToken.None)) { }

        using var payload = JsonDocument.Parse(handler.RequestBody);
        var messages = payload.RootElement.GetProperty("messages");
        Assert.Equal(JsonValueKind.Null, messages[1].GetProperty("content").ValueKind);
        Assert.Equal("call_1", messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("browser", messages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[2].GetProperty("tool_call_id").GetString());
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
