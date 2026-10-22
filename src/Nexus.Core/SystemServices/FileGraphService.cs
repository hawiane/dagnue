using System.Diagnostics;
using Nexus.Core.Models;

namespace Nexus.Core.SystemServices;

public sealed class FileGraphService
{
    private const int CacheVersion = 1;
    private const string CacheMagic = "NEXUS-FILE-GRAPH";
    private const string DeltaMagic = "NEXUS-FILE-DELTAS";
    private readonly string? _cachePath;
    private readonly string? _deltaPath;
    private readonly SemaphoreSlim _updateGate = new(1, 1);

    public FileGraphService(string? cachePath = null)
    {
        _cachePath = string.IsNullOrWhiteSpace(cachePath) ? null : Path.GetFullPath(cachePath);
        _deltaPath = _cachePath is null ? null : _cachePath + ".delta";
    }

    public FileGraphSnapshot Current { get; private set; } = FileGraphSnapshot.Empty;
    public bool CurrentIsBoundedProjection { get; private set; }

    public bool HasPersistentCache => _cachePath is not null && File.Exists(_cachePath);

    public Task<FileGraphSnapshot?> LoadBaselineAsync(CancellationToken cancellationToken = default) =>
        LoadBaselineAsync(0, cancellationToken);

    public async Task<FileGraphSnapshot?> LoadBaselineAsync(int maxNodes,
        CancellationToken cancellationToken = default)
    {
        if (_cachePath is null || !File.Exists(_cachePath)) return null;
        var nodeLimit = maxNodes <= 0 ? int.MaxValue : Math.Max(1, maxNodes);
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await Task.Run(() =>
                ReadCache(_cachePath, nodeLimit, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            Current = loaded.Snapshot;
            CurrentIsBoundedProjection = loaded.IsBoundedProjection;
            return loaded.Snapshot;
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<FileGraphSnapshot?> LoadCachedAsync(CancellationToken cancellationToken = default)
    {
        var baseline = await LoadBaselineAsync(cancellationToken).ConfigureAwait(false);
        if (baseline is null) return null;
        var pending = await ApplyPendingDeltasAsync(cancellationToken).ConfigureAwait(false);
        return pending.Snapshot;
    }

    public Task<(FileGraphSnapshot Snapshot, int DeltaCount)> ApplyPendingDeltasAsync(
        CancellationToken cancellationToken = default) =>
        ApplyPendingDeltasAsync(persistBaseline: true, cancellationToken);

    public async Task<(FileGraphSnapshot Snapshot, int DeltaCount)> ApplyPendingDeltasAsync(
        bool persistBaseline, CancellationToken cancellationToken = default)
    {
        // A visualization projection represents only the leading parent-safe part
        // of the full index. Replaying a potentially multi-gigabyte historical
        // journal into it is both incomplete and capable of stalling the desktop.
        if (CurrentIsBoundedProjection) return (Current, 0);
        if (_deltaPath is null || !File.Exists(_deltaPath) || Current.Nodes.Count == 0)
            return (Current, 0);

        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<FileActivity> deltas;
            try
            {
                deltas = await Task.Run(() => ReadDeltaJournal(cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                QuarantineDeltaJournal();
                return (Current, 0);
            }
            catch (EndOfStreamException)
            {
                QuarantineDeltaJournal();
                return (Current, 0);
            }

            if (deltas.Count == 0) return (Current, 0);
            var compacted = CompactDeltaChanges(deltas);
            var updated = await Task.Run(() => ApplyChanges(Current, compacted, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            Current = updated;

            // A bounded visualization may replay deltas into its in-memory projection,
            // but it must never replace the complete persistent baseline with that
            // projection. Full loads continue to compact and consume the journal.
            if (persistBaseline && !CurrentIsBoundedProjection)
                await WriteCacheAsync(updated, cancellationToken).ConfigureAwait(false);
            return (updated, deltas.Count);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private static IReadOnlyList<FileActivity> CompactDeltaChanges(IReadOnlyList<FileActivity> changes)
    {
        var result = new List<FileActivity>();
        var latestNonStructural = new Dictionary<string, FileActivity>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (change.Kind is FileActivityKind.Moved or FileActivityKind.Deleted)
                result.Add(change);
            else
                latestNonStructural[change.Path] = change;
        }
        result.AddRange(latestNonStructural.Values.OrderBy(change => change.Timestamp));
        return result;
    }

    private void QuarantineDeltaJournal()
    {
        if (_deltaPath is null || !File.Exists(_deltaPath)) return;
        var quarantinePath = _deltaPath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        try { File.Move(_deltaPath, quarantinePath, overwrite: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task<FileGraphSnapshot> BuildAsync(
        IEnumerable<string>? roots = null,
        int maxNodes = 0,
        IProgress<FileGraphProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool persistLimitedSnapshot = false)
    {
        var resolvedRoots = ResolveRoots(roots);
        var nodeLimit = maxNodes <= 0 ? int.MaxValue : Math.Max(1, maxNodes);
        var snapshot = await Task.Run(
            () => Build(resolvedRoots, nodeLimit, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        Current = snapshot;
        CurrentIsBoundedProjection = false;
        if (_cachePath is not null && (maxNodes <= 0 || persistLimitedSnapshot))
            await SaveCacheAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<FileGraphSnapshot> ApplyChangesAsync(IEnumerable<FileActivity> changes,
        CancellationToken cancellationToken = default)
    {
        var batch = CompactDeltaChanges(changes.Where(change => change.Kind != FileActivityKind.Accessed).ToArray());
        if (batch.Count == 0 || Current.Nodes.Count == 0) return Current;

        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = await Task.Run(() => ApplyChanges(Current, batch, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            Current = updated;
            if (_cachePath is not null && !CurrentIsBoundedProjection)
                await AppendDeltaJournalAsync(batch, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private async Task SaveCacheAsync(FileGraphSnapshot snapshot, CancellationToken cancellationToken)
    {
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteCacheAsync(snapshot, cancellationToken).ConfigureAwait(false); }
        finally { _updateGate.Release(); }
    }

    private async Task WriteCacheAsync(FileGraphSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (_cachePath is null) return;
        var directory = Path.GetDirectoryName(_cachePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = _cachePath + ".new";
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.SequentialScan);
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: false);
            writer.Write(CacheMagic);
            writer.Write(CacheVersion);
            writer.Write(snapshot.IndexedAt.UtcTicks);
            writer.Write(snapshot.IsTruncated);
            writer.Write(snapshot.Nodes.Count);
            foreach (var node in snapshot.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(node.ParentId ?? -1);
                writer.Write(node.Name);
                writer.Write(node.Path);
                writer.Write(node.IsDirectory);
                writer.Write(node.Depth);
                writer.Write(node.ChildCount);
                writer.Write(node.SizeBytes);
            }
        }, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, _cachePath, overwrite: true);
        if (_deltaPath is not null && File.Exists(_deltaPath)) File.Delete(_deltaPath);
    }

    private async Task AppendDeltaJournalAsync(IReadOnlyList<FileActivity> changes,
        CancellationToken cancellationToken)
    {
        if (_deltaPath is null || changes.Count == 0) return;
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var createHeader = !File.Exists(_deltaPath) || new FileInfo(_deltaPath).Length == 0;
            using var stream = new FileStream(_deltaPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: false);
            if (createHeader)
            {
                writer.Write(DeltaMagic);
                writer.Write(CacheVersion);
            }
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write((int)change.Kind);
                writer.Write(change.Path);
                writer.Write(change.PreviousPath is not null);
                if (change.PreviousPath is not null) writer.Write(change.PreviousPath);
                writer.Write(change.IsDirectory);
                writer.Write(change.Timestamp.UtcTicks);
                writer.Write(change.Source);
                writer.Write(change.SizeBytes);
                writer.Write(change.SignalCount);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<FileActivity> ReadDeltaJournal(CancellationToken token)
    {
        if (_deltaPath is null || !File.Exists(_deltaPath)) return [];
        using var stream = new FileStream(_deltaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);
        if (reader.ReadString() != DeltaMagic || reader.ReadInt32() != CacheVersion)
            throw new InvalidDataException("Unsupported filesystem delta journal.");
        var changes = new List<FileActivity>();
        while (stream.Position < stream.Length)
        {
            token.ThrowIfCancellationRequested();
            var kind = (FileActivityKind)reader.ReadInt32();
            var path = reader.ReadString();
            var previousPath = reader.ReadBoolean() ? reader.ReadString() : null;
            var isDirectory = reader.ReadBoolean();
            var timestamp = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var source = reader.ReadString();
            var size = reader.ReadInt64();
            var signalCount = reader.ReadInt32();
            changes.Add(new FileActivity(kind, path, previousPath, isDirectory, timestamp, source, size,
                signalCount));
        }
        return changes;
    }

    private static (FileGraphSnapshot Snapshot, bool IsBoundedProjection) ReadCache(
        string cachePath, int maxNodes, CancellationToken token)
    {
        using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);
        if (reader.ReadString() != CacheMagic || reader.ReadInt32() != CacheVersion)
            throw new InvalidDataException("Unsupported filesystem index cache.");
        var indexedAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var cacheWasTruncated = reader.ReadBoolean();
        var totalCount = reader.ReadInt32();
        if (totalCount < 0 || totalCount > 10_000_000)
            throw new InvalidDataException("Invalid filesystem cache size.");
        var loadCount = Math.Min(totalCount, maxNodes);
        var nodes = new List<FileGraphNode>(loadCount);
        for (var id = 0; id < loadCount; id++)
        {
            token.ThrowIfCancellationRequested();
            var parent = reader.ReadInt32();
            nodes.Add(new FileGraphNode(id, parent < 0 ? null : parent, reader.ReadString(), reader.ReadString(),
                reader.ReadBoolean(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt64()));
        }
        if (loadCount == totalCount && stream.Position != stream.Length)
            throw new InvalidDataException("Filesystem cache has trailing data.");
        var isBoundedProjection = loadCount < totalCount;
        return (CreateSnapshot(nodes, cacheWasTruncated || isBoundedProjection, indexedAt), isBoundedProjection);
    }

    private static FileGraphSnapshot ApplyChanges(FileGraphSnapshot snapshot,
        IReadOnlyList<FileActivity> changes, CancellationToken token)
    {
        var entries = new Dictionary<string, IndexedEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in snapshot.Nodes)
        {
            var parentPath = node.ParentId is { } parentId && parentId >= 0 && parentId < snapshot.Nodes.Count
                ? snapshot.Nodes[parentId].Path
                : null;
            entries[node.Path] = new IndexedEntry(node.Name, node.Path, parentPath, node.IsDirectory, node.SizeBytes);
        }

        void RemovePath(string path, bool recursive, bool allowVolumeRoot = false)
        {
            // FileSystemWatcher can occasionally report a drive root as the old
            // name of an unrelated atomic replace. A delta must never erase an
            // indexed volume root; only a deliberate baseline rebuild may do that.
            if (!allowVolumeRoot && entries.TryGetValue(path, out var protectedEntry) && protectedEntry.ParentPath is null)
                return;
            var pathRoot = Path.GetPathRoot(path);
            if (!allowVolumeRoot && !string.IsNullOrWhiteSpace(pathRoot) &&
                path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Equals(pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                return;
            if (!recursive)
            {
                entries.Remove(path);
                return;
            }
            var prefix = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            foreach (var key in entries.Keys.Where(key => key.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                entries.Remove(key);
        }

        void AddExistingPath(string path, bool indexEntireTree = false)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                var parentPath = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(parentPath) || !entries.ContainsKey(parentPath)) return;
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }
                entries[path] = new IndexedEntry(Path.GetFileName(path), path, parentPath, false, size);
                return;
            }
            if (!Directory.Exists(path)) return;

            // Watchers frequently emit Modified/Created echoes for directories that
            // are already indexed. Recursively rebuilding each of those subtrees can
            // turn a small delta journal into minutes of startup work. Child create,
            // move and delete events update the actual structural entries separately.
            if (entries.ContainsKey(path)) return;

            var containingDirectory = Path.GetDirectoryName(path);
            if (indexEntireTree)
            {
                var addition = Build([path], int.MaxValue, null, token);
                foreach (var node in addition.Nodes)
                {
                    var parentPath = node.ParentId is { } parentId
                        ? addition.Nodes[parentId].Path
                        : containingDirectory;
                    entries[node.Path] = new IndexedEntry(node.Name, node.Path, parentPath,
                        node.IsDirectory, node.SizeBytes);
                }
                return;
            }

            if (!string.IsNullOrWhiteSpace(containingDirectory) && !entries.ContainsKey(containingDirectory))
                return;

            // The watcher journal already contains the descendants as individual
            // events. Adding only this directory prevents a copied tree from being
            // recursively scanned again for every parent event during replay.
            entries[path] = new IndexedEntry(Path.GetFileName(path), path,
                containingDirectory, true, 0);
        }

        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested();
            if (change.Kind == FileActivityKind.Moved && !string.IsNullOrWhiteSpace(change.PreviousPath))
                RemovePath(change.PreviousPath, change.IsDirectory);
            if (change.Kind == FileActivityKind.Deleted)
                RemovePath(change.Path, change.IsDirectory,
                    change.Source.Equals("DRIVE_DETACH", StringComparison.OrdinalIgnoreCase));
            else if (change.Kind == FileActivityKind.Modified &&
                     entries.TryGetValue(change.Path, out var existing))
            {
                // Modification signals dominate real journals and do not change
                // topology. Update metadata in memory instead of issuing a disk
                // existence/stat call for every watcher echo during startup replay.
                if (!existing.IsDirectory && change.SizeBytes > 0)
                    entries[change.Path] = existing with { SizeBytes = change.SizeBytes };
            }
            else
                AddExistingPath(change.Path,
                    change.Source.Equals("DRIVE_ATTACH", StringComparison.OrdinalIgnoreCase));
        }

        return RebuildSnapshot(entries.Values, snapshot.IsTruncated, DateTimeOffset.UtcNow, token);
    }

    private static FileGraphSnapshot RebuildSnapshot(IEnumerable<IndexedEntry> source, bool truncated,
        DateTimeOffset indexedAt, CancellationToken token)
    {
        var entries = source.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var children = entries.Values.GroupBy(entry => entry.ParentPath ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var roots = entries.Values.Where(entry => entry.ParentPath is null || !entries.ContainsKey(entry.ParentPath))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var nodes = new List<FileGraphNode>(entries.Count);
        var queue = new Queue<(IndexedEntry Entry, int? ParentId, int Depth)>();
        foreach (var root in roots) queue.Enqueue((root, null, 0));
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (entry, parentId, depth) = queue.Dequeue();
            var id = nodes.Count;
            var directChildren = children.GetValueOrDefault(entry.Path) ?? [];
            nodes.Add(new FileGraphNode(id, parentId, entry.Name, entry.Path, entry.IsDirectory, depth,
                directChildren.Length, entry.SizeBytes));
            foreach (var child in directChildren) queue.Enqueue((child, id, depth + 1));
        }
        return CreateSnapshot(nodes, truncated, indexedAt);
    }

    private static FileGraphSnapshot CreateSnapshot(IReadOnlyList<FileGraphNode> nodes, bool truncated,
        DateTimeOffset indexedAt) => new(
        nodes,
        nodes.Count(node => !node.IsDirectory),
        nodes.Count(node => node.IsDirectory),
        nodes.Count(node => node.ParentId is null),
        nodes.Count,
        truncated,
        indexedAt);

    private static FileGraphSnapshot Build(IReadOnlyList<string> roots, int maxNodes,
        IProgress<FileGraphProgress>? progress, CancellationToken token)
    {
        var initialCapacity = maxNodes == int.MaxValue ? 16_384 : Math.Min(maxNodes, 16_384);
        var nodes = new List<FileGraphNode>(initialCapacity);
        var pending = new Queue<PendingDirectory>();
        var discovered = 0;
        var truncated = false;
        var fileCount = 0;
        var directoryCount = 0;
        var processedDirectoryCount = 0;
        var estimatedCompletion = 0d;
        var progressBatch = progress is null ? null : new List<FileGraphNode>(1024);
        var progressClock = Stopwatch.StartNew();
        var currentPath = string.Empty;

        void Track(FileGraphNode node)
        {
            nodes.Add(node);
            if (node.IsDirectory) directoryCount++;
            else fileCount++;
            progressBatch?.Add(node);
        }

        void ReportProgress(bool complete = false)
        {
            if (progress is null) return;
            var frontierTotal = processedDirectoryCount + pending.Count;
            var rawCompletion = frontierTotal == 0 ? 0d : (double)processedDirectoryCount / frontierTotal;
            estimatedCompletion = complete
                ? 1d
                : Math.Min(0.97d, Math.Max(estimatedCompletion, rawCompletion * 0.94d));
            var addedNodes = progressBatch is { Count: > 0 } ? progressBatch.ToArray() : [];
            progressBatch?.Clear();
            progress.Report(new FileGraphProgress(
                addedNodes,
                fileCount,
                directoryCount,
                roots.Count,
                processedDirectoryCount,
                pending.Count,
                estimatedCompletion,
                currentPath,
                complete));
            progressClock.Restart();
        }

        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (nodes.Count >= maxNodes) { truncated = true; break; }
            var name = new DirectoryInfo(root).Name;
            if (string.IsNullOrWhiteSpace(name)) name = root;
            var id = nodes.Count;
            Track(new FileGraphNode(id, null, name.ToUpperInvariant(), root, true, 0, 0, 0));
            pending.Enqueue(new PendingDirectory(root, id, 0));
        }

        ReportProgress();

        while (pending.Count > 0 && nodes.Count < maxNodes)
        {
            token.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            currentPath = current.Path;
            var entries = ReadEntries(current.Path, token);
            discovered += entries.Count;

            var currentNode = nodes[current.NodeId];
            nodes[current.NodeId] = currentNode with { ChildCount = entries.Count };

            var directories = entries.Where(entry => entry.IsDirectory).ToArray();
            var files = entries.Where(entry => !entry.IsDirectory).ToArray();

            foreach (var directory in directories)
            {
                if (nodes.Count >= maxNodes) { truncated = true; break; }
                var id = nodes.Count;
                Track(new FileGraphNode(id, current.NodeId, directory.Name, directory.Path, true,
                    current.Depth + 1, directory.ChildCount, 0));
                pending.Enqueue(new PendingDirectory(directory.Path, id, current.Depth + 1));
                if (progressBatch is { Count: >= 750 }) ReportProgress();
            }

            if (nodes.Count >= maxNodes) break;

            foreach (var file in files)
            {
                if (nodes.Count >= maxNodes) { truncated = true; break; }
                var id = nodes.Count;
                Track(new FileGraphNode(id, current.NodeId, file.Name, file.Path, false,
                    current.Depth + 1, 0, file.SizeBytes));
                if (progressBatch is { Count: >= 750 }) ReportProgress();
            }

            processedDirectoryCount++;
            if (progressBatch is { Count: >= 750 } || progressClock.Elapsed >= TimeSpan.FromMilliseconds(350))
                ReportProgress();

        }

        if (pending.Count > 0) truncated = true;
        ReportProgress(complete: true);
        return new FileGraphSnapshot(
            nodes,
            fileCount,
            directoryCount,
            nodes.Count(node => node.ParentId is null),
            discovered,
            truncated,
            DateTimeOffset.Now);
    }

    private static List<EntryInfo> ReadEntries(string path, CancellationToken token)
    {
        var entries = new List<EntryInfo>();
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false
            };
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(path, "*", options))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var attributes = File.GetAttributes(entryPath);
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    // Junctions and symbolic links are not followed because they can
                    // create cycles. The target itself is indexed through its real path.
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    var name = Path.GetFileName(entryPath);

                    long size = 0;
                    if (!isDirectory)
                    {
                        try { size = new FileInfo(entryPath).Length; } catch { }
                    }

                    entries.Add(new EntryInfo(entryPath, name, isDirectory, 0, size));
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                catch (System.Security.SecurityException) { }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (System.Security.SecurityException) { }

        return entries
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => StableHash(entry.Path))
            .ToList();
    }

    private static IReadOnlyList<string> ResolveRoots(IEnumerable<string>? roots)
    {
        var candidates = roots?.ToArray() ?? DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
            .Select(drive => drive.RootDirectory.FullName)
            .ToArray();

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 23;
            foreach (var character in value) hash = hash * 31 + char.ToUpperInvariant(character);
            return hash;
        }
    }

    private readonly record struct PendingDirectory(string Path, int NodeId, int Depth);
    private readonly record struct EntryInfo(string Path, string Name, bool IsDirectory, int ChildCount, long SizeBytes);
    private readonly record struct IndexedEntry(string Name, string Path, string? ParentPath,
        bool IsDirectory, long SizeBytes);
}
