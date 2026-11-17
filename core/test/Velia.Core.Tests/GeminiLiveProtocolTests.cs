using System.Text.Json;
using Velia.Core.Providers.Live;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class GeminiLiveProtocolTests
{
    private static readonly string[] CancelledCallIds = ["call-old"];

    [Fact]
    public void SetupUsesApprovedModelAudioVoiceTranscriptionAndResumption()
    {
        using var document = JsonDocument.Parse(GeminiLiveProtocol.CreateSetup(
            "gemini-3.1-flash-live-preview", LiveVoiceNames.Zephyr, "resume-123"));
        var setup = document.RootElement.GetProperty("setup");

        Assert.Equal("models/gemini-3.1-flash-live-preview", setup.GetProperty("model").GetString());
        Assert.Equal("AUDIO", setup.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString());
        Assert.Equal("Zephyr", setup.GetProperty("generationConfig").GetProperty("speechConfig")
            .GetProperty("voiceConfig").GetProperty("prebuiltVoiceConfig").GetProperty("voiceName").GetString());
        Assert.False(setup.TryGetProperty("tools", out _));
        var instructions = string.Join('\n', setup.GetProperty("systemInstruction").GetProperty("parts")
            .EnumerateArray().Select(part => part.GetProperty("text").GetString()));
        Assert.Contains("后台任务 Agent", instructions, StringComparison.Ordinal);
        Assert.Contains("VELIA_CORE_RESULT", instructions, StringComparison.Ordinal);
        Assert.True(setup.TryGetProperty("inputAudioTranscription", out _));
        Assert.True(setup.TryGetProperty("outputAudioTranscription", out _));
        var activity = setup.GetProperty("realtimeInputConfig");
        var automatic = activity.GetProperty("automaticActivityDetection");
        Assert.False(automatic.GetProperty("disabled").GetBoolean());
        Assert.Equal("START_SENSITIVITY_HIGH", automatic.GetProperty("startOfSpeechSensitivity").GetString());
        Assert.Equal(100, automatic.GetProperty("prefixPaddingMs").GetInt32());
        Assert.Equal("END_SENSITIVITY_HIGH", automatic.GetProperty("endOfSpeechSensitivity").GetString());
        Assert.Equal(420, automatic.GetProperty("silenceDurationMs").GetInt32());
        Assert.Equal("START_OF_ACTIVITY_INTERRUPTS", activity.GetProperty("activityHandling").GetString());
        Assert.Equal("TURN_INCLUDES_AUDIO_ACTIVITY_AND_ALL_VIDEO", activity.GetProperty("turnCoverage").GetString());
        Assert.Equal("resume-123", setup.GetProperty("sessionResumption").GetProperty("handle").GetString());
    }

    [Fact]
    public void RejectsUnapprovedLiveVoice()
    {
        Assert.Throws<ArgumentException>(() => GeminiLiveProtocol.CreateSetup(
            LiveModels.Gemini31FlashLivePreview, "unknown-voice"));
    }

    [Fact]
    public void SetupAllowsKoreLiveVoice()
    {
        using var document = JsonDocument.Parse(GeminiLiveProtocol.CreateSetup(
            LiveModels.Gemini31FlashLivePreview, LiveVoiceNames.Kore));

        Assert.Equal("Kore", document.RootElement.GetProperty("setup")
            .GetProperty("generationConfig").GetProperty("speechConfig")
            .GetProperty("voiceConfig").GetProperty("prebuiltVoiceConfig")
            .GetProperty("voiceName").GetString());
    }

    [Fact]
    public void VideoUsesBoundedRealtimeJpegShape()
    {
        using var document = JsonDocument.Parse(GeminiLiveProtocol.CreateVideo("/9j/2Q=="));
        var video = document.RootElement.GetProperty("realtimeInput").GetProperty("video");
        Assert.Equal("/9j/2Q==", video.GetProperty("data").GetString());
        Assert.Equal("image/jpeg", video.GetProperty("mimeType").GetString());
    }

    [Fact]
    public void TrustedCoreResultUsesRealtimeTextShape()
    {
        using var document = JsonDocument.Parse(GeminiLiveProtocol.CreateRealtimeText(
            "VELIA_CORE_RESULT 记事本已经打开。"));

        Assert.Equal(
            "VELIA_CORE_RESULT 记事本已经打开。",
            document.RootElement.GetProperty("realtimeInput").GetProperty("text").GetString());
        Assert.True(document.RootElement.GetProperty("realtimeInput")
            .GetProperty("audioStreamEnd").GetBoolean());
    }

    [Fact]
    public void ParsesScreenToolCallAndCancellation()
    {
        var json = JsonSerializer.Serialize(new
        {
            toolCall = new
            {
                functionCalls = new[]
                {
                    new { id = "call-1", name = "capture_current_screen", args = new { question = "当前是什么软件？" } },
                },
            },
            toolCallCancellation = new { ids = CancelledCallIds },
        });

        var message = GeminiLiveProtocol.Parse(json);

        var call = Assert.Single(message.ToolCalls);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("当前是什么软件？", call.Input);
        Assert.Equal("call-old", Assert.Single(message.CancelledToolCallIds));
    }

    [Fact]
    public void ParsesBoundedDelegatedTaskAndRejectsUnknownTools()
    {
        var json = JsonSerializer.Serialize(new
        {
            toolCall = new
            {
                functionCalls = new object[]
                {
                    new { id = "call-2", name = "delegate_task", args = new { task = "打开浏览器并搜索 Velia" } },
                    new { id = "call-3", name = "run_shell", args = new { task = "whoami" } },
                },
            },
        });

        var call = Assert.Single(GeminiLiveProtocol.Parse(json).ToolCalls);

        Assert.Equal("call-2", call.Id);
        Assert.Equal("delegate_task", call.Name);
        Assert.Equal("打开浏览器并搜索 Velia", call.Input);
    }

    [Fact]
    public void ToolResponseKeepsTheProviderCallIdentity()
    {
        using var document = JsonDocument.Parse(GeminiLiveProtocol.CreateToolResponse(
            "call-1", GeminiLiveProtocol.CaptureCurrentScreenTool, true, "这是 Codex。"));
        var response = document.RootElement.GetProperty("toolResponse").GetProperty("functionResponses")[0];
        Assert.Equal("call-1", response.GetProperty("id").GetString());
        Assert.Equal("capture_current_screen", response.GetProperty("name").GetString());
        Assert.Equal("这是 Codex。", response.GetProperty("response").GetProperty("result").GetString());
    }

    [Fact]
    public void DelegatedToolResponseKeepsItsOwnName()
    {
        using var document = JsonDocument.Parse(GeminiLiveProtocol.CreateToolResponse(
            "call-2", GeminiLiveProtocol.DelegateTaskTool, true, "搜索已完成。"));
        var response = document.RootElement.GetProperty("toolResponse").GetProperty("functionResponses")[0];

        Assert.Equal("call-2", response.GetProperty("id").GetString());
        Assert.Equal("delegate_task", response.GetProperty("name").GetString());
        Assert.Equal("搜索已完成。", response.GetProperty("response").GetProperty("result").GetString());
    }

    [Fact]
    public void ParsesAudioTranscriptsInterruptionGoAwayAndResumption()
    {
        var audio = Convert.ToBase64String([0, 1, 2, 3]);
        var json = JsonSerializer.Serialize(new
        {
            sessionResumptionUpdate = new { newHandle = "next" },
            goAway = new { timeLeft = "5s" },
            serverContent = new
            {
                interrupted = true,
                generationComplete = true,
                turnComplete = true,
                inputTranscription = new { text = "你好", finished = true, languageCode = "zh-CN" },
                outputTranscription = new { text = "您好" },
                modelTurn = new { parts = new[] { new { inlineData = new { mimeType = "audio/pcm;rate=24000", data = audio } } } },
            },
        });
        var message = GeminiLiveProtocol.Parse(json);

        Assert.True(message.Interrupted);
        Assert.True(message.GenerationComplete);
        Assert.True(message.TurnComplete);
        Assert.True(message.GoAway);
        Assert.Equal("你好", message.InputTranscript);
        Assert.True(message.InputTranscriptFinished);
        Assert.Equal("zh-CN", message.InputTranscriptLanguageCode);
        Assert.Equal("您好", message.OutputTranscript);
        Assert.Equal("next", message.ResumptionHandle);
        Assert.Equal(audio, Assert.Single(message.Audio));
    }

    [Fact]
    public void RejectsMalformedProviderJsonWithOnlyASafeMessage()
    {
        var exception = Assert.Throws<LiveProviderException>(() => GeminiLiveProtocol.Parse("not-json-with-secret-123"));
        Assert.Equal("live_protocol_error", exception.Code);
        Assert.DoesNotContain("secret-123", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("INVALID_ARGUMENT", "live_setup_rejected")]
    [InlineData("PERMISSION_DENIED", "live_permission_denied")]
    [InlineData("RESOURCE_EXHAUSTED", "live_quota_exhausted")]
    public void MapsServerErrorsWithoutExposingRawProviderText(string status, string expectedCode)
    {
        var json = JsonSerializer.Serialize(new { error = new { status, message = "raw-provider-secret-marker" } });
        var exception = Assert.Throws<LiveProviderException>(() => GeminiLiveProtocol.Parse(json));
        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain("raw-provider-secret-marker", exception.Message, StringComparison.Ordinal);
    }
}
