using Velia.Core.Runtime;
using Velia.Core.Tasks;
using Xunit;

namespace Velia.Core.Tests;

public sealed class AssistantUiStateMachineTests
{
    [Fact]
    public void KeepsThinkingBetweenVoiceEndAndOutputStart()
    {
        var state = new AssistantUiStateMachine();

        state.BeginUserTurn();
        Assert.Equal(AssistantVisualState.Listening, state.State);

        state.EndUserVoice();
        Assert.Equal(AssistantVisualState.Thinking, state.State);

        state.BeginOutput();
        Assert.Equal(AssistantVisualState.Speaking, state.State);

        state.SetPlaybackActive(false);
        state.CompleteTurn(TurnPresentation.Conversation);
        Assert.Equal(AssistantVisualState.Idle, state.State);
    }

    [Fact]
    public void AwaitingInputAndCancellationDoNotLeaveAStuckThinkingState()
    {
        var state = new AssistantUiStateMachine();
        state.BeginUserTurn();
        state.EndUserVoice();

        state.NoteMissionState(MissionLifecycleState.AwaitingInput);
        Assert.Equal(AssistantVisualState.Idle, state.State);

        state.NoteMissionState(MissionLifecycleState.Running);
        Assert.Equal(AssistantVisualState.Thinking, state.State);

        state.NoteMissionState(MissionLifecycleState.Cancelled);
        Assert.Equal(AssistantVisualState.Idle, state.State);
    }
}
