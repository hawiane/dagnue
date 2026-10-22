using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Nexus.Core.Models;

namespace Nexus.Core.SystemServices;

[SupportedOSPlatform("windows")]
public sealed class ApplicationInventoryService
{
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "dwm", "explorer", "fontdrvhost", "lsass", "registry", "services", "sihost",
        "smss", "spoolsv", "startmenuexperiencehost", "svchost", "system", "taskhostw",
        "wininit", "winlogon", "nexus.app", "dagnue"
    };

    private static readonly string[] ProtectedAppTerms =
    [
        "antivirus", "bluetooth", "chipset", "driver", "firmware", "framework", "graphics",
        "microsoft visual c++", ".net runtime", "redistributable", "security update", "system component",
        "windows update", "wi-fi", "wireless"
    ];

    public Task<IReadOnlyList<RunningApplicationInfo>> GetRunningApplicationsAsync(
        CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<RunningApplicationInfo>>(() =>
    {
        var currentId = Environment.ProcessId;
        var windowsDirectory = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var result = new List<RunningApplicationInfo>();
        foreach (var window in EnumerateVisibleWindows())
        {
            Process? process = null;
            try { process = Process.GetProcessById(window.ProcessId); }
            catch (ArgumentException) { continue; }
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.Id == currentId) continue;
                    var path = TryGetProcessPath(process.Id);
                    var name = process.ProcessName;
                    var protectedProcess = ProtectedProcesses.Contains(name) ||
                        (path is not null && IsWithin(path, windowsDirectory));
                    result.Add(new RunningApplicationInfo(process.Id, name, window.Title, path,
                        SafeWorkingSet(process), !protectedProcess,
                        protectedProcess ? "Protected Windows or DAGNUE process" : "User application"));
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
        }
        return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProcessId).ToArray();
    }, cancellationToken);

    private static IReadOnlyList<(int ProcessId, string Title)> EnumerateVisibleWindows()
    {
        const uint getWindowOwner = 4;
        var windows = new Dictionary<int, string>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, getWindowOwner) != nint.Zero) return true;
            var length = GetWindowTextLength(window);
            if (length <= 0) return true;
            var title = new StringBuilder(length + 1);
            if (GetWindowText(window, title, title.Capacity) <= 0 || string.IsNullOrWhiteSpace(title.ToString())) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId > 0 && (!windows.TryGetValue((int)processId, out var existing) || title.Length > existing.Length))
                windows[(int)processId] = title.ToString();
            return true;
        }, nint.Zero);
        return windows.Select(window => (window.Key, window.Value)).ToArray();
    }

    public Task<IReadOnlyList<InstalledApplicationInfo>> GetInstalledApplicationsAsync(
        CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<InstalledApplicationInfo>>(() =>
    {
        var collected = new Dictionary<string, InstalledApplicationInfo>(StringComparer.OrdinalIgnoreCase);
        var executionHistory = ReadUserExecutionHistory();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var key = uninstall.OpenSubKey(keyName);
                    if (key is null) continue;
                    var name = Value(key, "DisplayName");
                    if (string.IsNullOrWhiteSpace(name) || IntValue(key, "SystemComponent") == 1 ||
                        !string.IsNullOrWhiteSpace(Value(key, "ParentKeyName"))) continue;

                    var version = Value(key, "DisplayVersion");
                    var publisher = Value(key, "Publisher");
                    var installLocation = NormalizePath(Value(key, "InstallLocation"));
                    var displayIcon = NormalizeIconPath(Value(key, "DisplayIcon"));
                    var uninstallCommand = Value(key, "UninstallString");
                    if (string.IsNullOrWhiteSpace(uninstallCommand) && IntValue(key, "WindowsInstaller") == 1 &&
                        Guid.TryParse(keyName, out _)) uninstallCommand = $"msiexec.exe /x {keyName}";
                    var iconPath = ExistingPath(displayIcon) ?? ExistingPath(ExecutableFromCommand(uninstallCommand));
                    var noRemove = IntValue(key, "NoRemove") == 1;
                    var safety = EvaluateUninstallSafety(name, publisher, installLocation, uninstallCommand, noRemove);
                    var estimatedKilobytes = LongValue(key, "EstimatedSize");
                    var id = $"{hive}:{view}:{keyName}";
                    var item = new InstalledApplicationInfo(id, name.Trim(), NullIfBlank(version),
                        NullIfBlank(publisher), estimatedKilobytes > 0 ? estimatedKilobytes * 1024 : null,
                        installLocation, iconPath, MatchLastUsed(name, iconPath, executionHistory),
                        NullIfBlank(uninstallCommand), safety.Safe, safety.Reason);
                    var identity = $"{item.Name}|{item.Version}|{item.Publisher}";
                    if (!collected.TryGetValue(identity, out var existing) ||
                        (!existing.CanUninstall && item.CanUninstall)) collected[identity] = item;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return collected.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }, cancellationToken);

    public ApplicationActionResult Terminate(int processId)
    {
        if (processId <= 4 || processId == Environment.ProcessId)
            return new(false, "Protected process cannot be terminated.");
        try
        {
            using var process = Process.GetProcessById(processId);
            var path = TryGetProcessPath(process.Id);
            if (ProtectedProcesses.Contains(process.ProcessName) ||
                (path is not null && IsWithin(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows))))
                return new(false, "Protected Windows or DAGNUE process cannot be terminated.");
            process.Kill(entireProcessTree: true);
            return new(true, $"Ended {process.ProcessName} ({processId}).");
        }
        catch (ArgumentException) { return new(true, "Application was already closed."); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return new(false, $"End task failed: {exception.Message}");
        }
    }

    public ApplicationActionResult LaunchWindowsUninstaller(InstalledApplicationInfo application)
    {
        var safety = EvaluateUninstallSafety(application.Name, application.Publisher,
            application.InstallLocation, application.UninstallCommand, noRemove: false);
        if (!application.CanUninstall || !safety.Safe)
            return new(false, safety.Reason);
        try
        {
            if (string.IsNullOrWhiteSpace(application.UninstallCommand))
            {
                Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
                return new(true, $"Windows Installed Apps opened for {application.Name}.");
            }
            var arguments = SplitCommandLine(application.UninstallCommand);
            if (arguments.Count == 0) return new(false, "Registered uninstall command is invalid.");
            var start = new ProcessStartInfo(arguments[0]) { UseShellExecute = true };
            foreach (var argument in arguments.Skip(1)) start.ArgumentList.Add(argument);
            Process.Start(start);
            return new(true, $"Windows uninstaller opened for {application.Name}.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new(false, $"Uninstall failed: {exception.Message}");
        }
    }

    public static (bool Safe, string Reason) EvaluateUninstallSafety(string name, string? publisher,
        string? installLocation, string? quietCommand, bool noRemove)
    {
        if (noRemove) return (false, "Publisher has disabled removal");
        if (publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true)
            return (false, "Microsoft and Windows components are protected");
        if (ProtectedAppTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return (false, "Driver, runtime, security, or system component is protected");
        if (!string.IsNullOrWhiteSpace(installLocation) &&
            IsWithin(installLocation, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
            return (false, "Windows directory applications are protected");
        if (string.IsNullOrWhiteSpace(quietCommand)) return (true, "Opens Windows Installed Apps");
        return (true, "Registered Windows uninstall command");
    }

    private static long SafeWorkingSet(Process process)
    {
        try { return process.WorkingSet64; } catch { return 0; }
    }

    private static string? TryGetProcessPath(int processId)
    {
        const uint processQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(processQueryLimitedInformation, inheritHandle: false, processId);
        if (handle == nint.Zero) return null;
        try
        {
            var capacity = 32768u;
            var path = new StringBuilder((int)capacity);
            return QueryFullProcessImageName(handle, 0, path, ref capacity) ? path.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    private static string? Value(RegistryKey key, string name) => key.GetValue(name)?.ToString();
    private static int IntValue(RegistryKey key, string name) => Convert.ToInt32(key.GetValue(name) ?? 0);
    private static long LongValue(RegistryKey key, string name) => Convert.ToInt64(key.GetValue(name) ?? 0);
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizePath(string? value)
    {
        value = NullIfBlank(value)?.Trim('"');
        try { return value is null ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(value)); }
        catch { return value; }
    }

    private static string? NormalizeIconPath(string? value)
    {
        value = NullIfBlank(value);
        if (value is null) return null;
        var comma = value.LastIndexOf(',');
        if (comma > 1 && int.TryParse(value[(comma + 1)..], out _)) value = value[..comma];
        return NormalizePath(value);
    }

    private static string? ExistingPath(string? path) => path is not null && File.Exists(path) ? path : null;

    private static IReadOnlyDictionary<string, DateTimeOffset> ReadUserExecutionHistory()
    {
        var history = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\UserAssist");
            if (root is null) return history;
            foreach (var groupName in root.GetSubKeyNames())
            {
                using var count = root.OpenSubKey($@"{groupName}\Count");
                if (count is null) continue;
                foreach (var encodedName in count.GetValueNames())
                {
                    if (count.GetValue(encodedName) is not byte[] data || data.Length < 68) continue;
                    var fileTime = BitConverter.ToInt64(data, 60);
                    DateTimeOffset used;
                    try { used = DateTimeOffset.FromFileTime(fileTime); }
                    catch { continue; }
                    if (used.Year < 2000 || used > DateTimeOffset.Now.AddDays(1)) continue;
                    var decoded = Rot13(encodedName);
                    AddLatest(history, decoded, used);
                    var fileName = Path.GetFileName(decoded.Replace('/', Path.DirectorySeparatorChar));
                    if (!string.IsNullOrWhiteSpace(fileName))
                    {
                        AddLatest(history, fileName, used);
                        var normalized = NormalizeToken(Path.GetFileNameWithoutExtension(fileName));
                        if (normalized.Length >= 4) AddLatest(history, $"~{normalized}", used);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        return history;
    }

    private static DateTimeOffset? MatchLastUsed(string appName, string? iconPath,
        IReadOnlyDictionary<string, DateTimeOffset> history)
    {
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            if (history.TryGetValue(iconPath, out var exact)) return exact;
            var fileName = Path.GetFileName(iconPath);
            if (!string.IsNullOrWhiteSpace(fileName) && history.TryGetValue(fileName, out var byFile)) return byFile;
        }
        var normalizedName = NormalizeToken(appName);
        DateTimeOffset? latest = null;
        foreach (var entry in history)
        {
            if (entry.Key.Length < 5 || entry.Key[0] != '~') continue;
            var executable = entry.Key.AsSpan(1);
            if (normalizedName.AsSpan().Contains(executable, StringComparison.Ordinal) ||
                executable.Contains(normalizedName.AsSpan(), StringComparison.Ordinal))
                latest = latest is null || entry.Value > latest ? entry.Value : latest;
        }
        return latest;
    }

    private static void AddLatest(Dictionary<string, DateTimeOffset> history, string key, DateTimeOffset value)
    {
        if (!history.TryGetValue(key, out var existing) || value > existing) history[key] = value;
    }

    private static string NormalizeToken(string value) => new(value.Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant).ToArray());

    private static string Rot13(string value) => new(value.Select(character => character switch
    {
        >= 'a' and <= 'm' or >= 'A' and <= 'M' => (char)(character + 13),
        >= 'n' and <= 'z' or >= 'N' and <= 'Z' => (char)(character - 13),
        _ => character
    }).ToArray());

    private static string? ExecutableFromCommand(string? command)
    {
        var parts = SplitCommandLine(command);
        return parts.Count == 0 ? null : NormalizePath(parts[0]);
    }

    private static bool IsWithin(string candidate, string parent)
    {
        try
        {
            var fullCandidate = Path.GetFullPath(candidate);
            var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullCandidate.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static IReadOnlyList<string> SplitCommandLine(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return [];
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == nint.Zero) return [];
        try
        {
            var result = new string[count];
            for (var index = 0; index < count; index++)
                result[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * nint.Size)) ?? string.Empty;
            return result;
        }
        finally { LocalFree(pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint processHandle, uint flags,
        StringBuilder executablePath, ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
