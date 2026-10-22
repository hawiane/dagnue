namespace Nexus.Core.Models;

public sealed record RunningApplicationInfo(
    int ProcessId,
    string Name,
    string WindowTitle,
    string? ExecutablePath,
    long WorkingSetBytes,
    bool CanTerminate,
    string SafetyNote);

public sealed record InstalledApplicationInfo(
    string Id,
    string Name,
    string? Version,
    string? Publisher,
    long? SizeBytes,
    string? InstallLocation,
    string? IconPath,
    DateTimeOffset? LastUsedAt,
    string? UninstallCommand,
    bool CanUninstall,
    string SafetyNote);

public sealed record ApplicationActionResult(bool Succeeded, string Message);
