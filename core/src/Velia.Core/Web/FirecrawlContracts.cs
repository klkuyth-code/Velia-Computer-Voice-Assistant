using System.Net;
using Velia.Protocol;

namespace Velia.Core.Web;

internal static class FirecrawlActions
{
    public const string Search = "search";
    public const string Scrape = "scrape";
}

internal sealed record FirecrawlCommand(
    string Action,
    string? Query = null,
    int? Limit = null,
    Guid? ResultId = null,
    string? Url = null);

internal sealed record FirecrawlSearchItem(
    Guid ResultId,
    string Title,
    string Description,
    string Url);

internal sealed record FirecrawlSearchResult(
    string Action,
    string Source,
    bool UntrustedContent,
    IReadOnlyList<FirecrawlSearchItem> Results,
    int? CreditsUsed = null,
    string? Warning = null);

internal sealed record FirecrawlScrapeResult(
    string Action,
    string Source,
    bool UntrustedContent,
    string Url,
    string Title,
    string Description,
    string Markdown,
    bool Truncated,
    int? StatusCode = null);

internal sealed class FirecrawlException(
    string code,
    string message,
    bool retryable = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

internal interface IFirecrawlService
{
    Task<WebTestResult> TestAsync(CancellationToken cancellationToken);
    Task<object> ExecuteAsync(Guid operationId, FirecrawlCommand command, CancellationToken cancellationToken);
    void Release(Guid operationId);
}

internal delegate Task<IPAddress[]> FirecrawlHostResolver(
    string host,
    CancellationToken cancellationToken);
