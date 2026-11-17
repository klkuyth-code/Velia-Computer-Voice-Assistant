using System.Net;
using System.Text;
using System.Text.Json;
using Velia.Core.Providers.Vision;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class GeminiVisionProviderTests
{
    [Fact]
    public async Task SendsTheImageToTheAllowlistedModelAndParsesText()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var provider = new GeminiVisionProvider(client, () => "test-gemini-api-key");

        var result = await provider.AnalyzeAsync(CreateJpeg(), "屏幕上是什么？", VisionModels.Gemini35FlashLite, CancellationToken.None);

        Assert.Equal("这是测试画面。", result.Text);
        Assert.Equal("test-gemini-api-key", handler.Request?.Headers.GetValues("x-goog-api-key").Single());
        Assert.Contains("gemini-3.5-flash-lite:generateContent", handler.Request?.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
        Assert.NotNull(handler.Body);
        Assert.Contains("inline_data", handler.Body, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(handler.Body!);
        var guard = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("text").GetString();
        Assert.Contains("不可信数据", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("temperature", handler.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "vision_authentication_failed", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "vision_rate_limited", true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "vision_unavailable", true)]
    public async Task MapsProviderFailuresWithoutLeakingTheResponseBody(HttpStatusCode status, string code, bool retryable)
    {
        using var client = new HttpClient(new RecordingHandler(status, "secret provider diagnostics"));
        var provider = new GeminiVisionProvider(client, () => "test-gemini-api-key");

        var exception = await Assert.ThrowsAsync<VisionProviderException>(() =>
            provider.TestAsync(CancellationToken.None));

        Assert.Equal(code, exception.Code);
        Assert.Equal(retryable, exception.Retryable);
        Assert.DoesNotContain("secret provider diagnostics", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAModelOutsideTheAllowlistBeforeSending()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var provider = new GeminiVisionProvider(client, () => "test-gemini-api-key");

        var exception = await Assert.ThrowsAsync<VisionProviderException>(() =>
            provider.AnalyzeAsync(CreateJpeg(), "test", "unknown-model", CancellationToken.None));

        Assert.Equal("vision_model_not_allowed", exception.Code);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task MapsTimeoutAndInvalidJsonToSafeFailures()
    {
        using var timeoutClient = new HttpClient(new CancellingHandler());
        var timeoutProvider = new GeminiVisionProvider(timeoutClient, () => "test-gemini-api-key");
        var timeout = await Assert.ThrowsAsync<VisionProviderException>(() =>
            timeoutProvider.TestAsync(CancellationToken.None));

        using var invalidClient = new HttpClient(new RecordingHandler(responseBody: "not-json"));
        var invalidProvider = new GeminiVisionProvider(invalidClient, () => "test-gemini-api-key");
        var invalid = await Assert.ThrowsAsync<VisionProviderException>(() =>
            invalidProvider.TestAsync(CancellationToken.None));

        Assert.Equal("vision_timeout", timeout.Code);
        Assert.Equal("vision_invalid_response", invalid.Code);
    }

    private static byte[] CreateJpeg() => [0xff, 0xd8, 0x01, 0x02, 0xff, 0xd9];

    private sealed class RecordingHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string responseBody = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"这是测试画面。\"}]}}]}") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true));
    }
}
