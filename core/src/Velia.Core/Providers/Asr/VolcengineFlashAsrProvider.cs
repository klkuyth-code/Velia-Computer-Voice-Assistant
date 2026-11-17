using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Velia.Protocol;

namespace Velia.Core.Providers.Asr;

internal sealed class VolcengineFlashAsrProvider(
    HttpClient httpClient,
    Func<string?> credentialsProvider) : IAsrProvider
{
    private static readonly Uri Endpoint = new(
        "https://openspeech.bytedance.com/api/v3/auc/bigmodel/recognize/flash",
        UriKind.Absolute);
    private const string ResourceId = "volc.bigasr.auc_turbo";
    private const int MaximumAudioBytes = 700_000;
    private int recognitionContextRejected;

    public async Task<AsrTranscription> TranscribeAsync(
        ReadOnlyMemory<byte> wavAudio,
        CancellationToken cancellationToken) =>
        await TranscribeAsync(wavAudio, AsrRecognitionContext.Empty, cancellationToken).ConfigureAwait(false);

    public async Task<AsrTranscription> TranscribeAsync(
        ReadOnlyMemory<byte> wavAudio,
        AsrRecognitionContext context,
        CancellationToken cancellationToken)
    {
        var canSendContext = context.Hotwords.Count > 0 &&
            Volatile.Read(ref recognitionContextRejected) == 0;
        try
        {
            return await TranscribeOnceAsync(
                wavAudio,
                canSendContext ? context : AsrRecognitionContext.Empty,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AsrProviderException exception) when (
            canSendContext && exception.Code == "asr_request_rejected")
        {
            // Request-level hotwords are an accuracy enhancement, not a hard
            // dependency. Some existing Volcengine resources reject the
            // context field even though the same account can use flash ASR.
            // Remember that capability result for this process and retry the
            // same audio once without context so speech never stops working.
            Interlocked.Exchange(ref recognitionContextRejected, 1);
            return await TranscribeOnceAsync(
                wavAudio,
                AsrRecognitionContext.Empty,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AsrTranscription> TranscribeOnceAsync(
        ReadOnlyMemory<byte> wavAudio,
        AsrRecognitionContext context,
        CancellationToken cancellationToken)
    {
        if (wavAudio.Length is 0 or > MaximumAudioBytes)
        {
            throw new AsrProviderException("audio_too_large", "单次录音最长支持约 20 秒。", false);
        }

        var info = WavPcm16Validator.Validate(wavAudio.Span);
        var credentials = ReadCredentials();
        var requestId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.TryAddWithoutValidation("X-Api-Resource-Id", ResourceId);
        request.Headers.TryAddWithoutValidation("X-Api-Request-Id", requestId);
        request.Headers.TryAddWithoutValidation("X-Api-Sequence", "-1");
        if (credentials.Mode == "apiKey")
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", credentials.ApiKey);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("X-Api-App-Key", credentials.AppId);
            request.Headers.TryAddWithoutValidation("X-Api-Access-Key", credentials.AccessToken);
        }

        request.Content = JsonContent.Create(new
        {
            user = new { uid = credentials.Mode == "apiKey" ? "velia-desktop" : credentials.AppId },
            audio = new { data = Convert.ToBase64String(wavAudio.Span) },
            request = CreateRecognitionRequest(context),
        });

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AsrProviderException("asr_timeout", "语音识别超时，请稍后重试。", true);
        }
        catch (HttpRequestException exception)
        {
            throw new AsrProviderException("asr_unreachable", "无法连接火山语音服务。", true, innerException: exception);
        }

        using (response)
        {
            stopwatch.Stop();
            var statusCode = Header(response, "X-Api-Status-Code");
            var statusMessage = Header(response, "X-Api-Message");
            var logId = Header(response, "X-Tt-Logid");
            if (!response.IsSuccessStatusCode || statusCode != "20000000")
            {
                throw MapFailure((int)response.StatusCode, statusCode, statusMessage, logId);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                var text = root.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("text", out var textElement) &&
                    textElement.ValueKind == JsonValueKind.String
                        ? textElement.GetString()?.Trim()
                        : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new AsrProviderException("empty_transcription", "没有识别到清晰语音，请靠近麦克风重试。", false, logId);
                }

                return new AsrTranscription(text, info.DurationMs, stopwatch.ElapsedMilliseconds);
            }
            catch (JsonException exception)
            {
                throw new AsrProviderException("asr_invalid_response", "火山语音服务返回了无法解析的结果。", true, logId, exception);
            }
        }
    }

    private static object CreateRecognitionRequest(AsrRecognitionContext context)
    {
        var hotwords = context.Hotwords
            .Where(IsSafeHotword)
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .Select(static word => new { word })
            .ToArray();
        var serializedContext = hotwords.Length == 0
            ? null
            : JsonSerializer.Serialize(new { hotwords }, JsonDefaults.Strict);
        return new
        {
            model_name = "bigmodel",
            enable_itn = true,
            enable_punc = true,
            enable_ddc = true,
            context = serializedContext,
        };
    }

    private static bool IsSafeHotword(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is >= 2 and <= 40 &&
        value.IndexOfAny(['\\', '/', ':', '\r', '\n', '\0']) < 0;

    private AsrCredentials ReadCredentials()
    {
        var serialized = credentialsProvider();
        if (string.IsNullOrWhiteSpace(serialized))
        {
            throw new AsrProviderException("asr_not_configured", "请先配置火山语音识别凭据。", false);
        }

        try
        {
            var value = JsonSerializer.Deserialize<AsrCredentials>(serialized, JsonDefaults.Strict)
                ?? throw new JsonException();
            if (value.Mode == "apiKey" && IsValid(value.ApiKey)) return value;
            if (value.Mode == "legacy" && IsValid(value.AppId) && IsValid(value.AccessToken)) return value;
        }
        catch (JsonException)
        {
        }

        throw new AsrProviderException("asr_credentials_invalid", "火山语音识别凭据格式无效。", false);
    }

    private static bool IsValid(string? value) => value is { Length: >= 4 and <= 16_384 } && !string.IsNullOrWhiteSpace(value);

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static AsrProviderException MapFailure(int httpStatus, string? statusCode, string? message, string? logId)
    {
        if (httpStatus is 401 or 403)
            return new AsrProviderException("asr_authentication_failed", "火山语音凭据无效，或当前应用未开通极速识别服务。", false, logId);
        if (httpStatus == 429)
            return new AsrProviderException("asr_rate_limited", "火山语音请求过于频繁，请稍后重试。", true, logId);
        return statusCode switch
        {
            "20000003" or "45000002" => new AsrProviderException("empty_transcription", "没有识别到清晰语音，请靠近麦克风重试。", false, logId),
            "45000151" => new AsrProviderException("unsupported_audio_format", "火山语音无法识别当前音频格式。", false, logId),
            "45000001" => new AsrProviderException("asr_request_rejected", "火山语音拒绝了当前识别请求，请检查服务开通状态。", false, logId),
            _ when httpStatus >= 500 || statusCode?.StartsWith("550", StringComparison.Ordinal) == true =>
                new AsrProviderException("asr_unavailable", "火山语音服务暂时不可用。", true, logId),
            _ => new AsrProviderException("asr_failed", string.IsNullOrWhiteSpace(message) ? "语音识别失败。" : $"语音识别失败：{message}", false, logId),
        };
    }
}
