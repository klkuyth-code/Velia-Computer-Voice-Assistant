using Velia.Core.Vision;
using Xunit;

namespace Velia.Core.Tests;

public sealed class ContinuousVisionGateTests
{
    [Fact]
    public void RequiresConsentRateLimitsAndSuppressesDuplicateFrames()
    {
        var gate = new ContinuousVisionGate(TimeSpan.FromSeconds(1));
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<InvalidOperationException>(() => gate.Enable(false));
        Assert.False(gate.ShouldSend([1, 2, 3], now));

        gate.Enable(true);
        Assert.True(gate.ShouldSend([1, 2, 3], now));
        Assert.False(gate.ShouldSend([4, 5, 6], now.AddMilliseconds(500)));
        Assert.False(gate.ShouldSend([1, 2, 3], now.AddSeconds(1)));
        Assert.True(gate.ShouldSend([4, 5, 6], now.AddSeconds(2)));
    }

    [Fact]
    public void PrivacyPauseStopsFramesImmediately()
    {
        var gate = new ContinuousVisionGate(TimeSpan.FromSeconds(1));
        gate.Enable(true);
        gate.SetPrivacyPaused(true);

        Assert.False(gate.ShouldSend([1], DateTimeOffset.UtcNow));
    }
}
