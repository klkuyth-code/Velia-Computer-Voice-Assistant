using Velia.Core.Input;
using Xunit;

namespace Velia.Core.Tests;

public sealed class LongConversationShortcutStateTests
{
    private const int LeftControl = 0xa2;
    private const int LeftAlt = 0xa4;
    private const int L = 0x4c;

    [Fact]
    public void EmitsOnePressAndOneReleaseWhileIgnoringKeyRepeat()
    {
        var state = new LongConversationShortcutState();

        Assert.Null(state.Sample(LeftControl, isKeyUp: false, controlDown: true, altDown: false));
        Assert.Null(state.Sample(LeftAlt, isKeyUp: false, controlDown: true, altDown: true));
        Assert.Equal(PhysicalKeyEdge.Pressed, state.Sample(L, isKeyUp: false, controlDown: true, altDown: true));
        Assert.Null(state.Sample(L, isKeyUp: false, controlDown: true, altDown: true));
        Assert.Equal(PhysicalKeyEdge.Released, state.Sample(L, isKeyUp: true, controlDown: true, altDown: true));
    }

    [Fact]
    public void ReleasingAModifierEndsTheChordExactlyOnce()
    {
        var state = new LongConversationShortcutState();
        _ = state.Sample(LeftControl, isKeyUp: false, controlDown: true, altDown: false);
        _ = state.Sample(LeftAlt, isKeyUp: false, controlDown: true, altDown: true);
        Assert.Equal(PhysicalKeyEdge.Pressed, state.Sample(L, isKeyUp: false, controlDown: true, altDown: true));

        Assert.Equal(PhysicalKeyEdge.Released, state.Sample(LeftAlt, isKeyUp: true, controlDown: true, altDown: false));
        Assert.Null(state.Sample(L, isKeyUp: true, controlDown: true, altDown: false));
    }

    [Fact]
    public void IgnoresLWithoutBothModifiers()
    {
        var state = new LongConversationShortcutState();

        Assert.Null(state.Sample(L, isKeyUp: false, controlDown: true, altDown: false));
        Assert.Null(state.Sample(L, isKeyUp: true, controlDown: true, altDown: false));
    }

    [Fact]
    public void DetectsModifiersThatWereAlreadyHeldWhenTheHookStarted()
    {
        var state = new LongConversationShortcutState();

        Assert.Equal(PhysicalKeyEdge.Pressed, state.Sample(L, isKeyUp: false, controlDown: true, altDown: true));
        Assert.Equal(PhysicalKeyEdge.Released, state.Sample(L, isKeyUp: true, controlDown: true, altDown: true));
    }
}
