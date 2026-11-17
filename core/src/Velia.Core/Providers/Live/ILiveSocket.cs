namespace Velia.Core.Providers.Live;

internal interface ILiveSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
    Task SendTextAsync(string text, CancellationToken cancellationToken);
    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
}

internal interface ILiveSocketFactory
{
    ILiveSocket Create();
}
