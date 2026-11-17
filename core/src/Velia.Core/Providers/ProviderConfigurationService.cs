using Velia.Core.Providers.Chat;
using Velia.Core.Security;
using Velia.Protocol;

namespace Velia.Core.Providers;

internal sealed class ProviderConfigurationService(
    ISecretStore secrets,
    IChatProviderFactory chatProviders)
{
    private readonly object gate = new();
    private ProviderTestResult? lastTest;
    private bool testing;

    public ProviderProfilesResult GetProfiles()
    {
        var configured = secrets.IsConfigured(ProviderIds.Llm);
        ProviderTestResult? snapshot;
        bool isTesting;
        lock (gate)
        {
            snapshot = lastTest;
            isTesting = testing;
        }

        var connectionState = !configured
            ? "notConfigured"
            : isTesting
                ? "testing"
                : snapshot?.State ?? "configured";

        return new ProviderProfilesResult(
            ProviderProfileIds.DeepSeekV4Flash,
            [new ProviderProfileResult(
                ProviderProfileIds.DeepSeekV4Flash,
                "deepseek",
                "DeepSeek V4 Flash",
                ProviderProfileIds.DeepSeekV4Flash,
                true,
                configured ? "configured" : "notConfigured",
                connectionState,
                SupportsReasoning: true,
                SupportsTools: true,
                snapshot?.TestedAtUtc,
                snapshot?.Message)]);
    }

    public ProviderProfilesResult Select(string profileId)
    {
        ValidateProfile(profileId);
        return GetProfiles();
    }

    public void ResetConnectionState()
    {
        lock (gate)
        {
            lastTest = null;
            testing = false;
        }
    }

    public async Task<ProviderTestResult> TestAsync(string profileId, CancellationToken cancellationToken)
    {
        ValidateProfile(profileId);
        if (!secrets.IsConfigured(ProviderIds.Llm))
        {
            return Remember(new ProviderTestResult(
                profileId,
                "error",
                DateTimeOffset.UtcNow,
                "请先保存 DeepSeek API 密钥。",
                "provider_not_configured",
                false));
        }

        lock (gate)
        {
            testing = true;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var provider = chatProviders.Create(
                profileId,
                maximumOutputTokens: 16,
                thinkingEnabled: false);
            var sawContent = false;
            var completed = false;
            var request = new ChatCompletionRequest(
                [
                    new ChatMessage(ChatRole.System, "你正在执行 API 连接测试。不要调用任何工具。"),
                    new ChatMessage(ChatRole.User, "只回复：连接正常"),
                ]);

            await foreach (var streamEvent in provider.StreamAsync(request, timeout.Token).ConfigureAwait(false))
            {
                sawContent |= streamEvent.Kind == ChatStreamEventKind.ContentDelta &&
                    !string.IsNullOrEmpty(streamEvent.Text);
                completed |= streamEvent.Kind == ChatStreamEventKind.Completed;
            }

            if (!sawContent || !completed)
            {
                return Remember(Failure(profileId, "provider_invalid_response", "模型返回不完整，请稍后重试。", true));
            }

            return Remember(new ProviderTestResult(
                profileId,
                "ready",
                DateTimeOffset.UtcNow,
                "连接成功，模型已就绪。"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Remember(Failure(profileId, "provider_timeout", "连接超时，请检查网络后重试。", true));
        }
        catch (ChatProviderException exception)
        {
            return Remember(MapFailure(profileId, exception));
        }
        finally
        {
            lock (gate)
            {
                testing = false;
            }
        }
    }

    private ProviderTestResult Remember(ProviderTestResult result)
    {
        lock (gate)
        {
            lastTest = result;
        }

        return result;
    }

    internal static ProviderTestResult MapFailure(string profileId, ChatProviderException exception)
    {
        return exception.StatusCode switch
        {
            401 or 403 => Failure(profileId, "provider_authentication_failed", "API 密钥无效或没有访问权限。", false),
            402 => Failure(profileId, "provider_billing_unavailable", "账户余额不足或计费状态不可用。", false),
            404 => Failure(profileId, "provider_model_unavailable", "当前模型不可用，请稍后更新 Velia。", false),
            429 => Failure(profileId, "provider_rate_limited", "请求过于频繁，请稍后重试。", true),
            >= 500 => Failure(profileId, "provider_unavailable", "DeepSeek 服务暂时不可用。", true),
            _ => Failure(profileId, "provider_connection_failed", "无法连接模型服务，请检查网络和密钥。", exception.Retryable),
        };
    }

    private static ProviderTestResult Failure(string profileId, string code, string message, bool retryable) =>
        new(profileId, "error", DateTimeOffset.UtcNow, message, code, retryable);

    private static void ValidateProfile(string profileId)
    {
        if (!ProviderProfileIds.All.Contains(profileId))
        {
            throw new ArgumentException("Provider profile is not allowlisted.", nameof(profileId));
        }
    }
}
