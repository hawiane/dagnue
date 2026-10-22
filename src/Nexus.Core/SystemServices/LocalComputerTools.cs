using System.Diagnostics;
using Nexus.Core.Models;

namespace Nexus.Core.SystemServices;

public sealed class LocalComputerTools
{
    private static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad"] = "notepad.exe",
        ["calculator"] = "calc.exe",
        ["paint"] = "mspaint.exe",
        ["explorer"] = "explorer.exe",
        ["settings"] = "ms-settings:",
        ["terminal"] = "wt.exe"
    };

    public Task<IReadOnlyList<string>> SearchFilesAsync(string root, string query, int maxResults = 50, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];
            var results = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && results.Count < maxResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(current))
                    {
                        if (Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(file);
                            if (results.Count >= maxResults) break;
                        }
                    }
                    foreach (var directory in Directory.EnumerateDirectories(current)) pending.Push(directory);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return results;
        }, cancellationToken);
    }

    public ActionRequest CreateLaunchRequest(string appName)
    {
        var target = KnownApps.TryGetValue(appName.Trim(), out var executable) ? executable : appName.Trim();
        return new ActionRequest(Guid.NewGuid().ToString("N"), "launch-app", $"Open {appName}", target,
            ActionRisk.Low, false, DateTimeOffset.Now, new Dictionary<string, string> { ["target"] = target });
    }

    public Process? Launch(string target)
    {
        return Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
