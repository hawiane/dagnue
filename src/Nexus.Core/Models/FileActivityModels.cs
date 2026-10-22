namespace Nexus.Core.Models;

public enum FileActivityKind
{
    Accessed,
    Modified,
    Created,
    Deleted,
    Moved
}

public sealed record FileActivity(
    FileActivityKind Kind,
    string Path,
    string? PreviousPath,
    bool IsDirectory,
    DateTimeOffset Timestamp,
    string Source,
    long SizeBytes = 0,
    int SignalCount = 1);

public sealed class FileActivityEventArgs(FileActivity activity) : EventArgs
{
    public FileActivity Activity { get; } = activity;
}
