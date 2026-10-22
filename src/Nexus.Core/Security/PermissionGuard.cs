using Nexus.Core.Models;

namespace Nexus.Core.Security;

public sealed class PermissionGuard
{
    private static readonly string[] CriticalTerms =
    [
        "format", "diskpart", "bcdedit", "reg delete", "remove-item -recurse",
        "password", "credential", "purchase", "payment", "administrator"
    ];

    public ActionDecision Evaluate(ActionRequest request, AssistantSettings settings, bool isTrustedRoutine = false)
    {
        if (request.Risk == ActionRisk.Critical || ContainsCriticalTerm(request))
        {
            return new(false, true, "Critical actions are never allowed unattended.");
        }

        if (request.IsDestructive || request.Risk == ActionRisk.High)
        {
            return new(false, true, "Destructive or high-impact actions require explicit confirmation.");
        }

        return settings.PermissionLevel switch
        {
            PermissionLevel.Observe when request.Risk != ActionRisk.ReadOnly =>
                new(false, false, "Observe mode blocks actions that change computer state."),
            PermissionLevel.Suggest when request.Risk != ActionRisk.ReadOnly =>
                new(false, true, "Suggest mode can prepare this action but cannot execute it."),
            PermissionLevel.Confirm when request.Risk != ActionRisk.ReadOnly =>
                new(false, true, "Confirm mode requires approval before execution."),
            PermissionLevel.TrustedRoutines when request.Risk == ActionRisk.Medium && !isTrustedRoutine =>
                new(false, true, "Medium-impact actions must be part of an approved trusted routine."),
            _ => new(true, false, "Action is within the active permission boundary.")
        };
    }

    private static bool ContainsCriticalTerm(ActionRequest request)
    {
        var text = $"{request.Tool} {request.Summary} {request.Detail}";
        return CriticalTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
