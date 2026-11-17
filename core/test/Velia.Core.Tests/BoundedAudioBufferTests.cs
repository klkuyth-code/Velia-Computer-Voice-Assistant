using Velia.Core.Audio;
using Xunit;

namespace Velia.Core.Tests;

public sealed class BoundedAudioBufferTests
{
    [Fact]
    public void PreservesChunkOrderAcrossPartialReads()
    {
        var buffer = new BoundedAudioBuffer(16);

        Assert.True(buffer.TryAppend([1, 2, 3]));
        Assert.True(buffer.TryAppend([4, 5, 6, 7]));

        Assert.Equal([1, 2, 3, 4, 5], buffer.Read(5));
        Assert.Equal(2, buffer.BufferedBytes);
        Assert.Equal([6, 7], buffer.Read(10));
        Assert.Equal(0, buffer.BufferedBytes);
    }

    [Fact]
    public void RejectsTheWholeChunkWhenTheHardLimitWouldBeExceeded()
    {
        var buffer = new BoundedAudioBuffer(5);

        Assert.True(buffer.TryAppend([1, 2, 3]));
        Assert.False(buffer.TryAppend([4, 5, 6]));

        Assert.Equal([1, 2, 3], buffer.Read(5));
    }
}
