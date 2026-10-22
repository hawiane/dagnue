using Nexus.Core.SystemServices;

namespace Nexus.Core.Tests;

public sealed class SystemOptimizationServiceTests
{
    [Fact]
    public async Task OptimizeDisk_RemovesOnlyStaleTempFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dagnue-optimization-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var stale = Path.Combine(root, "stale.tmp");
            var recent = Path.Combine(root, "recent.tmp");
            await File.WriteAllTextAsync(stale, "old temporary data");
            await File.WriteAllTextAsync(recent, "new temporary data");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

            var result = await new SystemOptimizationService().OptimizeDiskAsync(root);

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(recent));
            Assert.True(result.BytesFreed > 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
