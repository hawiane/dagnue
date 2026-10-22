using System.Runtime.Versioning;
using Nexus.Core.SystemServices;

namespace Nexus.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class ApplicationInventoryServiceTests
{
    [Fact]
    public void UninstallSafety_AllowsThirdPartyAppWithQuietCommand()
    {
        var result = ApplicationInventoryService.EvaluateUninstallSafety(
            "Example Notes", "Example Software", @"C:\Program Files\Example Notes",
            @"C:\Program Files\Example Notes\uninstall.exe /S", noRemove: false);

        Assert.True(result.Safe);
    }

    [Theory]
    [InlineData("Device Driver", "Vendor", "uninstall.exe /S", false)]
    [InlineData("Normal App", "Microsoft Corporation", "msiexec.exe /x {00000000-0000-0000-0000-000000000000} /qn", false)]
    [InlineData("Normal App", "Vendor", "uninstall.exe /S", true)]
    public void UninstallSafety_RejectsProtectedOrUnverifiableApps(
        string name, string publisher, string? command, bool noRemove)
    {
        var result = ApplicationInventoryService.EvaluateUninstallSafety(
            name, publisher, @"C:\Program Files\Example", command, noRemove);

        Assert.False(result.Safe);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public void UninstallSafety_UsesWindowsSettingsWhenNoRegisteredCommandExists()
    {
        var result = ApplicationInventoryService.EvaluateUninstallSafety(
            "Normal App", "Vendor", @"C:\Program Files\Example", null, noRemove: false);

        Assert.True(result.Safe);
        Assert.Contains("Windows Installed Apps", result.Reason);
    }
}
