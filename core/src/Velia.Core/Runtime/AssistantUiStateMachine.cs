using Velia.Core.Tasks;

namespace Velia.Core.Runtime;

internal enum AssistantVisualState
{
    Idle,
    Listening,
    Thinking,
    Speaking,
}

internal enum TurnPresentation
{
    Conversation,
    FinalTask,
    Silent,
}

/// <summary>
/// Single source of truth for the assistant indicator. Priority is user voice,
/// playback/output, pending work, then idle.
/// </summary>
internal sealed class AssistantUiStateMachine
{
    private bool userVoiceActive;
    private bool awaitingTurn;
    private bool taskInProgress;
    private bool outputActive;
    private bool playbackActive;
    private bool taskActivitySuppressed;

    public AssistantVisualState State => userVoiceActive
        ? AssistantVisualState.Listening
        : playbackActive || outputActive
            ? AssistantVisualState.Speaking
            : taskInProgress
                ? AssistantVisualState.Thinking
                : AssistantVisualState.Idle;

    public void BeginUserTurn()
    {
        userVoiceActive = true;
        awaitingTurn = true;
        taskActivitySuppressed = false;
        outputActive = false;
        playbackActive = false;
    }

    public void EndUserVoice()
    {
        userVoiceActive = false;
        if (awaitingTurn && !taskActivitySuppressed)
        {
            taskInProgress = true;
        }
    }

    public void BeginOutput() => outputActive = true;

    public void InterruptOutput()
    {
        outputActive = false;
        playbackActive = false;
    }

    public void SetPlaybackActive(bool active)
    {
        playbackActive = active;
        if (!active)
        {
            outputActive = false;
        }
    }

    public void NoteMissionState(MissionLifecycleState state)
    {
        if (state == MissionLifecycleState.AwaitingInput)
        {
            awaitingTurn = false;
            taskInProgress = false;
            taskActivitySuppressed = false;
            return;
        }

        if (state == MissionLifecycleState.Cancelled)
        {
            CancelTaskTurn();
            return;
        }

        if (!taskActivitySuppressed)
        {
            taskInProgress = true;
        }
    }

    public void CancelTaskTurn()
    {
        awaitingTurn = false;
        taskInProgress = false;
        outputActive = false;
        playbackActive = false;
        taskActivitySuppressed = true;
    }

    public void CompleteTurn(TurnPresentation presentation)
    {
        awaitingTurn = false;
        if (presentation is TurnPresentation.Conversation or TurnPresentation.FinalTask)
        {
            taskInProgress = false;
        }
    }

    public void Reset()
    {
        userVoiceActive = false;
        awaitingTurn = false;
        taskInProgress = false;
        outputActive = false;
        playbackActive = false;
        taskActivitySuppressed = false;
    }
}
