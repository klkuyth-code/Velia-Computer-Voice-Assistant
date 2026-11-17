using System.Text.Json;
using Velia.Core.Conversations;
using Velia.Core.Providers;
using Velia.Core.Providers.Asr;
using Velia.Core.Providers.Vision;
using Velia.Core.Documents;
using Velia.Core.Security;
using Velia.Core.Input;
using Velia.Core.Providers.Live;
using Velia.Core.DesktopContext;
using Velia.Core.DesktopVisual;
using Velia.Core.Web;
using Velia.Protocol;

namespace Velia.Core.Rpc;

internal sealed class CoreRpcDispatcher(
    ISecretStore secrets,
    ProviderConfigurationService providerConfiguration,
    ConversationService conversations,
    AsrService asr,
    VisionService vision,
    DocumentService documents,
    VoiceShortcutSettings voiceShortcuts,
    GeminiLiveService live,
    DesktopContextService desktopContext,
    IFirecrawlService? firecrawl = null,
    WindowsDesktopAutomationService? desktopVisual = null)
{
    public async Task<RpcResponse> DispatchAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return request.Method switch
        {
            RpcMethods.SystemHealth => Health(request),
            RpcMethods.AppBootstrap => Bootstrap(request),
            RpcMethods.ConfigGetStatus => ConfigStatus(request),
            RpcMethods.ConfigGetProfiles => GetProfiles(request),
            RpcMethods.ConfigSelectProfile => SelectProfile(request),
            RpcMethods.ConfigTestProvider => await TestProviderAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.ConfigSetSecret => SetSecret(request),
            RpcMethods.ConfigRemoveSecret => RemoveSecret(request),
            RpcMethods.WebTest => await TestWebAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.VoiceTranscribe => await TranscribeAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.VoiceTestAsr => await TestAsrAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.VoiceGetShortcut => GetVoiceShortcut(request),
            RpcMethods.VoiceSetShortcut => SetVoiceShortcut(request),
            RpcMethods.LiveStart => await StartLiveAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.LiveSendAudio => SendLiveAudio(request),
            RpcMethods.LiveSendVideo => await SendLiveVideoAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.LiveSubmitLocalTranscript => await SubmitLiveLocalTranscriptAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.LiveSubmitToolResult => await SubmitLiveToolResultAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.LiveStop => await StopLiveAsync(request).ConfigureAwait(false),
            RpcMethods.LiveGetStatus => GetLiveStatus(request),
            RpcMethods.VisionTest => await TestVisionAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.VisionAnalyze => await AnalyzeVisionAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.DocumentOpen => OpenDocument(request),
            RpcMethods.DocumentAnalyze => await AnalyzeDocumentAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.DocumentClose => CloseDocument(request),
            RpcMethods.ConversationSubmitText => await SubmitTextAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.ConversationSubmitToolResult => SubmitToolResult(request),
            RpcMethods.ConversationCancel => CancelConversation(request),
            RpcMethods.ConversationHistoryGetSettings => GetConversationHistorySettings(request),
            RpcMethods.ConversationHistoryUpdateSettings => UpdateConversationHistorySettings(request),
            RpcMethods.ConversationHistoryList => ListConversationHistory(request),
            RpcMethods.ConversationHistoryGet => GetConversationHistory(request),
            RpcMethods.ConversationHistoryOpen => OpenConversationHistory(request),
            RpcMethods.ConversationHistoryStartNew => StartNewConversationHistory(request),
            RpcMethods.ConversationHistoryDelete => DeleteConversationHistory(request),
            RpcMethods.ConversationHistoryClear => ClearConversationHistory(request),
            RpcMethods.DesktopContextCapture => await CaptureDesktopContextAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.DesktopContextReplaceSelection => await ReplaceDesktopSelectionAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.DesktopContextOperateSelectedFiles => await OperateSelectedFilesAsync(request, cancellationToken).ConfigureAwait(false),
            RpcMethods.DesktopContextResolveSelectedDocument => ResolveSelectedDocument(request),
            RpcMethods.DesktopContextRelease => ReleaseDesktopContext(request),
            RpcMethods.DesktopVisualGetForeground => GetDesktopVisualForeground(request),
            RpcMethods.DesktopVisualExecuteAction => ExecuteDesktopVisualAction(request),
            _ => RpcResponse.Failure(request.Id, "unknown_method", "RPC method is not allowlisted."),
        };
    }

    private static RpcResponse Health(RpcRequest request)
    {
        DeserializeParams<EmptyParams>(request);
        return RpcResponse.Success(request.Id, new HealthResult(
            "velia-core",
            AppInfo.Version,
            RpcSchema.ProtocolVersion,
            "ready",
            DateTimeOffset.UtcNow));
    }

    private static RpcResponse Bootstrap(RpcRequest request)
    {
        DeserializeParams<EmptyParams>(request);
        return RpcResponse.Success(request.Id, new
        {
            product = "Velia",
            version = AppInfo.Version,
            platform = "windows",
            capabilities = new
            {
                textConversation = true,
                voice = true,
                live = true,
                vision = true,
                tools = true,
            },
        });
    }

    private RpcResponse ConfigStatus(RpcRequest request)
    {
        DeserializeParams<EmptyParams>(request);
        return RpcResponse.Success(request.Id, new ConfigStatusResult
        {
            Llm = State(ProviderIds.Llm),
            Asr = State(ProviderIds.Asr),
            Vision = State(ProviderIds.Vision),
            Web = State(ProviderIds.Web),
        });
    }

    private RpcResponse SetSecret(RpcRequest request)
    {
        var parameters = DeserializeParams<SetProviderSecretParams>(request);
        secrets.Save(parameters.Provider, parameters.Secret);
        if (parameters.Provider == ProviderIds.Llm) providerConfiguration.ResetConnectionState();
        return RpcResponse.Success(request.Id, new { configured = true });
    }

    private RpcResponse RemoveSecret(RpcRequest request)
    {
        var parameters = DeserializeParams<RemoveProviderSecretParams>(request);
        secrets.Remove(parameters.Provider);
        if (parameters.Provider == ProviderIds.Llm) providerConfiguration.ResetConnectionState();
        return RpcResponse.Success(request.Id, new { configured = false });
    }

    private async Task<RpcResponse> TestWebAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        DeserializeParams<EmptyParams>(request);
        var result = firecrawl is null
            ? new WebTestResult(
                "error",
                DateTimeOffset.UtcNow,
                "网页检索服务当前不可用。",
                ErrorCode: "web_unavailable",
                Retryable: true)
            : await firecrawl.TestAsync(cancellationToken).ConfigureAwait(false);
        return RpcResponse.Success(request.Id, result);
    }

    private async Task<RpcResponse> TranscribeAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        var parameters = DeserializeParams<VoiceTranscribeParams>(request);
        try
        {
            if (parameters.MaxLatencyMs is < 1_000 or > 20_000)
                throw new AsrProviderException("invalid_asr_deadline", "语音识别响应窗口无效。", false);
            var result = await asr.TranscribeAsync(
                parameters.WavBase64,
                parameters.MaxLatencyMs,
                cancellationToken).ConfigureAwait(false);
            return RpcResponse.Success(request.Id, result);
        }
        catch (AsrProviderException exception)
        {
            return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable);
        }
    }

    private async Task<RpcResponse> TestAsrAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        DeserializeParams<EmptyParams>(request);
        return RpcResponse.Success(request.Id, await asr.TestAsync(cancellationToken).ConfigureAwait(false));
    }

    private RpcResponse GetVoiceShortcut(RpcRequest request)
    {
        DeserializeParams<EmptyParams>(request);
        return RpcResponse.Success(request.Id, voiceShortcuts.Get());
    }

    private RpcResponse SetVoiceShortcut(RpcRequest request)
    {
        var parameters = DeserializeParams<SetVoiceShortcutParams>(request);
        try
        {
            return RpcResponse.Success(request.Id, voiceShortcuts.Set(parameters.ShortcutId));
        }
        catch (ArgumentException exception)
        {
            return RpcResponse.Failure(request.Id, "invalid_voice_shortcut", exception.Message);
        }
    }

    private async Task<RpcResponse> StartLiveAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        var parameters = DeserializeParams<LiveStartParams>(request);
        try { return RpcResponse.Success(request.Id, await live.StartAsync(parameters, cancellationToken).ConfigureAwait(false)); }
        catch (LiveProviderException exception) { return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable); }
    }

    private RpcResponse SendLiveAudio(RpcRequest request)
    {
        var parameters = DeserializeParams<LiveSendAudioParams>(request);
        try { return RpcResponse.Success(request.Id, live.SendAudio(parameters)); }
        catch (LiveProviderException exception) { return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable); }
    }

    private async Task<RpcResponse> SendLiveVideoAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        var parameters = DeserializeParams<LiveSendVideoParams>(request);
        try
        {
            return RpcResponse.Success(
                request.Id,
                await live.SendVideoAsync(parameters, cancellationToken).ConfigureAwait(false));
        }
        catch (LiveProviderException exception)
        {
            return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable);
        }
    }

    private async Task<RpcResponse> SubmitLiveToolResultAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        var parameters = DeserializeParams<LiveSubmitToolResultParams>(request);
        try
        {
            return RpcResponse.Success(
                request.Id,
                await live.SubmitToolResultAsync(parameters, cancellationToken).ConfigureAwait(false));
        }
        catch (LiveProviderException exception)
        {
            return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable);
        }
    }

    private async Task<RpcResponse> SubmitLiveLocalTranscriptAsync(
        RpcRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = DeserializeParams<LiveSubmitLocalTranscriptParams>(request);
        try
        {
            return RpcResponse.Success(
                request.Id,
                await live.SubmitLocalTranscriptAsync(parameters, cancellationToken).ConfigureAwait(false));
        }
        catch (LiveProviderException exception)
        {
            return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable);
        }
    }

    private async Task<RpcResponse> StopLiveAsync(RpcRequest request)
    {
        var parameters = DeserializeParams<LiveStopParams>(request);
        return RpcResponse.Success(request.Id, await live.StopAsync(parameters).ConfigureAwait(false));
    }

    private RpcResponse GetLiveStatus(RpcRequest request)
    {
        DeserializeParams<EmptyParams>(request);
        return RpcResponse.Success(request.Id, live.GetStatus());
    }

    private async Task<RpcResponse> TestVisionAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        DeserializeParams<EmptyParams>(request);
        return RpcResponse.Success(request.Id, await vision.TestAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<RpcResponse> AnalyzeVisionAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        var parameters = DeserializeParams<VisionAnalyzeParams>(request);
        try
        {
            return RpcResponse.Success(
                request.Id,
                await vision.AnalyzeAsync(parameters, cancellationToken).ConfigureAwait(false));
        }
        catch (VisionProviderException exception)
        {
            return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable);
        }
    }

    private RpcResponse OpenDocument(RpcRequest request)
    {
        var parameters = DeserializeParams<DocumentOpenParams>(request);
        try { return RpcResponse.Success(request.Id, documents.Open(parameters)); }
        catch (DocumentException exception) { return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable); }
    }

    private async Task<RpcResponse> AnalyzeDocumentAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        var parameters = DeserializeParams<DocumentAnalyzeParams>(request);
        try
        {
            return RpcResponse.Success(
                request.Id,
                await documents.AnalyzeAsync(parameters, cancellationToken).ConfigureAwait(false));
        }
        catch (DocumentException exception)
        {
            return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable);
        }
    }

    private RpcResponse CloseDocument(RpcRequest request)
    {
        var parameters = DeserializeParams<DocumentCloseParams>(request);
        return RpcResponse.Success(request.Id, documents.Close(parameters.DocumentId));
    }

    private string State(string provider) => secrets.IsConfigured(provider) ? "configured" : "notConfigured";

    private RpcResponse GetProfiles(RpcRequest request)
    {
        DeserializeParams<EmptyParams>(request);
        return RpcResponse.Success(request.Id, providerConfiguration.GetProfiles());
    }

    private RpcResponse SelectProfile(RpcRequest request)
    {
        var parameters = DeserializeParams<SelectProviderProfileParams>(request);
        return RpcResponse.Success(request.Id, providerConfiguration.Select(parameters.ProfileId));
    }

    private async Task<RpcResponse> TestProviderAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        var parameters = DeserializeParams<TestProviderParams>(request);
        var result = await providerConfiguration.TestAsync(parameters.ProfileId, cancellationToken).ConfigureAwait(false);
        return RpcResponse.Success(request.Id, result);
    }

    private async Task<RpcResponse> SubmitTextAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        var parameters = DeserializeParams<SubmitTextParams>(request);
        try
        {
            return RpcResponse.Success(
                request.Id,
                await conversations.SubmitAsync(
                    parameters.Text,
                    parameters.Origin,
                    parameters.Relationship,
                    cancellationToken).ConfigureAwait(false));
        }
        catch (ConversationStartException exception)
        {
            return RpcResponse.Failure(request.Id, exception.Code, exception.Message);
        }
        catch (ArgumentException)
        {
            return RpcResponse.Failure(request.Id, "invalid_message", "消息不能为空。" );
        }
    }

    private RpcResponse CancelConversation(RpcRequest request)
    {
        var parameters = DeserializeParams<CancelConversationParams>(request);
        return RpcResponse.Success(request.Id, conversations.Cancel(parameters.OperationId));
    }

    private RpcResponse SubmitToolResult(RpcRequest request)
    {
        var parameters = DeserializeParams<SubmitToolResultParams>(request);
        try
        {
            return RpcResponse.Success(request.Id, conversations.SubmitToolResult(parameters));
        }
        catch (ConversationStartException exception)
        {
            return RpcResponse.Failure(request.Id, exception.Code, exception.Message);
        }
    }

    private RpcResponse GetConversationHistorySettings(RpcRequest request)
    {
        DeserializeParams<EmptyParams>(request);
        return RpcResponse.Success(request.Id, conversations.GetHistorySettings());
    }

    private RpcResponse UpdateConversationHistorySettings(RpcRequest request)
    {
        var parameters = DeserializeParams<UpdateConversationHistorySettingsParams>(request);
        return DispatchHistory(request, () => conversations.UpdateHistorySettings(
            parameters.Enabled,
            parameters.RetentionDays));
    }

    private RpcResponse ListConversationHistory(RpcRequest request)
    {
        DeserializeParams<EmptyParams>(request);
        return RpcResponse.Success(request.Id, conversations.ListHistory());
    }

    private RpcResponse GetConversationHistory(RpcRequest request)
    {
        var parameters = DeserializeParams<ConversationHistoryIdParams>(request);
        return DispatchHistory(request, () => conversations.GetHistory(parameters.ConversationId));
    }

    private RpcResponse OpenConversationHistory(RpcRequest request)
    {
        var parameters = DeserializeParams<ConversationHistoryIdParams>(request);
        return DispatchHistory(request, () => conversations.OpenHistory(parameters.ConversationId));
    }

    private RpcResponse StartNewConversationHistory(RpcRequest request)
    {
        DeserializeParams<EmptyParams>(request);
        return DispatchHistory(request, conversations.StartNewHistory);
    }

    private RpcResponse DeleteConversationHistory(RpcRequest request)
    {
        var parameters = DeserializeParams<ConversationHistoryIdParams>(request);
        return DispatchHistory(request, () => conversations.DeleteHistory(parameters.ConversationId));
    }

    private RpcResponse ClearConversationHistory(RpcRequest request)
    {
        DeserializeParams<EmptyParams>(request);
        return DispatchHistory(request, conversations.ClearHistory);
    }

    private static RpcResponse DispatchHistory(RpcRequest request, Func<object> action)
    {
        try { return RpcResponse.Success(request.Id, action()); }
        catch (ConversationStartException exception)
        {
            return RpcResponse.Failure(request.Id, exception.Code, exception.Message);
        }
    }

    private async Task<RpcResponse> CaptureDesktopContextAsync(
        RpcRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = DeserializeParams<DesktopContextCaptureParams>(request);
        if (!conversations.IsOperationActive(parameters.OperationId))
            return RpcResponse.Failure(request.Id, "context_operation_inactive", "桌面上下文只能绑定当前正在执行的任务。");
        try
        {
            var result = await desktopContext.CaptureAsync(parameters, cancellationToken).ConfigureAwait(false);
            if (conversations.IsOperationActive(parameters.OperationId))
                return RpcResponse.Success(request.Id, result);
            desktopContext.Release(parameters.OperationId);
            return RpcResponse.Failure(request.Id, "context_operation_inactive", "任务已经结束，未保留桌面上下文。");
        }
        catch (DesktopContextException exception)
        {
            return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable);
        }
    }

    private RpcResponse ReleaseDesktopContext(RpcRequest request)
    {
        var parameters = DeserializeParams<DesktopContextReleaseParams>(request);
        return RpcResponse.Success(request.Id, desktopContext.Release(parameters.OperationId));
    }

    private RpcResponse ResolveSelectedDocument(RpcRequest request)
    {
        var parameters = DeserializeParams<DesktopContextResolveSelectedDocumentParams>(request);
        if (!conversations.IsOperationActive(parameters.OperationId))
            return RpcResponse.Failure(request.Id, "context_operation_inactive", "只能读取当前任务刚刚绑定的选中文档。");
        try { return RpcResponse.Success(request.Id, desktopContext.ResolveSelectedDocument(parameters)); }
        catch (DesktopContextException exception)
        {
            return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable);
        }
    }

    private RpcResponse GetDesktopVisualForeground(RpcRequest request)
    {
        var parameters = DeserializeParams<DesktopVisualGetForegroundParams>(request);
        if (!conversations.IsOperationActive(parameters.OperationId))
            return RpcResponse.Failure(request.Id, "desktop_visual_operation_inactive", "桌面视觉只能绑定当前正在执行的任务。");
        if (desktopVisual is null)
            return RpcResponse.Failure(request.Id, "desktop_visual_unavailable", "当前 Windows 桌面视觉动作不可用。", true);
        try { return RpcResponse.Success(request.Id, desktopVisual.GetForeground(parameters.OperationId)); }
        catch (DesktopVisualException exception) { return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable); }
    }

    private RpcResponse ExecuteDesktopVisualAction(RpcRequest request)
    {
        var parameters = DeserializeParams<DesktopVisualExecuteActionParams>(request);
        if (!conversations.IsOperationActive(parameters.OperationId))
            return RpcResponse.Failure(request.Id, "desktop_visual_operation_inactive", "桌面视觉只能绑定当前正在执行的任务。");
        if (desktopVisual is null)
            return RpcResponse.Failure(request.Id, "desktop_visual_unavailable", "当前 Windows 桌面视觉动作不可用。", true);
        try { return RpcResponse.Success(request.Id, desktopVisual.Execute(parameters)); }
        catch (DesktopVisualException exception) { return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable); }
    }

    private async Task<RpcResponse> ReplaceDesktopSelectionAsync(
        RpcRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = DeserializeParams<DesktopContextReplaceSelectionParams>(request);
        if (!conversations.IsOperationActive(parameters.OperationId))
            return RpcResponse.Failure(request.Id, "context_operation_inactive", "只能替换当前正在执行任务绑定的选中文字。");
        try
        {
            var result = await desktopContext.ReplaceSelectionAsync(parameters, cancellationToken).ConfigureAwait(false);
            if (conversations.IsOperationActive(parameters.OperationId))
                return RpcResponse.Success(request.Id, result);
            desktopContext.Release(parameters.OperationId);
            return RpcResponse.Failure(request.Id, "context_operation_inactive", "任务已经结束，未保留替换上下文。");
        }
        catch (DesktopContextException exception)
        {
            return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable);
        }
    }

    private async Task<RpcResponse> OperateSelectedFilesAsync(
        RpcRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = DeserializeParams<DesktopContextFileOperationParams>(request);
        if (!conversations.IsOperationActive(parameters.OperationId))
            return RpcResponse.Failure(request.Id, "context_operation_inactive", "只能操作当前正在执行任务绑定的选中文件。" );
        try
        {
            var result = await desktopContext.OperateSelectedFilesAsync(parameters, cancellationToken).ConfigureAwait(false);
            return conversations.IsOperationActive(parameters.OperationId)
                ? RpcResponse.Success(request.Id, result)
                : RpcResponse.Failure(request.Id, "context_operation_inactive", "任务已经结束，文件操作结果未提交给模型。" );
        }
        catch (DesktopContextException exception)
        {
            return RpcResponse.Failure(request.Id, exception.Code, exception.Message, exception.Retryable);
        }
    }

    private static T DeserializeParams<T>(RpcRequest request)
    {
        return request.Params.Deserialize<T>(JsonDefaults.Strict)
            ?? throw new JsonException("Parameters cannot be null.");
    }
}
