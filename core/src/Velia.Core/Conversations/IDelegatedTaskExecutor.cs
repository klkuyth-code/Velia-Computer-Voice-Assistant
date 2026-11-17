namespace Velia.Core.Conversations;

using Velia.Core.DesktopVisual;
using Velia.Core.WindowsControl;

internal interface IDelegatedTaskExecutor
{
    Task<DelegatedTaskExecutionResult> ExecuteDelegatedTaskAsync(
        string task,
        CancellationToken cancellationToken);

    Task<DelegatedTaskExecutionResult> ExecuteDeterministicWindowsTaskAsync(
        string task,
        CancellationToken cancellationToken) => ExecuteDelegatedTaskAsync(task, cancellationToken);

    Task<DelegatedTaskExecutionResult> ExecuteRoutedTaskAsync(
        string task,
        string relationship,
        CancellationToken cancellationToken) => ExecuteDelegatedTaskAsync(task, cancellationToken);

    DelegatedTaskSummary? GetActiveTaskSummary() => null;

    string? GetCurrentWebPlatform() => null;

    ActiveInteractionSurfaceKind GetActiveInteractionSurface() => ActiveInteractionSurfaceKind.Unknown;

    bool TryGetDeterministicTaskKey(
        string task,
        string? currentWebPlatform,
        out string key) =>
        DeterministicLocalTaskIntentDetector.TryGetCanonicalKey(
            task,
            out key,
            currentWebPlatform ?? GetCurrentWebPlatform());
}

internal sealed record DelegatedTaskExecutionResult(bool Ok, string Value);

internal sealed record DelegatedTaskSummary(
    string Goal,
    string Phase,
    string Status,
    string? TargetApplication);
