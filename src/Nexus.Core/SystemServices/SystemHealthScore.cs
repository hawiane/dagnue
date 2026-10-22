namespace Nexus.Core.SystemServices;

public static class SystemHealthScore
{
    /// <summary>
    /// Returns available system headroom from 0-100. CPU and memory each contribute
    /// 40% of the pressure score; system-drive capacity contributes 20%.
    /// </summary>
    public static int Calculate(double cpuPercent, double memoryPercent, double diskPercent)
    {
        var pressure = Math.Clamp(cpuPercent, 0, 100) * 0.40
            + Math.Clamp(memoryPercent, 0, 100) * 0.40
            + Math.Clamp(diskPercent, 0, 100) * 0.20;
        return (int)Math.Round(100 - pressure, MidpointRounding.AwayFromZero);
    }
}
