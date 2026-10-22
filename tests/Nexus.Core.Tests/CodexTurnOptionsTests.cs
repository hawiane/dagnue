using Nexus.Core.Models;

namespace Nexus.Core.Tests;

public sealed class CodexTurnOptionsTests
{
    [Fact]
    public void DefaultApprovalPolicy_UsesCurrentAppServerVariant()
    {
        var options = new CodexTurnOptions();

        Assert.Equal("untrusted", options.ApprovalPolicy);
    }

    [Theory]
    [InlineData("unlessTrusted", "untrusted")]
    [InlineData("untrusted", "untrusted")]
    [InlineData("onRequest", "on-request")]
    [InlineData("on-request", "on-request")]
    [InlineData("granular", "granular")]
    [InlineData("never", "never")]
    [InlineData("unexpected-value", "untrusted")]
    [InlineData(null, "untrusted")]
    public void Normalize_OnlyReturnsAcceptedAppServerVariants(string? input, string expected)
    {
        Assert.Equal(expected, CodexApprovalPolicies.Normalize(input));
    }
}
