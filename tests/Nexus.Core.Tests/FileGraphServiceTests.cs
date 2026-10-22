using Nexus.Core.Models;
using Nexus.Core.SystemServices;
using System.Numerics;

namespace Nexus.Core.Tests;

public sealed class FileGraphServiceTests
{
    [Fact]
    public void HierarchicalLayout_KeepsFilesCloserThanDirectoriesAndSeparatesDrives()
    {
        var nodes = new[]
        {
            new FileGraphNode(0, null, "C", "C:\\", true, 0, 2, 0),
            new FileGraphNode(1, null, "D", "D:\\", true, 0, 1, 0),
            new FileGraphNode(2, 0, "docs", "C:\\docs", true, 1, 2, 0),
            new FileGraphNode(3, 0, "media", "C:\\media", true, 1, 0, 0),
            new FileGraphNode(4, 2, "one.pdf", "C:\\docs\\one.pdf", false, 2, 0, 12),
            new FileGraphNode(5, 2, "two.pdf", "C:\\docs\\two.pdf", false, 2, 0, 12),
            new FileGraphNode(6, 1, "backup", "D:\\backup", true, 1, 0, 0)
        };
        var snapshot = new FileGraphSnapshot(nodes, 2, 5, 2, 7, false, DateTimeOffset.Now);

        var positions = HierarchicalFileGraphLayout.Build(snapshot);

        Assert.True(Vector3.Distance(positions[0], positions[1]) > 0.45f);
        Assert.True(Vector3.Distance(positions[4], positions[2]) <
                    Vector3.Distance(positions[2], positions[0]));
        Assert.True(Vector3.Distance(positions[5], positions[2]) <
                    Vector3.Distance(positions[3], positions[0]));
        Assert.NotEqual(positions[2], positions[3]);
    }

    [Fact]
    public void HierarchicalLayout_IsDeterministicAndBounded()
    {
        var nodes = new[]
        {
            new FileGraphNode(0, null, "root", "R", true, 0, 2, 0),
            new FileGraphNode(1, 0, "a", "R/a", true, 1, 1, 0),
            new FileGraphNode(2, 1, "b", "R/a/b", true, 2, 1, 0),
            new FileGraphNode(3, 2, "c", "R/a/b/c", false, 3, 0, 10)
        };
        var snapshot = new FileGraphSnapshot(nodes, 1, 3, 1, 4, false, DateTimeOffset.Now);

        var first = HierarchicalFileGraphLayout.Build(snapshot);
        var second = HierarchicalFileGraphLayout.Build(snapshot);

        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.All(first, position => Assert.True(position.Length() <= 0.921f));
    }

    [Fact]
    public async Task BuildAsync_ReportsLiveMappingBatchesAndCompletesAtOneHundredPercent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dagnue-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < 900; index++)
                await File.WriteAllTextAsync(Path.Combine(root, $"mapped-{index:0000}.dat"), string.Empty);

            var reports = new List<FileGraphProgress>();
            var progress = new InlineProgress<FileGraphProgress>(reports.Add);
            var snapshot = await new FileGraphService().BuildAsync([root], progress: progress);

            Assert.True(reports.Count >= 2);
            Assert.Equal(snapshot.Nodes.Count, reports.SelectMany(report => report.AddedNodes).Count());
            Assert.Equal(snapshot.FileCount, reports[^1].MappedFileCount);
            Assert.Equal(1d, reports[^1].EstimatedCompletion);
            Assert.True(reports[^1].IsComplete);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BuildAsync_DefaultModeIndexesBeyondFormerVisualizationLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dagnue-full-graph-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < 780; index++)
                await File.WriteAllTextAsync(Path.Combine(root, $"signal-{index:0000}.dat"), string.Empty);

            var snapshot = await new FileGraphService().BuildAsync([root]);

            Assert.Equal(780, snapshot.FileCount);
            Assert.Equal(1, snapshot.DirectoryCount);
            Assert.Equal(781, snapshot.Nodes.Count);
            Assert.False(snapshot.IsTruncated);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BuildAsync_MapsDirectoriesAndFilesToParentLinkedNodes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dagnue-graph-{Guid.NewGuid():N}");
        var cluster = Path.Combine(root, "cluster");
        Directory.CreateDirectory(cluster);
        await File.WriteAllTextAsync(Path.Combine(root, "root.dat"), "root");
        await File.WriteAllTextAsync(Path.Combine(cluster, "alpha.dat"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(cluster, "beta.dat"), "beta");

        try
        {
            var snapshot = await new FileGraphService().BuildAsync([root], 100);

            Assert.Equal(3, snapshot.FileCount);
            Assert.Equal(2, snapshot.DirectoryCount);
            Assert.Equal(1, snapshot.RootCount);
            Assert.All(snapshot.Nodes.Where(node => node.ParentId is not null),
                node => Assert.Contains(snapshot.Nodes, parent => parent.Id == node.ParentId && parent.IsDirectory));
            Assert.Contains(snapshot.Nodes, node => node.Name == "cluster" && node.IsDirectory && node.ChildCount == 2);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PersistentCache_LoadsCompletedMapWithoutScanningAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dagnue-cache-root-{Guid.NewGuid():N}");
        var cache = Path.Combine(Path.GetTempPath(), $"dagnue-cache-{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "remembered.txt"), "saved");
        try
        {
            var writer = new FileGraphService(cache);
            var mapped = await writer.BuildAsync([root]);
            Directory.Delete(root, true);

            var reader = new FileGraphService(cache);
            var loaded = await reader.LoadCachedAsync();

            Assert.NotNull(loaded);
            Assert.Equal(mapped.FileCount, loaded.FileCount);
            Assert.Contains(loaded.Nodes, node => node.Name == "remembered.txt");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (File.Exists(cache)) File.Delete(cache);
            if (File.Exists(cache + ".delta")) File.Delete(cache + ".delta");
        }
    }

    [Fact]
    public async Task LoadBaselineAsync_BoundedProjectionPreservesCompleteCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dagnue-bounded-root-{Guid.NewGuid():N}");
        var cache = Path.Combine(Path.GetTempPath(), $"dagnue-bounded-cache-{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < 20; index++)
                await File.WriteAllTextAsync(Path.Combine(root, $"remembered-{index:00}.txt"), "saved");

            var writer = new FileGraphService(cache);
            var complete = await writer.BuildAsync([root]);
            var cacheLength = new FileInfo(cache).Length;

            var reader = new FileGraphService(cache);
            var bounded = await reader.LoadBaselineAsync(5);

            Assert.NotNull(bounded);
            Assert.Equal(5, bounded.Nodes.Count);
            Assert.True(bounded.IsTruncated);
            Assert.True(reader.CurrentIsBoundedProjection);
            Assert.True(complete.Nodes.Count > bounded.Nodes.Count);
            Assert.All(bounded.Nodes.Where(node => node.ParentId is not null),
                node => Assert.InRange(node.ParentId!.Value, 0, node.Id - 1));
            Assert.Equal(cacheLength, new FileInfo(cache).Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (File.Exists(cache)) File.Delete(cache);
            if (File.Exists(cache + ".delta")) File.Delete(cache + ".delta");
        }
    }

    [Fact]
    public async Task BoundedProjection_DoesNotReplayOrAppendPersistentDeltaJournal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dagnue-projection-root-{Guid.NewGuid():N}");
        var cache = Path.Combine(Path.GetTempPath(), $"dagnue-projection-cache-{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "existing.txt"), "existing");
            await new FileGraphService(cache).BuildAsync([root]);
            var service = new FileGraphService(cache);
            var projected = await service.LoadBaselineAsync(1);
            Assert.NotNull(projected);
            Assert.True(service.CurrentIsBoundedProjection);

            var added = Path.Combine(root, "added.txt");
            await File.WriteAllTextAsync(added, "new");
            await service.ApplyChangesAsync([
                new FileActivity(FileActivityKind.Created, added, null, false, DateTimeOffset.UtcNow, "TEST")
            ]);
            var pending = await service.ApplyPendingDeltasAsync();

            Assert.Equal(0, pending.DeltaCount);
            Assert.False(File.Exists(cache + ".delta"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (File.Exists(cache)) File.Delete(cache);
            if (File.Exists(cache + ".delta")) File.Delete(cache + ".delta");
        }
    }

    [Fact]
    public async Task ApplyChanges_UpdatesOnlyDeltaAndPersistsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dagnue-delta-root-{Guid.NewGuid():N}");
        var cache = Path.Combine(Path.GetTempPath(), $"dagnue-delta-{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(root);
        var removedPath = Path.Combine(root, "removed.txt");
        var addedPath = Path.Combine(root, "added.txt");
        await File.WriteAllTextAsync(removedPath, "old");
        try
        {
            var service = new FileGraphService(cache);
            await service.BuildAsync([root]);
            File.Delete(removedPath);
            await File.WriteAllTextAsync(addedPath, "new");

            var updated = await service.ApplyChangesAsync([
                new FileActivity(FileActivityKind.Deleted, removedPath, null, false,
                    DateTimeOffset.UtcNow, "TEST"),
                new FileActivity(FileActivityKind.Created, addedPath, null, false,
                    DateTimeOffset.UtcNow, "TEST")
            ]);

            Assert.DoesNotContain(updated.Nodes, node => node.Path == removedPath);
            Assert.Contains(updated.Nodes, node => node.Path == addedPath);
            var reloaded = await new FileGraphService(cache).LoadCachedAsync();
            Assert.NotNull(reloaded);
            Assert.Contains(reloaded.Nodes, node => node.Path == addedPath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (File.Exists(cache)) File.Delete(cache);
            if (File.Exists(cache + ".delta")) File.Delete(cache + ".delta");
        }
    }

    [Fact]
    public async Task ApplyChanges_NeverRemovesVolumeRootFromMalformedRenameSignal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dagnue-root-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var original = Path.Combine(root, "original.txt");
        var replacement = Path.Combine(root, "replacement.txt");
        await File.WriteAllTextAsync(original, "original");
        await File.WriteAllTextAsync(replacement, "replacement");
        try
        {
            var service = new FileGraphService();
            await service.BuildAsync([root]);
            var updated = await service.ApplyChangesAsync([
                new FileActivity(FileActivityKind.Moved, replacement, root, false,
                    DateTimeOffset.UtcNow, "TEST")
            ]);

            Assert.Contains(updated.Nodes, node => node.Path == root && node.ParentId is null);
            Assert.Contains(updated.Nodes, node => node.Path == original);
            Assert.Contains(updated.Nodes, node => node.Path == replacement);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ApplyChanges_DriveAttachIndexesOnlyTheNewTree_AndDetachRemovesIt()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), $"dagnue-drive-a-{Guid.NewGuid():N}");
        var attachedRoot = Path.Combine(Path.GetTempPath(), $"dagnue-drive-b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(Path.Combine(attachedRoot, "folder"));
        var attachedFile = Path.Combine(attachedRoot, "folder", "attached.txt");
        await File.WriteAllTextAsync(attachedFile, "attached");
        try
        {
            var service = new FileGraphService();
            await service.BuildAsync([firstRoot]);
            var attached = await service.ApplyChangesAsync([
                new FileActivity(FileActivityKind.Created, attachedRoot, null, true,
                    DateTimeOffset.UtcNow, "DRIVE_ATTACH")
            ]);
            Assert.Contains(attached.Nodes, node => node.Path == attachedFile);

            var detached = await service.ApplyChangesAsync([
                new FileActivity(FileActivityKind.Deleted, attachedRoot, null, true,
                    DateTimeOffset.UtcNow, "DRIVE_DETACH")
            ]);
            Assert.DoesNotContain(detached.Nodes, node => node.Path == attachedRoot ||
                node.Path.StartsWith(attachedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(firstRoot)) Directory.Delete(firstRoot, true);
            if (Directory.Exists(attachedRoot)) Directory.Delete(attachedRoot, true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
