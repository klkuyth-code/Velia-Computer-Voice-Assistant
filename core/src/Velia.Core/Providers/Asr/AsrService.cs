using Velia.Core.Security;
using Velia.Protocol;

namespace Velia.Core.Providers.Asr;

internal sealed class AsrService(
    ISecretStore secrets,
    IAsrProvider provider,
    IAsrTranscriptPostProcessor? transcriptPostProcessor = null,
    IAsrRecognitionContextProvider? recognitionContextProvider = null)
{
    private const int MaximumBase64Characters = 940_000;

    public async Task<VoiceTranscriptionResult> TranscribeAsync(
        string wavBase64,
        int? maxLatencyMs,
        CancellationToken cancellationToken)
    {
        if (!secrets.IsConfigured(ProviderIds.Asr))
            throw new AsrProviderException("asr_not_configured", "请先配置火山语音识别凭据。", false);
        if (string.IsNullOrWhiteSpace(wavBase64) || wavBase64.Length > MaximumBase64Characters)
            throw new AsrProviderException("invalid_audio", "录音数据为空或超过安全限制。", false);

        byte[] audio;
        try { audio = Convert.FromBase64String(wavBase64); }
        catch (FormatException) { throw new AsrProviderException("invalid_audio", "录音数据编码无效。", false); }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (maxLatencyMs is > 0)
            deadline.CancelAfter(Math.Clamp(maxLatencyMs.Value, 1_000, 20_000));

        try
        {
            var recognitionContext = recognitionContextProvider?.GetCurrent() ?? AsrRecognitionContext.Empty;
            var result = await provider.TranscribeAsync(
                audio,
                recognitionContext,
                deadline.Token).ConfigureAwait(false);
            var text = transcriptPostProcessor?.Process(result.Text) ?? result.Text;
            return new VoiceTranscriptionResult(text, result.AudioDurationMs, result.ProviderLatencyMs);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new AsrProviderException("asr_timeout", "实时语音识别超过响应窗口，已丢弃这段旧语音。", true);
        }
        finally
        {
            Array.Clear(audio);
        }
    }

    public async Task<AsrTestResult> TestAsync(CancellationToken cancellationToken)
    {
        if (!secrets.IsConfigured(ProviderIds.Asr))
            return Failure("asr_not_configured", "请先保存火山语音凭据。", false);
        try
        {
            var wav = CreateSilentWav(milliseconds: 120);
            await provider.TranscribeAsync(wav, cancellationToken).ConfigureAwait(false);
            return new AsrTestResult("ready", DateTimeOffset.UtcNow, "连接成功，火山语音已就绪。");
        }
        catch (AsrProviderException exception) when (exception.Code == "empty_transcription")
        {
            return new AsrTestResult("ready", DateTimeOffset.UtcNow, "连接成功，火山语音已就绪。");
        }
        catch (AsrProviderException exception)
        {
            return Failure(exception.Code, exception.Message, exception.Retryable);
        }
    }

    private static AsrTestResult Failure(string code, string message, bool retryable) =>
        new("error", DateTimeOffset.UtcNow, message, code, retryable);

    private static byte[] CreateSilentWav(int milliseconds)
    {
        var pcmLength = 16_000 * 2 * milliseconds / 1_000;
        using var stream = new MemoryStream(44 + pcmLength);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcmLength);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(32_000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcmLength);
        writer.Write(new byte[pcmLength]);
        return stream.ToArray();
    }
}
