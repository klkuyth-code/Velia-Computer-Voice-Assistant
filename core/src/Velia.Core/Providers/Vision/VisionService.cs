using System.Security.Cryptography;
using Velia.Core.Security;
using Velia.Protocol;

namespace Velia.Core.Providers.Vision;

internal sealed class VisionService(
    ISecretStore secrets,
    IVisionProvider provider)
{
    internal const int MaximumImageBytes = 6 * 1024 * 1024;
    internal const int MaximumBase64Characters = 8 * 1024 * 1024;
    internal const int MaximumPromptCharacters = 4_000;

    public async Task<VisionAnalyzeResult> AnalyzeAsync(
        VisionAnalyzeParams parameters,
        CancellationToken cancellationToken)
    {
        if (!secrets.IsConfigured(ProviderIds.Vision))
            throw new VisionProviderException("vision_not_configured", "请先保存 Gemini API 密钥。", false);
        ValidateMetadata(parameters);

        byte[] image;
        try { image = Convert.FromBase64String(parameters.ImageBase64); }
        catch (FormatException) { throw new VisionProviderException("invalid_image", "截图数据编码无效。", false); }

        try
        {
            if (image.Length is 0 or > MaximumImageBytes || !IsJpeg(image))
                throw new VisionProviderException("invalid_image", "截图不是有效 JPEG，或超过 6 MB 安全限制。", false);
            var result = await provider.AnalyzeAsync(image, parameters.Prompt.Trim(), parameters.Model, cancellationToken)
                .ConfigureAwait(false);
            return new VisionAnalyzeResult(
                result.Text,
                parameters.Model,
                parameters.CapturedAtUtc,
                result.ProviderLatencyMs,
                image.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(image);
        }
    }

    public async Task<VisionTestResult> TestAsync(CancellationToken cancellationToken)
    {
        if (!secrets.IsConfigured(ProviderIds.Vision))
            return Failure("vision_not_configured", "请先保存 Gemini API 密钥。", false);
        try
        {
            await provider.TestAsync(cancellationToken).ConfigureAwait(false);
            return new VisionTestResult("ready", DateTimeOffset.UtcNow, "连接成功，Gemini 屏幕视觉已就绪。");
        }
        catch (VisionProviderException exception)
        {
            return Failure(exception.Code, exception.Message, exception.Retryable);
        }
    }

    private static void ValidateMetadata(VisionAnalyzeParams parameters)
    {
        if (parameters.MimeType != "image/jpeg")
            throw new VisionProviderException("invalid_image_type", "屏幕视觉只接受本机生成的 JPEG 截图。", false);
        if (string.IsNullOrWhiteSpace(parameters.ImageBase64) || parameters.ImageBase64.Length > MaximumBase64Characters)
            throw new VisionProviderException("invalid_image", "截图为空或超过安全限制。", false);
        if (string.IsNullOrWhiteSpace(parameters.Prompt) || parameters.Prompt.Length > MaximumPromptCharacters)
            throw new VisionProviderException("invalid_prompt", "视觉问题为空或超过 4000 字。", false);
        if (!VisionModels.All.Contains(parameters.Model))
            throw new VisionProviderException("vision_model_not_allowed", "当前视觉模型不在安全白名单中。", false);
        if (string.IsNullOrWhiteSpace(parameters.DisplayId) || parameters.DisplayId.Length > 128)
            throw new VisionProviderException("invalid_display", "显示器标识无效。", false);
    }

    private static bool IsJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 &&
        bytes[^2] == 0xff && bytes[^1] == 0xd9;

    private static VisionTestResult Failure(string code, string message, bool retryable) =>
        new("error", DateTimeOffset.UtcNow, message, code, retryable);
}
