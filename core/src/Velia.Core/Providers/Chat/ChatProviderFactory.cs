using Velia.Core.Security;
using Velia.Protocol;

namespace Velia.Core.Providers.Chat;

internal sealed class ChatProviderFactory(HttpClient httpClient, ISecretStore secrets) : IChatProviderFactory
{
    private static readonly Uri DeepSeekBaseUri = new("https://api.deepseek.com", UriKind.Absolute);

    public IStreamingChatProvider Create(
        string profileId,
        int maximumOutputTokens = 4096,
        bool thinkingEnabled = true)
    {
        if (profileId != ProviderProfileIds.DeepSeekV4Flash)
        {
            throw new ArgumentException("Provider profile is not allowlisted.", nameof(profileId));
        }

        return new OpenAiCompatibleChatProvider(
            httpClient,
            () => secrets.Load(ProviderIds.Llm),
            new ChatProviderOptions(
                DeepSeekBaseUri,
                ProviderProfileIds.DeepSeekV4Flash,
                ThinkingEnabled: thinkingEnabled,
                ReasoningEffort: "high",
                MaximumOutputTokens: maximumOutputTokens));
    }
}
