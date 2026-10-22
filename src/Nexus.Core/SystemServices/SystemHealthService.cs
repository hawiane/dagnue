using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Nexus.Core.Models;

namespace Nexus.Core.SystemServices;

public sealed class SystemHealthService
{
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;

    public SystemSnapshot Capture(bool codexConnected = false, int activeTasks = 0, int pendingApprovals = 0)
    {
        var memory = GetMemory();
        var drive = DriveInfo.GetDrives().FirstOrDefault(x => x.IsReady && x.Name.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase));
        var totalDisk = drive?.TotalSize ?? 0;
        var freeDisk = drive?.AvailableFreeSpace ?? 0;
        var diskPercent = totalDisk == 0 ? 0 : 100d * (totalDisk - freeDisk) / totalDisk;
        var (power, battery) = GetPower();

        return new SystemSnapshot(
            DateTimeOffset.Now,
            GetCpuPercent(),
            memory.Total == 0 ? 0 : 100d * memory.Used / memory.Total,
            memory.Used,
            memory.Total,
            diskPercent,
            freeDisk,
            power,
            battery,
            NetworkInterface.GetIsNetworkAvailable(),
            activeTasks,
            pendingApprovals,
            codexConnected);
    }

    private double GetCpuPercent()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var idleValue = ToUInt64(idle);
        var kernelValue = ToUInt64(kernel);
        var userValue = ToUInt64(user);
        var idleDelta = idleValue - _previousIdle;
        var totalDelta = kernelValue - _previousKernel + userValue - _previousUser;
        _previousIdle = idleValue;
        _previousKernel = kernelValue;
        _previousUser = userValue;
        return totalDelta == 0 ? 0 : Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100);
    }

    private static (ulong Used, ulong Total) GetMemory()
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? (status.TotalPhysical - status.AvailablePhysical, status.TotalPhysical)
            : (0, 0);
    }

    private static (string Status, int Battery) GetPower()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var status)) return ("Unknown", -1);
        var label = status.ACLineStatus == 1 ? "Plugged in" : "On battery";
        var battery = status.BatteryLifePercent == 255 ? -1 : status.BatteryLifePercent;
        return (label, battery);
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
