using Velia.Core.Providers.Asr;
using Xunit;

namespace Velia.Core.Tests;

public sealed class WavPcm16ValidatorTests
{
    [Fact]
    public void AcceptsSixteenKhzMonoPcmAndCalculatesDuration()
    {
        var result = WavPcm16Validator.Validate(CreateWav(16_000, 1, 16, 16_000));

        Assert.Equal(16_000, result.PcmBytes);
        Assert.Equal(500, result.DurationMs);
    }

    [Fact]
    public void RejectsUnsupportedSampleRate()
    {
        var exception = Assert.Throws<AsrProviderException>(() =>
            WavPcm16Validator.Validate(CreateWav(48_000, 1, 16, 48_000)));

        Assert.Equal("unsupported_audio_format", exception.Code);
    }

    private static byte[] CreateWav(int sampleRate, short channels, short bits, int dataLength)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bits / 8);
        writer.Write((short)(channels * bits / 8));
        writer.Write(bits);
        writer.Write("data"u8);
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        return stream.ToArray();
    }
}
