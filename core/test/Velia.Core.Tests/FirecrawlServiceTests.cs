using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Velia.Core.Security;
using Velia.Core.Web;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class FirecrawlServiceTests
{
    private const string ApiKey = "fc-secrettoken123";

    [Fact]
    public async Task TestsTheFixedCreditEndpointWithBearerAuthenticationWithoutLeakingTheKey()
    {
        var handler = new RecordingHandler(
            Response(HttpStatusCode.OK, """{"success":true,"data":{"remainingCredits":37}}"""));
        using var client = new HttpClient(handler);
        var service = CreateService(client);

        var result = await service.TestAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.firecrawl.dev/v2/team/credit-usage", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal(ApiKey, request.AuthorizationParameter);
        Assert.Null(request.Body);
        Assert.Equal("ready", result.State);
        Assert.Equal(37, result.RemainingCredits);
        Assert.DoesNotContain(ApiKey, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchUsesOnlyTheFixedV2EndpointAndBoundedRequestBody()
    {
        var handler = new RecordingHandler(Response(HttpStatusCode.OK, """
            {
              "success":true,
              "data":{"web":[
                {"title":"Velia","description":"公开说明","url":"https://openai.com/research/#ignored"},
                {"title":"第二条","description":"说明","url":"https://www.microsoft.com/windows/"}
              ]},
              "creditsUsed":1
            }
            """));
        using var client = new HttpClient(handler);
        var service = CreateService(client);
        var operationId = Guid.NewGuid();

        var value = await service.ExecuteAsync(
            operationId,
            new FirecrawlCommand(FirecrawlActions.Search, "Velia Firecrawl", 2),
            CancellationToken.None);

        var result = Assert.IsType<FirecrawlSearchResult>(value);
        Assert.Equal("firecrawl", result.Source);
        Assert.True(result.UntrustedContent);
        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, static item => Assert.NotEqual(Guid.Empty, item.ResultId));
        Assert.Equal("https://openai.com/research/", result.Results[0].Url);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.firecrawl.dev/v2/search", request.Uri.AbsoluteUri);
        using var body = JsonDocument.Parse(Assert.IsType<string>(request.Body));
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        Assert.Equal("Velia Firecrawl", body.RootElement.GetProperty("query").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task ResultIdsAreScopedToOneOperationAndReleaseInvalidatesThem()
    {
        var handler = new RecordingHandler(
            Response(HttpStatusCode.OK, """
                {"success":true,"data":{"web":[{"title":"文章","description":"摘要","url":"https://openai.com/research/"}]}}
                """),
            Response(HttpStatusCode.OK, """
                {"success":true,"data":{"markdown":"正文","metadata":{"title":"文章","description":"摘要","statusCode":200}}}
                """));
        using var client = new HttpClient(handler);
        var service = CreateService(client);
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();

        var search = Assert.IsType<FirecrawlSearchResult>(await service.ExecuteAsync(
            owner,
            new FirecrawlCommand(FirecrawlActions.Search, "公开文章", 1),
            CancellationToken.None));
        var resultId = Assert.Single(search.Results).ResultId;

        var wrongOperation = await Assert.ThrowsAsync<FirecrawlException>(() => service.ExecuteAsync(
            other,
            new FirecrawlCommand(FirecrawlActions.Scrape, ResultId: resultId),
            CancellationToken.None));
        Assert.Equal("web_result_not_available", wrongOperation.Code);
        Assert.Single(handler.Requests);

        var scrape = Assert.IsType<FirecrawlScrapeResult>(await service.ExecuteAsync(
            owner,
            new FirecrawlCommand(FirecrawlActions.Scrape, ResultId: resultId),
            CancellationToken.None));
        Assert.Equal("正文", scrape.Markdown);
        Assert.Equal(200, scrape.StatusCode);

        var scrapeRequest = handler.Requests[1];
        Assert.Equal("https://api.firecrawl.dev/v2/scrape", scrapeRequest.Uri.AbsoluteUri);
        using (var body = JsonDocument.Parse(Assert.IsType<string>(scrapeRequest.Body)))
        {
            Assert.Equal(5, body.RootElement.EnumerateObject().Count());
            Assert.Equal("https://openai.com/research/", body.RootElement.GetProperty("url").GetString());
            Assert.Equal("markdown", body.RootElement.GetProperty("formats")[0].GetString());
            Assert.True(body.RootElement.GetProperty("onlyMainContent").GetBoolean());
            Assert.True(body.RootElement.GetProperty("removeBase64Images").GetBoolean());
            Assert.Equal(0, body.RootElement.GetProperty("maxAge").GetInt32());
        }

        service.Release(owner);
        var released = await Assert.ThrowsAsync<FirecrawlException>(() => service.ExecuteAsync(
            owner,
            new FirecrawlCommand(FirecrawlActions.Scrape, ResultId: resultId),
            CancellationToken.None));
        Assert.Equal("web_result_not_available", released.Code);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("http://127.0.0.1/private")]
    [InlineData("http://10.0.0.8/private")]
    [InlineData("http://[::1]/private")]
    [InlineData("https://localhost/private")]
    [InlineData("https://service.internal/private")]
    [InlineData("https://user:password@openai.com/private")]
    [InlineData("https://openai.com:8443/private")]
    [InlineData("file:///C:/private.txt")]
    public async Task RejectsNonPublicOrNonStandardUrlsBeforeAnyNetworkRequest(string url)
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var service = CreateService(client);

        var exception = await Assert.ThrowsAsync<FirecrawlException>(() => service.ExecuteAsync(
            Guid.NewGuid(),
            new FirecrawlCommand(FirecrawlActions.Scrape, Url: url),
            CancellationToken.None));

        Assert.Equal("web_url_rejected", exception.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RejectsAHostThatDnsResolvesToAPrivateAddress()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var service = CreateService(
            client,
            static (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.20") }));

        var exception = await Assert.ThrowsAsync<FirecrawlException>(() => service.ExecuteAsync(
            Guid.NewGuid(),
            new FirecrawlCommand(FirecrawlActions.Scrape, Url: "https://public-host.invalid-domain.example.org/page"),
            CancellationToken.None));

        Assert.Equal("web_url_rejected", exception.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TruncatesMarkdownAtTheDocumentBoundary()
    {
        var markdown = new string('测', 120_037);
        var responseBody = JsonSerializer.Serialize(new
        {
            success = true,
            data = new
            {
                markdown,
                metadata = new { title = "长文章", statusCode = 200 },
            },
        });
        var handler = new RecordingHandler(Response(HttpStatusCode.OK, responseBody));
        using var client = new HttpClient(handler);
        var service = CreateService(client);

        var value = await service.ExecuteAsync(
            Guid.NewGuid(),
            new FirecrawlCommand(FirecrawlActions.Scrape, Url: "https://openai.com/research/"),
            CancellationToken.None);

        var result = Assert.IsType<FirecrawlScrapeResult>(value);
        Assert.True(result.Truncated);
        Assert.Equal(120_000, result.Markdown.Length);
        Assert.Equal(new string('测', 120_000), result.Markdown);
    }

    [Fact]
    public async Task RejectsAnOversizedResponseBeforeParsingIt()
    {
        var oversized = "{\"success\":true,\"padding\":\"" + new string('x', 1_048_576) + "\"}";
        var handler = new RecordingHandler(Response(HttpStatusCode.OK, oversized));
        using var client = new HttpClient(handler);
        var service = CreateService(client);

        var result = await service.TestAsync(CancellationToken.None);

        Assert.Equal("error", result.State);
        Assert.Equal("web_response_too_large", result.ErrorCode);
        Assert.False(result.Retryable);
    }

    [Theory]
    [InlineData(401, "web_authentication_failed", false, 1)]
    [InlineData(402, "web_billing_unavailable", false, 1)]
    [InlineData(408, "web_timeout", true, 2)]
    [InlineData(429, "web_rate_limited", true, 2)]
    [InlineData(500, "web_unavailable", true, 2)]
    [InlineData(502, "web_unavailable", true, 2)]
    [InlineData(503, "web_unavailable", true, 2)]
    [InlineData(504, "web_unavailable", true, 2)]
    public async Task MapsProviderFailuresWithoutLeakingBodiesOrKeys(
        int statusCode,
        string expectedCode,
        bool retryable,
        int expectedRequests)
    {
        var responses = Enumerable.Range(0, expectedRequests)
            .Select(_ => Response((HttpStatusCode)statusCode, $"raw diagnostics containing {ApiKey}"))
            .ToArray();
        var handler = new RecordingHandler(responses);
        using var client = new HttpClient(handler);
        var delays = new List<TimeSpan>();
        var service = CreateService(
            client,
            retryDelay: (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await service.TestAsync(CancellationToken.None);

        Assert.Equal("error", result.State);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(expectedRequests, handler.Requests.Count);
        Assert.Equal(retryable ? 1 : 0, delays.Count);
        Assert.DoesNotContain("raw diagnostics", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetriesOneTransientFailureAndCapsRetryAfterBeforeSucceeding()
    {
        var first = Response(HttpStatusCode.TooManyRequests, "busy");
        first.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        var handler = new RecordingHandler(
            first,
            Response(HttpStatusCode.OK, """{"success":true,"remainingCredits":9}"""));
        using var client = new HttpClient(handler);
        var delays = new List<TimeSpan>();
        var service = CreateService(
            client,
            retryDelay: (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await service.TestAsync(CancellationToken.None);

        Assert.Equal("ready", result.State);
        Assert.Equal(9, result.RemainingCredits);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(delays));
    }

    private static FirecrawlService CreateService(
        HttpClient client,
        FirecrawlHostResolver? hostResolver = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        var secrets = new MemorySecretStore();
        secrets.Save(ProviderIds.Web, ApiKey);
        return new FirecrawlService(
            client,
            secrets,
            hostResolver ?? ((_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") })),
            retryDelay);
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string? Body);

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestSnapshot(
                request.Method,
                Assert.IsType<Uri>(request.RequestUri),
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                body));
            return responses.Count > 0
                ? responses.Dequeue()
                : throw new InvalidOperationException("The test did not provide an HTTP response.");
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
