using Velia.Core.Input;
using Xunit;

namespace Velia.Core.Tests;

public sealed class ShortcutKeySuppressionStateTests
{
    [Fact]
    public void SuppressesOnlyTheFinalSpaceKeyForTheEnabledChord()
    {
        var state = new ShortcutKeySuppressionState();

        Assert.False(state.ShouldSuppress(0x11, false, true, false, true));
        Assert.False(state.ShouldSuppress(0x10, false, true, true, true));
        Assert.True(state.ShouldSuppress(0x20, false, false, false, true));
        Assert.True(state.IsShortcutDown);
        Assert.True(state.ShouldSuppress(0x20, true, true, true, true));
        Assert.False(state.IsShortcutDown);
        Assert.False(state.ShouldSuppress(0x41, false, true, true, true));
    }

    [Fact]
    public void SuppressesTheMatchingKeyUpEvenWhenModifiersWereReleasedFirst()
    {
        var state = new ShortcutKeySuppressionState();

        Assert.True(state.ShouldSuppress(0x20, false, true, true, true));
        Assert.True(state.IsShortcutDown);
        Assert.False(state.ShouldSuppress(0xa0, true, true, false, true));
        Assert.False(state.IsShortcutDown);
        Assert.True(state.ShouldSuppress(0x20, true, true, false, true));
        Assert.False(state.ShouldSuppress(0x20, true, false, false, false));
    }

    [Fact]
    public void LeavesSpaceUntouchedWhenTheShortcutIsDisabledOrIncomplete()
    {
        var state = new ShortcutKeySuppressionState();

        Assert.False(state.ShouldSuppress(0x20, false, true, true, false));
        Assert.False(state.ShouldSuppress(0x20, true, true, true, false));
        Assert.False(state.ShouldSuppress(0x20, false, true, false, true));
        Assert.False(state.ShouldSuppress(0x20, true, true, false, true));
    }
}
