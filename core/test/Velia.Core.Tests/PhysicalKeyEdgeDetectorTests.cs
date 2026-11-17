using Velia.Core.Input;
using Xunit;

namespace Velia.Core.Tests;

public sealed class PhysicalKeyEdgeDetectorTests
{
    [Fact]
    public void EmitsExactlyOneEdgeForEachPhysicalTransition()
    {
        var detector = new PhysicalKeyEdgeDetector();

        Assert.Null(detector.Sample(false));
        Assert.Equal(PhysicalKeyEdge.Pressed, detector.Sample(true));
        Assert.Null(detector.Sample(true));
        Assert.Equal(PhysicalKeyEdge.Released, detector.Sample(false));
        Assert.Null(detector.Sample(false));
    }

    [Fact]
    public void IgnoresAKeyHeldDuringStartupUntilItsFirstRelease()
    {
        var detector = new PhysicalKeyEdgeDetector();

        Assert.Null(detector.Sample(true));
        Assert.Null(detector.Sample(true));
        Assert.Null(detector.Sample(false));
        Assert.Equal(PhysicalKeyEdge.Pressed, detector.Sample(true));
        Assert.Equal(PhysicalKeyEdge.Released, detector.Sample(false));
    }

    [Fact]
    public void ResetRearmsTheDetectorWithoutEmittingAGhostEdge()
    {
        var detector = new PhysicalKeyEdgeDetector();

        Assert.Null(detector.Sample(false));
        Assert.Equal(PhysicalKeyEdge.Pressed, detector.Sample(true));
        detector.Reset();
        Assert.Null(detector.Sample(true));
        Assert.Null(detector.Sample(false));
        Assert.Equal(PhysicalKeyEdge.Pressed, detector.Sample(true));
    }
}
