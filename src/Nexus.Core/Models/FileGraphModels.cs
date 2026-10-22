namespace Nexus.Core.Models;

public sealed record FileGraphNode(
    int Id,
    int? ParentId,
    string Name,
    string Path,
    bool IsDirectory,
    int Depth,
    int ChildCount,
    long SizeBytes);

public sealed record FileGraphSnapshot(
    IReadOnlyList<FileGraphNode> Nodes,
    int FileCount,
    int DirectoryCount,
    int RootCount,
    int DiscoveredEntryCount,
    bool IsTruncated,
    DateTimeOffset IndexedAt)
{
    public static FileGraphSnapshot Empty { get; } = new(
        [], 0, 0, 0, 0, false, DateTimeOffset.MinValue);
}

public sealed record FileGraphProgress(
    IReadOnlyList<FileGraphNode> AddedNodes,
    int MappedFileCount,
    int MappedDirectoryCount,
    int RootCount,
    int ProcessedDirectoryCount,
    int PendingDirectoryCount,
    double EstimatedCompletion,
    string CurrentPath,
    bool IsComplete)
{
    public int MappedNodeCount => MappedFileCount + MappedDirectoryCount;

    public static FileGraphProgress Empty { get; } = new(
        [], 0, 0, 0, 0, 0, 0, string.Empty, false);
}
