using System.Net;
using System.Text;
using System.Text.Json;
using Velia.Core.Providers.Asr;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class VolcengineFlashAsrProviderTests
{
    [Fact]
    public async Task SendsOnlyAllowlistedHeadersAndParsesTranscript()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var credentials = JsonSerializer.Serialize(new AsrCredentials("apiKey", ApiKey: "test-api-key"), JsonDefaults.Strict);
        var provider = new VolcengineFlashAsrProvider(client, () => credentials);

        var result = await provider.TranscribeAsync(CreateWav(), CancellationToken.None);

        Assert.Equal("你好，Velia。", result.Text);
        Assert.Equal("test-api-key", handler.Request?.Headers.GetValues("X-Api-Key").Single());
        Assert.Equal("volc.bigasr.auc_turbo", handler.Request?.Headers.GetValues("X-Api-Resource-Id").Single());
        Assert.False(handler.Request?.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task SendsBoundedRequestHotwordsWithoutLocalPaths()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var credentials = JsonSerializer.Serialize(
            new AsrCredentials("apiKey", ApiKey: "test-api-key"),
            JsonDefaults.Strict);
        var provider = new VolcengineFlashAsrProvider(client, () => credentials);

        await provider.TranscribeAsync(
            CreateWav(),
            new AsrRecognitionContext(["Velia", "剪映专业版", "C:\\Users\\secret.txt", "停止"]),
            CancellationToken.None);

        var body = await handler.ReadRequestBodyAsync();
        using var payload = JsonDocument.Parse(body);
        var context = payload.RootElement.GetProperty("request").GetProperty("context").GetString();
        using var parsedContext = JsonDocument.Parse(context!);
        var hotwords = parsedContext.RootElement.GetProperty("hotwords")
            .EnumerateArray()
            .Select(static item => item.GetProperty("word").GetString())
            .ToArray();
        Assert.Contains("剪映专业版", hotwords);
        Assert.Contains("停止", hotwords);
        Assert.DoesNotContain(hotwords, static value =>
            value?.Contains("C:\\Users", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task FallsBackToPlainFlashRecognitionWhenTheResourceRejectsContext()
    {
        var handler = new ContextFallbackHandler();
        using var client = new HttpClient(handler);
        var credentials = JsonSerializer.Serialize(
            new AsrCredentials("apiKey", ApiKey: "test-api-key"),
            JsonDefaults.Strict);
        var provider = new VolcengineFlashAsrProvider(client, () => credentials);

        var first = await provider.TranscribeAsync(
            CreateWav(),
            new AsrRecognitionContext(["Velia", "停止"]),
            CancellationToken.None);
        var second = await provider.TranscribeAsync(
            CreateWav(),
            new AsrRecognitionContext(["Velia", "停止"]),
            CancellationToken.None);

        Assert.Equal("回退成功", first.Text);
        Assert.Equal("回退成功", second.Text);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Contains("\"context\":\"", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"context\":null", handler.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("\"context\":null", handler.Bodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapsAuthenticationFailureWithoutLeakingProviderBody()
    {
        using var client = new HttpClient(new RecordingHandler(HttpStatusCode.Forbidden, "not authorized"));
        var credentials = JsonSerializer.Serialize(new AsrCredentials("legacy", AppId: "test-app", AccessToken: "test-token"), JsonDefaults.Strict);
        var provider = new VolcengineFlashAsrProvider(client, () => credentials);

        var exception = await Assert.ThrowsAsync<AsrProviderException>(() =>
            provider.TranscribeAsync(CreateWav(), CancellationToken.None));

        Assert.Equal("asr_authentication_failed", exception.Code);
        Assert.DoesNotContain("token", exception.Message, StringComparison.Ordinal);
    }

    private static byte[] CreateWav()
    {
        const int dataLength = 3_200;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + dataLength); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(16_000); writer.Write(32_000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        return stream.ToArray();
    }

    private sealed class RecordingHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string responseBody = "{\"result\":{\"text\":\"你好，Velia。\"}}") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        private string? requestBody;

        public Task<string> ReadRequestBodyAsync() => Task.FromResult(requestBody ?? string.Empty);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            requestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("X-Api-Status-Code", statusCode == HttpStatusCode.OK ? "20000000" : "45000001");
            response.Headers.TryAddWithoutValidation("X-Api-Message", statusCode == HttpStatusCode.OK ? "OK" : "Denied");
            response.Headers.TryAddWithoutValidation("X-Tt-Logid", "test-log-id");
            return response;
        }
    }

    private sealed class ContextFallbackHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            var rejectsContext = Bodies.Count == 1;
            var response = new HttpResponseMessage(rejectsContext
                ? HttpStatusCode.BadRequest
                : HttpStatusCode.OK)
            {
                Content = new StringContent(
                    rejectsContext ? "{}" : "{\"result\":{\"text\":\"回退成功\"}}",
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.TryAddWithoutValidation(
                "X-Api-Status-Code",
                rejectsContext ? "45000001" : "20000000");
            response.Headers.TryAddWithoutValidation(
                "X-Api-Message",
                rejectsContext ? "invalid context" : "OK");
            response.Headers.TryAddWithoutValidation("X-Tt-Logid", $"fallback-{Bodies.Count}");
            return response;
        }
    }
}
