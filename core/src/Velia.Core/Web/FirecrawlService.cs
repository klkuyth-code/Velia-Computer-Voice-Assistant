using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Velia.Core.Security;
using Velia.Protocol;

namespace Velia.Core.Web;

internal sealed class FirecrawlService : IFirecrawlService
{
    private const string ApiRoot = "https://api.firecrawl.dev/";
    private const int MaximumApiKeyCharacters = 16_384;
    private const int MaximumResponseBytes = 1_048_576;
    private const int MaximumMarkdownCharacters = 120_000;
    private const int MaximumTitleCharacters = 512;
    private const int MaximumDescriptionCharacters = 4_096;
    private const int MaximumWarningCharacters = 1_000;
    private const int MaximumUrlCharacters = 2_048;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(70);
    private static readonly TimeSpan ScrapeTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly string[] MarkdownFormat = ["markdown"];
    private static readonly string[] BlockedDnsSuffixes =
    [
        ".localhost", ".local", ".internal", ".home", ".lan", ".test", ".invalid", ".example",
    ];

    private readonly HttpClient httpClient;
    private readonly ISecretStore secrets;
    private readonly FirecrawlHostResolver resolveHost;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Uri>> operationResults = new();

    public FirecrawlService(
        HttpClient httpClient,
        ISecretStore secrets,
        FirecrawlHostResolver? hostResolver = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        resolveHost = hostResolver ?? ResolveHostAsync;
        delayAsync = retryDelay ?? Task.Delay;
    }

    public async Task<WebTestResult> TestAsync(CancellationToken cancellationToken)
    {
        var testedAt = DateTimeOffset.UtcNow;
        try
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                "v2/team/credit-usage",
                payload: null,
                TestTimeout,
                cancellationToken).ConfigureAwait(false);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            EnsureSuccessfulEnvelope(document.RootElement);
            var remaining = ReadRemainingCredits(document.RootElement);
            if (remaining == 0)
            {
                return new WebTestResult(
                    "error",
                    testedAt,
                    "Firecrawl 密钥有效，但当前剩余额度为 0。",
                    0,
                    "web_billing_unavailable",
                    false);
            }
            return new WebTestResult(
                "ready",
                testedAt,
                "Firecrawl 连接正常。",
                remaining);
        }
        catch (FirecrawlException exception)
        {
            return new WebTestResult(
                "error",
                testedAt,
                exception.Message,
                ErrorCode: exception.Code,
                Retryable: exception.Retryable);
        }
    }

    public Task<object> ExecuteAsync(
        Guid operationId,
        FirecrawlCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Action switch
        {
            FirecrawlActions.Search => SearchAsync(operationId, command, cancellationToken),
            FirecrawlActions.Scrape => ScrapeAsync(operationId, command, cancellationToken),
            _ => throw new FirecrawlException("invalid_web_research", "网页检索参数无效。"),
        };
    }

    public void Release(Guid operationId) => operationResults.TryRemove(operationId, out _);

    private async Task<object> SearchAsync(
        Guid operationId,
        FirecrawlCommand command,
        CancellationToken cancellationToken)
    {
        var query = command.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 500 || command.Limit is not (>= 1 and <= 5) ||
            command.ResultId is not null || command.Url is not null)
        {
            throw new FirecrawlException("invalid_web_research", "搜索参数无效。" );
        }

        using var response = await SendAsync(
            HttpMethod.Post,
            "v2/search",
            new { query, limit = command.Limit.Value },
            SearchTimeout,
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        EnsureSuccessfulEnvelope(root);

        var results = new List<FirecrawlSearchItem>(command.Limit.Value);
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("web", out var web) && web.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in web.EnumerateArray())
            {
                if (results.Count >= command.Limit.Value || item.ValueKind != JsonValueKind.Object) break;
                var rawUrl = ReadString(item, "url", MaximumUrlCharacters);
                if (rawUrl is null || !TryNormalizePublicUri(rawUrl, out var uri)) continue;
                var resultId = Guid.NewGuid();
                var map = operationResults.GetOrAdd(
                    operationId,
                    static _ => new ConcurrentDictionary<Guid, Uri>());
                if (!map.TryAdd(resultId, uri)) continue;
                results.Add(new FirecrawlSearchItem(
                    resultId,
                    ReadString(item, "title", MaximumTitleCharacters) ?? uri.Host,
                    ReadString(item, "description", MaximumDescriptionCharacters) ?? string.Empty,
                    uri.AbsoluteUri));
            }
        }

        return new FirecrawlSearchResult(
            FirecrawlActions.Search,
            "firecrawl",
            true,
            results,
            ReadInt32(root, "creditsUsed"),
            ReadString(root, "warning", MaximumWarningCharacters));
    }

    private async Task<object> ScrapeAsync(
        Guid operationId,
        FirecrawlCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Query is not null || command.Limit is not null ||
            (command.ResultId is null) == (command.Url is null))
        {
            throw new FirecrawlException("invalid_web_research", "网页抓取参数无效。" );
        }

        Uri uri;
        if (command.ResultId is { } resultId)
        {
            if (!operationResults.TryGetValue(operationId, out var map) || !map.TryGetValue(resultId, out uri!))
            {
                throw new FirecrawlException(
                    "web_result_not_available",
                    "该搜索结果不属于当前任务或已经失效，请重新搜索。" );
            }
        }
        else if (!TryNormalizePublicUri(command.Url!, out uri!))
        {
            throw new FirecrawlException("web_url_rejected", "只允许抓取用户明确提供的公网 HTTP 或 HTTPS 网址。" );
        }

        await EnsurePublicHostAsync(uri, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(
            HttpMethod.Post,
            "v2/scrape",
            new
            {
                url = uri.AbsoluteUri,
                formats = MarkdownFormat,
                onlyMainContent = true,
                removeBase64Images = true,
                maxAge = 0,
            },
            ScrapeTimeout,
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        EnsureSuccessfulEnvelope(root);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new FirecrawlException("web_invalid_response", "Firecrawl 返回了无法解析的结果。", true);
        }

        var markdown = ReadString(data, "markdown", MaximumMarkdownCharacters, out var truncated);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new FirecrawlException("web_empty_result", "Firecrawl 没有返回可用的网页正文。", true);
        }
        var metadata = data.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Object
            ? metadataElement
            : default;
        var title = metadata.ValueKind == JsonValueKind.Object
            ? ReadString(metadata, "title", MaximumTitleCharacters) ?? ReadString(metadata, "ogTitle", MaximumTitleCharacters)
            : null;
        var description = metadata.ValueKind == JsonValueKind.Object
            ? ReadString(metadata, "description", MaximumDescriptionCharacters) ?? ReadString(metadata, "ogDescription", MaximumDescriptionCharacters)
            : null;
        var statusCode = metadata.ValueKind == JsonValueKind.Object ? ReadInt32(metadata, "statusCode") : null;

        return new FirecrawlScrapeResult(
            FirecrawlActions.Scrape,
            "firecrawl",
            true,
            uri.AbsoluteUri,
            title ?? uri.Host,
            description ?? string.Empty,
            markdown,
            truncated,
            statusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        object? payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var apiKey = GetApiKey();
        byte[]? json = payload is null ? null : JsonSerializer.SerializeToUtf8Bytes(payload, JsonDefaults.Strict);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(method, new Uri(new Uri(ApiRoot), relativePath));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (json is not null)
            {
                request.Content = new ByteArrayContent(json);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FirecrawlException("web_timeout", "Firecrawl 请求超时，请稍后重试。", true, exception);
            }
            catch (HttpRequestException exception)
            {
                throw new FirecrawlException("web_unreachable", "无法连接 Firecrawl 服务，请检查网络。", true, exception);
            }

            if (response.IsSuccessStatusCode) return response;
            var failure = MapFailure(response.StatusCode);
            if (attempt == 0 && failure.Retryable)
            {
                var delay = RetryDelay(response);
                response.Dispose();
                await delayAsync(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }
            response.Dispose();
            throw failure;
        }
        throw new FirecrawlException("web_unavailable", "Firecrawl 服务暂时不可用，请稍后重试。", true);
    }

    private string GetApiKey()
    {
        string? stored;
        try
        {
            stored = secrets.Load(ProviderIds.Web);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new FirecrawlException(
                "web_secret_unavailable",
                "无法读取本机保存的 Firecrawl 密钥，请删除后重新保存。",
                false,
                exception);
        }
        var apiKey = stored?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new FirecrawlException("web_not_configured", "请先保存 Firecrawl API 密钥。" );
        }
        if (apiKey.Length is < 8 or > MaximumApiKeyCharacters ||
            !apiKey.StartsWith("fc-", StringComparison.Ordinal) ||
            !HasValidApiKeyCharacters(apiKey.AsSpan(3)))
        {
            throw new FirecrawlException("web_authentication_failed", "Firecrawl API 密钥格式无效。" );
        }
        return apiKey;
    }

    private static bool HasValidApiKeyCharacters(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') return false;
        }
        return true;
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16_384];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumResponseBytes)
                {
                    throw new FirecrawlException("web_response_too_large", "Firecrawl 返回内容超过安全大小限制。" );
                }
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new FirecrawlException("web_invalid_response", "Firecrawl 返回了无法解析的结果。", true, exception);
        }
    }

    private static void EnsureSuccessfulEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
        {
            throw new FirecrawlException("web_invalid_response", "Firecrawl 返回了无法确认的结果。", true);
        }
    }

    private async Task EnsurePublicHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(uri.DnsSafeHost, out var literal))
        {
            if (!IsPublicAddress(literal))
                throw new FirecrawlException("web_url_rejected", "不能抓取本机、局域网或保留地址。" );
            return;
        }
        IPAddress[] addresses;
        try
        {
            addresses = await resolveHost(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            throw new FirecrawlException("web_url_unreachable", "无法确认该网址是可访问的公网地址。", true, exception);
        }
        if (addresses.Length == 0 || addresses.Any(static address => !IsPublicAddress(address)))
            throw new FirecrawlException("web_url_rejected", "不能抓取本机、局域网或保留地址。" );
    }

    private static bool TryNormalizePublicUri(string value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumUrlCharacters ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var candidate) ||
            candidate.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(candidate.Host) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !candidate.IsDefaultPort)
        {
            return false;
        }
        var host = candidate.DnsSafeHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            BlockedDnsSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (IPAddress.TryParse(host, out var address) && !IsPublicAddress(address)) return false;
        uri = new UriBuilder(candidate) { Host = host, Fragment = string.Empty }.Uri;
        return uri.AbsoluteUri.Length <= MaximumUrlCharacters;
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return !IPAddress.IPv6Any.Equals(address) && !IPAddress.IPv6Loopback.Equals(address) &&
                   !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast &&
                   (bytes[0] & 0xfe) != 0xfc &&
                   !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
        }
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var octets = address.GetAddressBytes();
        var first = octets[0];
        var second = octets[1];
        return first is not 0 and not 10 and not 127 && first < 224 &&
               !(first == 100 && second is >= 64 and <= 127) &&
               !(first == 169 && second == 254) &&
               !(first == 172 && second is >= 16 and <= 31) &&
               !(first == 192 && second is 0 or 168) &&
               !(first == 198 && second is 18 or 19) &&
               !(first == 198 && second == 51 && octets[2] == 100) &&
               !(first == 203 && second == 0 && octets[2] == 113);
    }

    private static FirecrawlException MapFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new("web_authentication_failed", "Firecrawl API 密钥无效或已被撤销。" ),
        HttpStatusCode.PaymentRequired =>
            new("web_billing_unavailable", "Firecrawl 额度不足，请先检查账户额度。" ),
        HttpStatusCode.RequestTimeout =>
            new("web_timeout", "Firecrawl 请求超时，请稍后重试。", true),
        HttpStatusCode.TooManyRequests =>
            new("web_rate_limited", "Firecrawl 请求过于频繁，请稍后重试。", true),
        _ when (int)statusCode >= 500 =>
            new("web_unavailable", "Firecrawl 服务暂时不可用，请稍后重试。", true),
        _ => new("web_request_rejected", "Firecrawl 拒绝了本次请求。" ),
    };

    private static TimeSpan RetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta ??
                    (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(250));
        if (delay < TimeSpan.Zero) return TimeSpan.Zero;
        return delay > MaximumRetryDelay ? MaximumRetryDelay : delay;
    }

    private static int? ReadRemainingCredits(JsonElement root)
    {
        var direct = ReadInt32(root, "remainingCredits");
        if (direct is not null) return direct;
        return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? ReadInt32(data, "remainingCredits")
            : null;
    }

    private static string? ReadString(JsonElement root, string propertyName, int maximumCharacters) =>
        ReadString(root, propertyName, maximumCharacters, out _);

    private static string? ReadString(
        JsonElement root,
        string propertyName,
        int maximumCharacters,
        out bool truncated)
    {
        truncated = false;
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length <= maximumCharacters) return text;
        truncated = true;
        return text[..maximumCharacters];
    }

    private static int? ReadInt32(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;

    private static Task<IPAddress[]> ResolveHostAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}
