using Nexus.Core.Models;
using Nexus.Core.SystemServices;

namespace Nexus.Core.Tests;

public sealed class FileActivityServiceTests
{
    [Fact]
    public void TrackApplicationAccess_PublishesExplicitDagnueAccess()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dagnue-activity-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[4096]);
        try
        {
            using var service = new FileActivityService();
            FileActivity? observed = null;
            service.ActivityDetected += (_, args) => observed = args.Activity;

            service.TrackApplicationAccess(path, source: "TEST");

            Assert.NotNull(observed);
            Assert.Equal(FileActivityKind.Accessed, observed.Kind);
            Assert.Equal(path, observed.Path);
            Assert.Equal("TEST", observed.Source);
            Assert.Equal(4096, observed.SizeBytes);
            Assert.Equal(1, observed.SignalCount);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
