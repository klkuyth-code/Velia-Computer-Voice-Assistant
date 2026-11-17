using Velia.Core.Security;
using Velia.Core.Rpc;
using Velia.Core.Conversations;
using Velia.Protocol;

namespace Velia.Core.Providers.Live;

internal sealed class GeminiLiveService(
    ISecretStore secrets,
    ILiveSocketFactory sockets,
    ILiveEventSink events,
    IDelegatedTaskExecutor taskExecutor,
    ILiveSemanticRouter? semanticRouter = null,
    ILiveTimingSink? timingSink = null,
    TimeSpan? providerInputLivenessTimeout = null,
    TimeSpan? providerRecoveryResponseTimeout = null) : IAsyncDisposable
{
    internal const string InputMimeType = "audio/pcm;rate=16000";
    internal const string VideoMimeType = "image/jpeg";
    internal const int MaximumInputBytes = 2_560;
    internal const int MaximumVideoBytes = 4 * 1024 * 1024;
    internal const int MaximumToolResultCharacters = 32_768;
    private readonly SemaphoreSlim gate = new(1, 1);
    private GeminiLiveSession? active;

    public async Task<LiveStartResult> StartAsync(LiveStartParams parameters, CancellationToken cancellationToken)
    {
        if (!LiveModels.All.Contains(parameters.Model))
            throw new LiveProviderException("live_model_not_allowed", "Live 模型不在允许列表中。", false);
        if (!LiveVoiceNames.All.Contains(parameters.VoiceName))
            throw new LiveProviderException("live_voice_not_allowed", "Live 音色配置无效。", false);
        var key = secrets.Load(ProviderIds.Vision);
        if (string.IsNullOrWhiteSpace(key))
            throw new LiveProviderException("live_not_configured", "请先在“屏幕视觉”中配置 Gemini API Key。", false);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active is not null) throw new LiveProviderException("live_already_active", "已有 Live 会话正在运行。", false);
            var session = new GeminiLiveSession(
                Guid.NewGuid(),
                parameters.Model,
                parameters.VoiceName,
                key,
                sockets,
                events,
                taskExecutor,
                semanticRouter,
                timingSink,
                providerInputLivenessTimeout,
                providerRecoveryResponseTimeout);
            active = session;
            try { await session.StartAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                active = null;
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            return new LiveStartResult(session.Id, session.Model, 16_000, 24_000);
        }
        finally { gate.Release(); }
    }

    public LiveSendAudioResult SendAudio(LiveSendAudioParams parameters)
    {
        var session = active;
        if (session is null || session.Id != parameters.SessionId)
            throw new LiveProviderException("live_session_not_found", "Live 会话不存在或已结束。", false);
        if (!string.Equals(parameters.MimeType, InputMimeType, StringComparison.Ordinal))
            throw new LiveProviderException("invalid_live_audio", "Live 音频格式必须是 16 kHz PCM。", false);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(parameters.AudioBase64); }
        catch (FormatException) { throw new LiveProviderException("invalid_live_audio", "Live 音频数据无效。", false); }
        if (bytes.Length is < 320 or > MaximumInputBytes || bytes.Length % 2 != 0)
            throw new LiveProviderException("invalid_live_audio", "Live 音频分片大小无效。", false);
        var accepted = session.TryEnqueueAudio(parameters.AudioBase64);
        return new LiveSendAudioResult(session.Id, accepted, session.BufferedChunks);
    }

    public async Task<LiveSendVideoResult> SendVideoAsync(
        LiveSendVideoParams parameters,
        CancellationToken cancellationToken)
    {
        var session = active;
        if (session is null || session.Id != parameters.SessionId)
            throw new LiveProviderException("live_session_not_found", "Live 会话不存在或已结束。", false);
        if (!string.Equals(parameters.MimeType, VideoMimeType, StringComparison.Ordinal))
            throw new LiveProviderException("invalid_live_video", "Live 画面必须使用 JPEG 格式。", false);

        byte[] bytes;
        try { bytes = Convert.FromBase64String(parameters.ImageBase64); }
        catch (FormatException) { throw new LiveProviderException("invalid_live_video", "Live 画面数据无效。", false); }
        try
        {
            if (bytes.Length is < 4 or > MaximumVideoBytes ||
                bytes[0] != 0xff || bytes[1] != 0xd8 ||
                bytes[^2] != 0xff || bytes[^1] != 0xd9)
            {
                throw new LiveProviderException("invalid_live_video", "Live 画面不是有效的 JPEG。", false);
            }
        }
        finally { Array.Clear(bytes); }

        var accepted = await session.SendVideoAsync(parameters.ImageBase64, cancellationToken).ConfigureAwait(false);
        return new LiveSendVideoResult(session.Id, accepted);
    }

    public async Task<LiveSubmitToolResultResult> SubmitToolResultAsync(
        LiveSubmitToolResultParams parameters,
        CancellationToken cancellationToken)
    {
        var session = active;
        if (session is null || session.Id != parameters.SessionId)
            return new LiveSubmitToolResultResult(parameters.SessionId, parameters.CallId, false);
        if (parameters.Name != GeminiLiveProtocol.CaptureCurrentScreenTool ||
            string.IsNullOrWhiteSpace(parameters.CallId) || parameters.CallId.Length > 256 ||
            parameters.Ok == (parameters.Result is null) || parameters.Ok == (parameters.Error is not null))
        {
            throw new LiveProviderException("invalid_live_tool_result", "Live 视觉工具结果无效。", false);
        }
        var value = parameters.Ok ? parameters.Result! : parameters.Error!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumToolResultCharacters)
            throw new LiveProviderException("invalid_live_tool_result", "Live 视觉工具结果为空或超过限制。", false);

        var accepted = await session.SubmitToolResultAsync(
            parameters.CallId, parameters.Name, parameters.Ok, value, cancellationToken).ConfigureAwait(false);
        return new LiveSubmitToolResultResult(session.Id, parameters.CallId, accepted);
    }

    public async Task<LiveSubmitLocalTranscriptResult> SubmitLocalTranscriptAsync(
        LiveSubmitLocalTranscriptParams parameters,
        CancellationToken cancellationToken)
    {
        var session = active;
        if (session is null || session.Id != parameters.SessionId)
            return new LiveSubmitLocalTranscriptResult(parameters.SessionId, false, false, false);
        var transcript = parameters.Transcript.Trim();
        if (transcript.Length is < 1 or > 32_768)
            throw new LiveProviderException("invalid_live_local_transcript", "本地语音转写无效。", false);
        var utteranceId = string.IsNullOrWhiteSpace(parameters.UtteranceId)
            ? Guid.NewGuid().ToString("D")
            : parameters.UtteranceId.Trim();
        if (!Guid.TryParse(utteranceId, out _) || utteranceId.Length > 64)
            throw new LiveProviderException("invalid_live_utterance_id", "本地语音片段标识无效。", false);
        var capturedAtUtc = parameters.CapturedAtUtc ?? DateTimeOffset.UtcNow;
        if (Math.Abs((DateTimeOffset.UtcNow - capturedAtUtc).TotalMinutes) > 5)
            throw new LiveProviderException("invalid_live_utterance_time", "本地语音片段时间无效。", false);
        return await session.SubmitLocalTranscriptAsync(
            transcript,
            utteranceId,
            capturedAtUtc,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<LiveStopResult> StopAsync(LiveStopParams parameters)
    {
        GeminiLiveSession? session;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            session = active;
            if (session is null || session.Id != parameters.SessionId)
                return new LiveStopResult(parameters.SessionId, false);
            active = null;
        }
        finally { gate.Release(); }
        await session.DisposeAsync().ConfigureAwait(false);
        return new LiveStopResult(parameters.SessionId, true);
    }

    public LiveStatusResult GetStatus() => active is { } session
        ? new LiveStatusResult(session.State, session.Id, session.Model)
        : new LiveStatusResult(LiveStates.Inactive);

    public async ValueTask DisposeAsync()
    {
        var session = active;
        active = null;
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        gate.Dispose();
    }
}
