namespace Velia.Core.Audio;

/// <summary>
/// A copy-on-append PCM queue with a hard byte limit. It avoids repeatedly
/// concatenating the complete utterance while audio is waiting for a provider.
/// </summary>
internal sealed class BoundedAudioBuffer(int maximumBytes)
{
    private readonly object gate = new();
    private readonly Queue<byte[]> chunks = new();
    private int headOffset;
    private int bufferedBytes;

    public int MaximumBytes { get; } = maximumBytes > 0
        ? maximumBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumBytes));

    public int BufferedBytes
    {
        get
        {
            lock (gate)
            {
                return bufferedBytes;
            }
        }
    }

    public bool TryAppend(ReadOnlySpan<byte> audio)
    {
        if (audio.IsEmpty)
        {
            return true;
        }

        lock (gate)
        {
            if (audio.Length > MaximumBytes - bufferedBytes)
            {
                return false;
            }

            chunks.Enqueue(audio.ToArray());
            bufferedBytes += audio.Length;
            return true;
        }
    }

    public byte[] Read(int requestedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedBytes);

        lock (gate)
        {
            var bytesToRead = Math.Min(requestedBytes, bufferedBytes);
            if (bytesToRead == 0)
            {
                return [];
            }

            var output = new byte[bytesToRead];
            var outputOffset = 0;
            while (outputOffset < bytesToRead)
            {
                var chunk = chunks.Peek();
                var available = chunk.Length - headOffset;
                var copyLength = Math.Min(available, bytesToRead - outputOffset);
                Buffer.BlockCopy(chunk, headOffset, output, outputOffset, copyLength);
                outputOffset += copyLength;
                headOffset += copyLength;

                if (headOffset == chunk.Length)
                {
                    chunks.Dequeue();
                    headOffset = 0;
                }
            }

            bufferedBytes -= bytesToRead;
            return output;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            chunks.Clear();
            headOffset = 0;
            bufferedBytes = 0;
        }
    }
}
