using Nexus.Core.Models;
using Nexus.Core.Security;

namespace Nexus.Core.Tests;

public sealed class PermissionGuardTests
{
    private readonly PermissionGuard _guard = new();

    [Fact]
    public void ObserveMode_AllowsReadOnlyAction()
    {
        var request = Request(ActionRisk.ReadOnly);
        var decision = _guard.Evaluate(request, new AssistantSettings(PermissionLevel: PermissionLevel.Observe));
        Assert.True(decision.Allowed);
        Assert.False(decision.RequiresConfirmation);
    }

    [Fact]
    public void ObserveMode_BlocksStateChange()
    {
        var decision = _guard.Evaluate(Request(ActionRisk.Low), new AssistantSettings(PermissionLevel: PermissionLevel.Observe));
        Assert.False(decision.Allowed);
        Assert.False(decision.RequiresConfirmation);
    }

    [Fact]
    public void CriticalAction_IsNeverUnattended()
    {
        var decision = _guard.Evaluate(Request(ActionRisk.Critical),
            new AssistantSettings(PermissionLevel: PermissionLevel.TrustedRoutines), true);
        Assert.False(decision.Allowed);
        Assert.True(decision.RequiresConfirmation);
    }

    [Fact]
    public void DestructiveAction_AlwaysRequiresConfirmation()
    {
        var request = Request(ActionRisk.Low) with { IsDestructive = true };
        var decision = _guard.Evaluate(request,
            new AssistantSettings(PermissionLevel: PermissionLevel.TrustedRoutines), true);
        Assert.False(decision.Allowed);
        Assert.True(decision.RequiresConfirmation);
    }

    private static ActionRequest Request(ActionRisk risk) =>
        new("1", "test", "Test action", null, risk, false, DateTimeOffset.Now);
}
