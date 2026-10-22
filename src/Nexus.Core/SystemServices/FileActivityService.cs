using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Core.Models;

namespace Nexus.Core.SystemServices;

public sealed class FileActivityService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _driveRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _watcherGate = new();
    private readonly ConcurrentDictionary<string, long> _recentSignals = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SignalBurst> _signalBursts = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;
    private Timer? _driveRefreshTimer;

    public event EventHandler<FileActivityEventArgs>? ActivityDetected;

    public void Start(IEnumerable<string>? roots = null)
    {
        if (_started) return;
        _started = true;
        foreach (var root in ResolveRoots(roots))
        {
            AddWatcher(root, includeSubdirectories: true, recentItems: false);
            _driveRoots.Add(root);
        }

        var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!string.IsNullOrWhiteSpace(recent) && Directory.Exists(recent))
            AddWatcher(recent, includeSubdirectories: false, recentItems: true);
        if (roots is null)
            _driveRefreshTimer = new Timer(_ => RefreshDriveRoots(), null, TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(4));
    }

    private void RefreshDriveRoots()
    {
        if (!_started) return;
        try
        {
            var current = ResolveRoots(null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> attached;
            List<string> detached;
            lock (_watcherGate)
            {
                attached = current.Where(root => !_driveRoots.Contains(root)).ToList();
                detached = _driveRoots.Where(root => !current.Contains(root)).ToList();
                foreach (var root in attached)
                {
                    AddWatcher(root, includeSubdirectories: true, recentItems: false);
                    _driveRoots.Add(root);
                }
                foreach (var root in detached)
                {
                    foreach (var watcher in _watchers.Where(watcher =>
                                 watcher.IncludeSubdirectories && watcher.Path.Equals(root,
                                     StringComparison.OrdinalIgnoreCase)).ToArray())
                    {
                        watcher.Dispose();
                        _watchers.Remove(watcher);
                    }
                    _driveRoots.Remove(root);
                }
            }
            foreach (var root in attached)
                Publish(new FileActivity(FileActivityKind.Created, root, null, true,
                    DateTimeOffset.Now, "DRIVE_ATTACH"));
            foreach (var root in detached)
                Publish(new FileActivity(FileActivityKind.Deleted, root, null, true,
                    DateTimeOffset.Now, "DRIVE_DETACH"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void TrackApplicationAccess(string path, bool isDirectory = false, string source = "DAGNUE")
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Publish(new FileActivity(FileActivityKind.Accessed, path, null, isDirectory,
            DateTimeOffset.Now, source, ReadFileSize(path)));
    }

    private void AddWatcher(string root, bool includeSubdirectories, bool recentItems)
    {
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = includeSubdirectories,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                               NotifyFilters.CreationTime | NotifyFilters.Size,
                Filter = recentItems ? "*.lnk" : "*",
                InternalBufferSize = 32 * 1024,
                EnableRaisingEvents = true
            };

            if (recentItems)
            {
                watcher.Created += RecentItemChanged;
                watcher.Changed += RecentItemChanged;
            }
            else
            {
                watcher.Changed += (_, e) => PublishPath(FileActivityKind.Modified, e.FullPath, null, "FILESYSTEM");
                watcher.Created += (_, e) => PublishPath(FileActivityKind.Created, e.FullPath, null, "FILESYSTEM");
                watcher.Deleted += (_, e) => PublishPath(FileActivityKind.Deleted, e.FullPath, null, "FILESYSTEM");
                watcher.Renamed += (_, e) => PublishPath(FileActivityKind.Moved, e.FullPath, e.OldFullPath, "FILESYSTEM");
            }
            watcher.Error += (_, _) => { };
            _watchers.Add(watcher);
        }
        catch (ArgumentException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RecentItemChanged(object sender, FileSystemEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120);
                if (!OperatingSystem.IsWindows()) return;
                var target = ResolveShortcut(e.FullPath);
                if (!string.IsNullOrWhiteSpace(target) && (File.Exists(target) || Directory.Exists(target)))
                    Publish(new FileActivity(FileActivityKind.Accessed, target, null, Directory.Exists(target),
                        DateTimeOffset.Now, "WINDOWS RECENT", ReadFileSize(target)));
            }
            catch { }
        });
    }

    private void PublishPath(FileActivityKind kind, string path, string? previousPath, string source)
    {
        var isDirectory = Directory.Exists(path) || previousPath is not null && Directory.Exists(previousPath);
        Publish(new FileActivity(kind, path, previousPath, isDirectory, DateTimeOffset.Now, source,
            isDirectory ? 0 : ReadFileSize(path)));
    }

    private void Publish(FileActivity activity)
    {
        var now = Environment.TickCount64;
        var burst = _signalBursts.AddOrUpdate(activity.Path,
            _ => new SignalBurst(now, 1),
            (_, previous) => now - previous.WindowStarted > 2_000
                ? new SignalBurst(now, 1)
                : previous with { Count = Math.Min(32, previous.Count + 1) });
        activity = activity with { SignalCount = burst.Count };

        var key = $"{activity.Kind}|{activity.Path}";
        if (_recentSignals.TryGetValue(key, out var previous) && now - previous < 280) return;
        _recentSignals[key] = now;
        ActivityDetected?.Invoke(this, new FileActivityEventArgs(activity));

        if (_recentSignals.Count > 512)
            foreach (var stale in _recentSignals.Where(pair => now - pair.Value > 30_000).Select(pair => pair.Key))
                _recentSignals.TryRemove(stale, out _);

        if (_signalBursts.Count > 512)
            foreach (var stale in _signalBursts.Where(pair => now - pair.Value.WindowStarted > 30_000)
                         .Select(pair => pair.Key))
                _signalBursts.TryRemove(stale, out _);
    }

    private static long ReadFileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    [SupportedOSPlatform("windows")]
    private static string? ResolveShortcut(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            shell = Activator.CreateInstance(type);
            if (shell is null) return null;
            shortcut = ((dynamic)shell).CreateShortcut(shortcutPath);
            return ((dynamic)shortcut).TargetPath as string;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static IReadOnlyList<string> ResolveRoots(IEnumerable<string>? roots)
    {
        var candidates = roots?.ToArray() ?? DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(drive => drive.RootDirectory.FullName)
            .ToArray();
        return candidates.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Dispose()
    {
        _driveRefreshTimer?.Dispose();
        _driveRefreshTimer = null;
        lock (_watcherGate)
        {
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
            _driveRoots.Clear();
        }
        _started = false;
    }

    private readonly record struct SignalBurst(long WindowStarted, int Count);
}
