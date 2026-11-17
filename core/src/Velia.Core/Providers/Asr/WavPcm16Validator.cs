using System.Buffers.Binary;

namespace Velia.Core.Providers.Asr;

internal static class WavPcm16Validator
{
    private const int ExpectedSampleRate = 16_000;
    private const short ExpectedChannels = 1;
    private const short ExpectedBitsPerSample = 16;

    public static WavAudioInfo Validate(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 44 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new AsrProviderException("invalid_audio", "录音不是有效的 WAV 文件。", false);
        }

        var offset = 12;
        short? format = null;
        short? channels = null;
        int? sampleRate = null;
        short? bits = null;
        int? dataLength = null;
        while (offset <= wav.Length - 8)
        {
            var chunkLength = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(offset + 4, 4));
            if (chunkLength < 0 || chunkLength > wav.Length - offset - 8)
            {
                throw new AsrProviderException("invalid_audio", "WAV 数据块长度无效。", false);
            }

            var id = wav.Slice(offset, 4);
            var data = wav.Slice(offset + 8, chunkLength);
            if (id.SequenceEqual("fmt "u8) && data.Length >= 16)
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(data);
                channels = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataLength = data.Length;
            }

            offset += 8 + chunkLength + (chunkLength & 1);
        }

        if (format != 1 || channels != ExpectedChannels || sampleRate != ExpectedSampleRate ||
            bits != ExpectedBitsPerSample || dataLength is null or < 3_200)
        {
            throw new AsrProviderException(
                "unsupported_audio_format",
                "录音必须是 16kHz、16 位、单声道 PCM WAV，且至少包含 100 毫秒语音。",
                false);
        }

        return new WavAudioInfo(
            dataLength.Value,
            (int)Math.Round(dataLength.Value * 1_000d / (ExpectedSampleRate * ExpectedChannels * (ExpectedBitsPerSample / 8))));
    }
}

internal sealed record WavAudioInfo(int PcmBytes, int DurationMs);
