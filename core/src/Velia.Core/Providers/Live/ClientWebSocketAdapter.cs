using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Velia.Core.Providers.Live;

internal sealed class ClientWebSocketFactory : ILiveSocketFactory
{
    public ILiveSocket Create() => new ClientWebSocketAdapter(WebRequest.GetSystemWebProxy());
}

internal sealed class ClientWebSocketAdapter : ILiveSocket
{
    private const int MaximumMessageBytes = 1_048_576;
    private readonly ClientWebSocket socket = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SemaphoreSlim sendGate = new(1, 1);

    public ClientWebSocketAdapter(IWebProxy? proxy = null)
    {
        socket.Options.Proxy = proxy;
    }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        socket.ConnectAsync(uri, cancellationToken);

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16_384];
        using var output = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw MapClose(result.CloseStatus, result.CloseStatusDescription);
            }
            // Gemini can deliver its JSON envelope in either text frames or
            // binary frames containing UTF-8. Other binary payloads still fail
            // strict UTF-8/JSON validation in the protocol parser.
            if (result.MessageType is not (WebSocketMessageType.Text or WebSocketMessageType.Binary))
            {
                throw new LiveProviderException("live_protocol_error", "Gemini Live 返回了不支持的数据格式。", true);
            }
            if (output.Length + result.Count > MaximumMessageBytes)
            {
                throw new LiveProviderException("live_message_too_large", "Gemini Live 返回的数据超过安全限制。", false);
            }
            output.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                try { return StrictUtf8.GetString(output.GetBuffer(), 0, checked((int)output.Length)); }
                catch (DecoderFallbackException exception)
                {
                    throw new LiveProviderException("live_protocol_error", "Gemini Live 返回了无效的 UTF-8 数据。", true, exception);
                }
            }
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "session complete", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await CloseAsync(timeout.Token).ConfigureAwait(false); }
        catch (Exception) { }
        socket.Dispose();
        sendGate.Dispose();
    }

    private static LiveProviderException MapClose(WebSocketCloseStatus? status, string? description)
    {
        var normalized = description?.ToUpperInvariant() ?? string.Empty;
        if (normalized.Contains("INVALID_ARGUMENT", StringComparison.Ordinal) || status == WebSocketCloseStatus.InvalidPayloadData)
            return new LiveProviderException("live_setup_rejected", "Gemini Live 拒绝了会话配置。", false);
        if (normalized.Contains("PERMISSION_DENIED", StringComparison.Ordinal) ||
            normalized.Contains("UNAUTHENTICATED", StringComparison.Ordinal) || status == WebSocketCloseStatus.PolicyViolation)
            return new LiveProviderException("live_permission_denied", "当前 Gemini API Key 没有 Live API 权限。", false);
        if (normalized.Contains("RESOURCE_EXHAUSTED", StringComparison.Ordinal))
            return new LiveProviderException("live_quota_exhausted", "Gemini Live 配额或余额不足。", true);
        if (normalized.Contains("NOT_FOUND", StringComparison.Ordinal))
            return new LiveProviderException("live_model_unavailable", "当前账户无法使用所选 Gemini Live 模型。", false);
        return new LiveProviderException("live_connection_closed", "Gemini Live 服务器关闭了连接。", true);
    }
}
