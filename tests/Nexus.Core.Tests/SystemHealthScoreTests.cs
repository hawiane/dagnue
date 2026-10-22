using Nexus.Core.SystemServices;

namespace Nexus.Core.Tests;

public sealed class SystemHealthScoreTests
{
    [Theory]
    [InlineData(0, 0, 0, 100)]
    [InlineData(100, 100, 100, 0)]
    [InlineData(20, 84, 87, 41)]
    public void Calculate_ReturnsWeightedLiveHeadroom(double cpu, double memory, double disk, int expected)
    {
        Assert.Equal(expected, SystemHealthScore.Calculate(cpu, memory, disk));
    }

    [Fact]
    public void Calculate_ClampsInvalidPercentages()
    {
        Assert.Equal(60, SystemHealthScore.Calculate(-20, 120, 0));
    }
}
