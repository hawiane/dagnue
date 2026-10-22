using Nexus.Core.SystemServices;

namespace Nexus.Core.Tests;

public sealed class MiniAppSnapshotServiceTests
{
    [Fact]
    public async Task Snapshot_RestoresWorkspaceFilesWithoutTouchingHistory()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NEXUS", "task-workspaces", $"snapshot-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "APP.md"), "before");
            var service = new MiniAppSnapshotService();
            var snapshot = await service.CaptureAsync(root, "command-1", "before");
            await File.WriteAllTextAsync(Path.Combine(root, "APP.md"), "after");
            await File.WriteAllTextAsync(Path.Combine(root, "new.txt"), "new");

            await service.RestoreAsync(root, snapshot);

            Assert.Equal("before", await File.ReadAllTextAsync(Path.Combine(root, "APP.md")));
            Assert.False(File.Exists(Path.Combine(root, "new.txt")));
            Assert.True(File.Exists(snapshot));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
