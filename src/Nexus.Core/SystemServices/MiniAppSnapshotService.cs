using System.IO.Compression;

namespace Nexus.Core.SystemServices;

public sealed class MiniAppSnapshotService
{
    private const string MetadataDirectory = ".dagnue";

    public async Task<string> CaptureAsync(string workspaceDirectory, string commandId, string phase,
        CancellationToken cancellationToken = default)
    {
        var root = ValidateRoot(workspaceDirectory);
        Directory.CreateDirectory(root);
        var history = Path.Combine(root, MetadataDirectory, "history", commandId);
        Directory.CreateDirectory(history);
        var destination = Path.Combine(history, $"{phase}.zip");
        if (File.Exists(destination)) File.Delete(destination);

        await using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file);
            if (relative.Equals(MetadataDirectory, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(MetadataDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var entry = archive.CreateEntry(relative, CompressionLevel.Fastest);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                81920, FileOptions.Asynchronous);
            await using var output = entry.Open();
            await input.CopyToAsync(output, cancellationToken);
        }
        return destination;
    }

    public async Task RestoreAsync(string workspaceDirectory, string snapshotPath,
        CancellationToken cancellationToken = default)
    {
        var root = ValidateRoot(workspaceDirectory);
        var snapshot = Path.GetFullPath(snapshotPath);
        var metadataRoot = Path.Combine(root, MetadataDirectory) + Path.DirectorySeparatorChar;
        if (!snapshot.StartsWith(metadataRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(snapshot))
            throw new InvalidOperationException("The undo snapshot is outside this mini-system workspace.");

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)) File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(directory).Equals(MetadataDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.Delete(directory, recursive: true);
        }

        using var archive = ZipFile.OpenRead(snapshot);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Snapshot contains an invalid path.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static string ValidateRoot(string workspaceDirectory)
    {
        var root = Path.GetFullPath(workspaceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var allowed = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NEXUS", "task-workspaces")) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mini-system history is restricted to DAGNUE task workspaces.");
        return root;
    }
}
