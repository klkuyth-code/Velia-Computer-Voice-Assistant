using Velia.Core.Tools;
using Xunit;

namespace Velia.Core.Tests;

public sealed class ToolConsentPolicyTests
{
    [Fact]
    public void ScreenCaptureRequiresCurrentIntentOrContinuousConsent()
    {
        Assert.Equal(
            ToolConsentDecision.Deny,
            ToolConsentPolicy.Evaluate(
                ToolRisk.ScreenCapture,
                new ToolConsentContext(false, false, false)));

        Assert.Equal(
            ToolConsentDecision.Allow,
            ToolConsentPolicy.Evaluate(
                ToolRisk.ScreenCapture,
                new ToolConsentContext(true, false, false)));
    }

    [Theory]
    [InlineData((int)ToolRisk.Destructive)]
    [InlineData((int)ToolRisk.ExternalCommunication)]
    [InlineData((int)ToolRisk.Financial)]
    [InlineData((int)ToolRisk.AccountOrSecurityChange)]
    public void HighImpactActionsRequireFreshConfirmation(int riskValue)
    {
        var risk = (ToolRisk)riskValue;
        Assert.Equal(
            ToolConsentDecision.RequireConfirmation,
            ToolConsentPolicy.Evaluate(risk, new ToolConsentContext(true, false, false)));
        Assert.Equal(
            ToolConsentDecision.Allow,
            ToolConsentPolicy.Evaluate(risk, new ToolConsentContext(true, false, true)));
    }

    [Fact]
    public void ModelCannotMutateLocalStateWithoutAnExplicitUserRequest()
    {
        Assert.Equal(
            ToolConsentDecision.Deny,
            ToolConsentPolicy.Evaluate(
                ToolRisk.LocalMutation,
                new ToolConsentContext(false, false, false)));
    }
}
