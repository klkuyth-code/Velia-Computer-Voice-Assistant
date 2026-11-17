namespace Velia.Core.Tools;

[Flags]
internal enum ToolRisk
{
    ReadOnly = 0,
    ScreenCapture = 1 << 0,
    LocalMutation = 1 << 1,
    Destructive = 1 << 2,
    ExternalCommunication = 1 << 3,
    Financial = 1 << 4,
    AccountOrSecurityChange = 1 << 5,
}

internal enum ToolConsentDecision
{
    Allow,
    RequireConfirmation,
    Deny,
}

internal sealed record ToolConsentContext(
    bool ExplicitlyRequested,
    bool ContinuousVisionConsent,
    bool HasFreshConfirmation);

/// <summary>
/// Central policy used before a future tool adapter executes. The model cannot
/// downgrade a tool's declared risk or invent consent.
/// </summary>
internal static class ToolConsentPolicy
{
    private const ToolRisk ConfirmationRisks =
        ToolRisk.Destructive |
        ToolRisk.ExternalCommunication |
        ToolRisk.Financial |
        ToolRisk.AccountOrSecurityChange;

    public static ToolConsentDecision Evaluate(ToolRisk risk, ToolConsentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if ((risk & ToolRisk.ScreenCapture) != 0 &&
            !context.ExplicitlyRequested &&
            !context.ContinuousVisionConsent)
        {
            return ToolConsentDecision.Deny;
        }

        if ((risk & ConfirmationRisks) != 0)
        {
            return context.HasFreshConfirmation
                ? ToolConsentDecision.Allow
                : ToolConsentDecision.RequireConfirmation;
        }

        if ((risk & ToolRisk.LocalMutation) != 0 && !context.ExplicitlyRequested)
        {
            return ToolConsentDecision.Deny;
        }

        return ToolConsentDecision.Allow;
    }
}
