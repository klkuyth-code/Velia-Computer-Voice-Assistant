using Velia.Core.Providers.Asr;
using Velia.Core.Security;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class AsrServiceTests
{
    [Fact]
    public async Task PassesTheCurrentBoundedRecognitionContextToTheProvider()
    {
        var provider = new RecordingProvider();
        var context = new AsrRecognitionContext(["Velia", "剪映专业版", "停止"]);
        var service = new AsrService(
            new ConfiguredSecrets(),
            provider,
            recognitionContextProvider: new FixedContextProvider(context));

        var result = await service.TranscribeAsync(
            Convert.ToBase64String([1, 2, 3, 4]),
            maxLatencyMs: null,
            CancellationToken.None);

        Assert.Equal("测试转写", result.Text);
        Assert.Same(context, provider.Context);
    }

    private sealed class FixedContextProvider(AsrRecognitionContext context) : IAsrRecognitionContextProvider
    {
        public AsrRecognitionContext GetCurrent() => context;
    }

    private sealed class RecordingProvider : IAsrProvider
    {
        public AsrRecognitionContext? Context { get; private set; }

        public Task<AsrTranscription> TranscribeAsync(
            ReadOnlyMemory<byte> wavAudio,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Context-aware overload was not used.");

        public Task<AsrTranscription> TranscribeAsync(
            ReadOnlyMemory<byte> wavAudio,
            AsrRecognitionContext context,
            CancellationToken cancellationToken)
        {
            Context = context;
            return Task.FromResult(new AsrTranscription("测试转写", 500, 80));
        }
    }

    private sealed class ConfiguredSecrets : ISecretStore
    {
        public bool IsConfigured(string provider) => provider == ProviderIds.Asr;
        public void Save(string provider, string secret) { }
        public string? Load(string provider) => "configured";
        public void Remove(string provider) { }
    }
}
