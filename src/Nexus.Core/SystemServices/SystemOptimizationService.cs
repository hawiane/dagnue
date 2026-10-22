using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nexus.Core.SystemServices;

public enum SystemOptimizationKind
{
    Cpu,
    Memory,
    Disk
}

public sealed record SystemOptimizationResult(
    SystemOptimizationKind Kind,
    string Summary,
    long BytesFreed = 0,
    int ProcessesAdjusted = 0);

/// <summary>
/// Performs conservative, reversible maintenance. It never terminates applications,
/// empties the recycle bin, or removes files outside the current user's temp folder.
/// </summary>
public sealed class SystemOptimizationService
{
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "audiodg", "csrss", "dwm", "explorer", "fontdrvhost", "idle", "lsass",
        "registry", "services", "sihost", "smss", "spoolsv", "startmenuexperiencehost",
        "svchost", "system", "taskhostw", "wininit", "winlogon"
    };

    public async Task<SystemOptimizationResult> OptimizeAsync(
        SystemOptimizationKind kind,
        CancellationToken cancellationToken = default) => kind switch
        {
            SystemOptimizationKind.Cpu => await OptimizeCpuAsync(cancellationToken),
            SystemOptimizationKind.Memory => await OptimizeMemoryAsync(cancellationToken),
            SystemOptimizationKind.Disk => await OptimizeDiskAsync(cancellationToken: cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    public async Task<SystemOptimizationResult> OptimizeCpuAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return new(SystemOptimizationKind.Cpu, "CPU optimization is available on Windows.");

        var current = Process.GetCurrentProcess();
        var candidates = new List<(Process Process, TimeSpan StartCpu)>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == current.Id || process.SessionId != current.SessionId ||
                    process.MainWindowHandle != IntPtr.Zero || ProtectedProcesses.Contains(process.ProcessName) ||
                    process.PriorityClass != ProcessPriorityClass.Normal)
                {
                    process.Dispose();
                    continue;
                }

                candidates.Add((process, process.TotalProcessorTime));
            }
            catch
            {
                process.Dispose();
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
        var adjusted = new List<Process>();
        foreach (var (process, startCpu) in candidates)
        {
            try
            {
                process.Refresh();
                var cpuPercent = (process.TotalProcessorTime - startCpu).TotalMilliseconds /
                                 (700d * Math.Max(1, Environment.ProcessorCount)) * 100d;
                if (cpuPercent < 8d) continue;
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
                adjusted.Add(process);
            }
            catch
            {
                process.Dispose();
            }
        }

        foreach (var (process, _) in candidates)
            if (!adjusted.Contains(process)) process.Dispose();

        if (adjusted.Count > 0) _ = RestorePrioritiesAsync(adjusted);
        var summary = adjusted.Count == 0
            ? "CPU checked; no safe background worker needed adjustment."
            : $"Temporarily eased {adjusted.Count} high-CPU background worker{(adjusted.Count == 1 ? "" : "s")} for 3 minutes.";
        return new(SystemOptimizationKind.Cpu, summary, ProcessesAdjusted: adjusted.Count);
    }

    public Task<SystemOptimizationResult> OptimizeMemoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var process = Process.GetCurrentProcess();
        var before = process.WorkingSet64;

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: true);

        if (OperatingSystem.IsWindows()) EmptyWorkingSet(process.Handle);
        process.Refresh();
        var freed = Math.Max(0, before - process.WorkingSet64);
        var summary = freed > 0
            ? $"Released {FormatBytes(freed)} from DAGNUE; other applications were left untouched."
            : "DAGNUE memory was already compact; other applications were left untouched.";
        return Task.FromResult(new SystemOptimizationResult(SystemOptimizationKind.Memory, summary, freed));
    }

    public async Task<SystemOptimizationResult> OptimizeDiskAsync(
        string? tempRoot = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(tempRoot ?? Path.GetTempPath());
        if (!Directory.Exists(root))
            return new(SystemOptimizationKind.Disk, "The user temp folder is already clear.");

        var cutoff = DateTime.UtcNow.Subtract(TimeSpan.FromHours(24));
        long freed = 0;
        var removed = 0;
        await Task.Run(() =>
        {
            foreach (var file in EnumerateFilesSafely(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc >= cutoff || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    var length = info.Length;
                    info.Delete();
                    freed += length;
                    removed++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            RemoveEmptyDirectories(root, cutoff, cancellationToken);
        }, cancellationToken);

        var summary = removed == 0
            ? "User temp files are already clean; recent and in-use files were kept."
            : $"Safely removed {removed} stale temp item{(removed == 1 ? "" : "s")} ({FormatBytes(freed)}).";
        return new(SystemOptimizationKind.Disk, summary, freed);
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files) yield return file;
            foreach (var child in directories)
            {
                try
                {
                    if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) pending.Push(child);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void RemoveEmptyDirectories(string root, DateTime cutoff, CancellationToken cancellationToken)
    {
        string[] directories;
        try { directories = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly); }
        catch { return; }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
                RemoveEmptyDirectories(directory, cutoff, cancellationToken);
                var info = new DirectoryInfo(directory);
                if (info.LastWriteTimeUtc < cutoff && !info.EnumerateFileSystemInfos().Any()) info.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task RestorePrioritiesAsync(List<Process> processes)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3)); }
        finally
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited && process.PriorityClass == ProcessPriorityClass.BelowNormal)
                        process.PriorityClass = ProcessPriorityClass.Normal;
                }
                catch { }
                finally { process.Dispose(); }
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
