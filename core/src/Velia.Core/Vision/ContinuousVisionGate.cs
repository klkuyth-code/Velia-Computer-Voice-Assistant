using System.Security.Cryptography;

namespace Velia.Core.Vision;

/// <summary>
/// Privacy and rate gate for a future continuous-vision adapter. Capture is
/// disabled by default, requires explicit consent, and suppresses duplicate
/// frames before any provider upload.
/// </summary>
internal sealed class ContinuousVisionGate(TimeSpan minimumInterval)
{
    private readonly object gate = new();
    private readonly TimeSpan interval = minimumInterval > TimeSpan.Zero
        ? minimumInterval
        : throw new ArgumentOutOfRangeException(nameof(minimumInterval));
    private bool enabled;
    private bool privacyPaused;
    private DateTimeOffset? lastAcceptedAt;
    private byte[]? lastFrameHash;

    public bool Enabled
    {
        get
        {
            lock (gate)
            {
                return enabled;
            }
        }
    }

    public void Enable(bool explicitUserConsent)
    {
        if (!explicitUserConsent)
        {
            throw new InvalidOperationException("Continuous vision requires explicit user consent.");
        }

        lock (gate)
        {
            enabled = true;
            privacyPaused = false;
        }
    }

    public void Disable()
    {
        lock (gate)
        {
            enabled = false;
            privacyPaused = false;
            lastAcceptedAt = null;
            lastFrameHash = null;
        }
    }

    public void SetPrivacyPaused(bool paused)
    {
        lock (gate)
        {
            privacyPaused = paused;
        }
    }

    public bool ShouldSend(ReadOnlySpan<byte> encodedFrame, DateTimeOffset capturedAt)
    {
        if (encodedFrame.IsEmpty)
        {
            return false;
        }

        lock (gate)
        {
            if (!enabled || privacyPaused)
            {
                return false;
            }

            if (lastAcceptedAt is not null && capturedAt - lastAcceptedAt < interval)
            {
                return false;
            }

            var hash = SHA256.HashData(encodedFrame);
            if (lastFrameHash is not null && CryptographicOperations.FixedTimeEquals(lastFrameHash, hash))
            {
                return false;
            }

            lastAcceptedAt = capturedAt;
            lastFrameHash = hash;
            return true;
        }
    }
}
