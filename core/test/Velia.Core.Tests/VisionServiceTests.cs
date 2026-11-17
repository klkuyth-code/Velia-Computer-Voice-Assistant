using Velia.Core.Providers.Vision;
using Velia.Core.Security;
using Velia.Protocol;
using Xunit;

namespace Velia.Core.Tests;

public sealed class VisionServiceTests
{
    [Fact]
    public async Task ReturnsNotConfiguredWithoutCallingTheProvider()
    {
        var provider = new StubProvider();
        var service = new VisionService(new MemorySecretStore(), provider);

        var result = await service.TestAsync(CancellationToken.None);

        Assert.Equal("error", result.State);
        Assert.Equal("vision_not_configured", result.ErrorCode);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task RejectsInvalidBase64AndImageData()
    {
        var secrets = ConfiguredSecrets();
        var provider = new StubProvider();
        var service = new VisionService(secrets, provider);

        var invalidBase64 = await Assert.ThrowsAsync<VisionProviderException>(() =>
            service.AnalyzeAsync(Params("not-base64"), CancellationToken.None));
        var invalidJpeg = await Assert.ThrowsAsync<VisionProviderException>(() =>
            service.AnalyzeAsync(Params(Convert.ToBase64String([1, 2, 3, 4])), CancellationToken.None));

        Assert.Equal("invalid_image", invalidBase64.Code);
        Assert.Equal("invalid_image", invalidJpeg.Code);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task EnforcesModelAllowlist()
    {
        var secrets = ConfiguredSecrets();
        var provider = new StubProvider();
        var service = new VisionService(secrets, provider);

        var model = await Assert.ThrowsAsync<VisionProviderException>(() =>
            service.AnalyzeAsync(Params(Jpeg()) with { Model = "unknown" }, CancellationToken.None));

        Assert.Equal("vision_model_not_allowed", model.Code);
    }

    [Fact]
    public async Task RejectsWrongMimeTypeOversizedPromptAndOversizedBase64()
    {
        var service = new VisionService(ConfiguredSecrets(), new StubProvider());
        var valid = Params(Jpeg());

        var mime = await Assert.ThrowsAsync<VisionProviderException>(() =>
            service.AnalyzeAsync(valid with { MimeType = "image/png" }, CancellationToken.None));
        var prompt = await Assert.ThrowsAsync<VisionProviderException>(() =>
            service.AnalyzeAsync(valid with { Prompt = new string('a', VisionService.MaximumPromptCharacters + 1) }, CancellationToken.None));
        var image = await Assert.ThrowsAsync<VisionProviderException>(() =>
            service.AnalyzeAsync(valid with { ImageBase64 = new string('A', VisionService.MaximumBase64Characters + 1) }, CancellationToken.None));

        Assert.Equal("invalid_image_type", mime.Code);
        Assert.Equal("invalid_prompt", prompt.Code);
        Assert.Equal("invalid_image", image.Code);
    }

    [Fact]
    public async Task AnalyzesAValidJpegAndReturnsMetadata()
    {
        var provider = new StubProvider();
        var service = new VisionService(ConfiguredSecrets(), provider);

        var result = await service.AnalyzeAsync(Params(Jpeg()), CancellationToken.None);

        Assert.Equal("识别成功", result.Text);
        Assert.Equal(6, result.InputBytes);
        Assert.Equal(1, provider.CallCount);
    }

    private static VisionAnalyzeParams Params(string base64) => new(
        base64, "image/jpeg", "描述画面", VisionModels.Gemini35FlashLite, "1", DateTimeOffset.UtcNow);
    private static string Jpeg() => Convert.ToBase64String([0xff, 0xd8, 1, 2, 0xff, 0xd9]);
    private static MemorySecretStore ConfiguredSecrets()
    {
        var store = new MemorySecretStore(); store.Save(ProviderIds.Vision, "test-key"); return store;
    }

    private sealed class StubProvider : IVisionProvider
    {
        public int CallCount { get; private set; }
        public Task<VisionProviderResult> AnalyzeAsync(ReadOnlyMemory<byte> jpegImage, string prompt, string model, CancellationToken cancellationToken)
        { CallCount += 1; return Task.FromResult(new VisionProviderResult("识别成功", 12)); }
        public Task<long> TestAsync(CancellationToken cancellationToken)
        { CallCount += 1; return Task.FromResult(12L); }
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
