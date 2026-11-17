using System.Runtime.CompilerServices;
using Velia.Core.Providers;
using Velia.Core.Providers.Chat;
using Velia.Core.Security;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class ProviderConfigurationServiceTests
{
    [Fact]
    public async Task TestsTheAllowlistedProfileWithoutReturningTheSecret()
    {
        var secrets = new MemorySecretStore();
        secrets.Save(ProviderIds.Llm, "never-return-this-key");
        var factory = new SuccessfulFactory();
        var service = new ProviderConfigurationService(secrets, factory);

        var result = await service.TestAsync(ProviderProfileIds.DeepSeekV4Flash, CancellationToken.None);
        var profiles = service.GetProfiles();

        Assert.Equal("ready", result.State);
        Assert.Equal("ready", profiles.Profiles[0].ConnectionState);
        Assert.Equal(16, factory.LastMaximumOutputTokens);
        Assert.False(factory.LastThinkingEnabled);
        Assert.DoesNotContain("never-return-this-key", System.Text.Json.JsonSerializer.Serialize(profiles), StringComparison.Ordinal);
    }

    [Fact]
    public void MapsAuthenticationAndBillingFailuresToSafeMessages()
    {
        var authentication = ProviderConfigurationService.MapFailure(
            ProviderProfileIds.DeepSeekV4Flash,
            new ChatProviderException("raw provider body", false, 401));
        var billing = ProviderConfigurationService.MapFailure(
            ProviderProfileIds.DeepSeekV4Flash,
            new ChatProviderException("raw provider body", false, 402));

        Assert.Equal("provider_authentication_failed", authentication.ErrorCode);
        Assert.Equal("provider_billing_unavailable", billing.ErrorCode);
        Assert.DoesNotContain("raw provider body", authentication.Message, StringComparison.Ordinal);
    }

    private sealed class SuccessfulFactory : IChatProviderFactory
    {
        public int LastMaximumOutputTokens { get; private set; }

        public bool LastThinkingEnabled { get; private set; } = true;

        public IStreamingChatProvider Create(
            string profileId,
            int maximumOutputTokens = 4096,
            bool thinkingEnabled = true)
        {
            LastMaximumOutputTokens = maximumOutputTokens;
            LastThinkingEnabled = thinkingEnabled;
            return new SuccessfulProvider();
        }
    }

    private sealed class SuccessfulProvider : IStreamingChatProvider
    {
        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatStreamEvent(ChatStreamEventKind.ContentDelta, "连接正常");
            yield return new ChatStreamEvent(ChatStreamEventKind.Completed, FinishReason: "stop");
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public bool IsConfigured(string provider) => values.ContainsKey(provider);

        public void Save(string provider, string secret) => values[provider] = secret;

        public string? Load(string provider) => values.GetValueOrDefault(provider);

        public void Remove(string provider) => values.Remove(provider);
    }
}
