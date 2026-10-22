using Nexus.Core.Models;
using Nexus.Core.SystemServices;

namespace Nexus.Core.Tests;

public sealed class IndexedFileSearchServiceTests
{
    private static readonly FileGraphSnapshot Snapshot = new(
    [
        new FileGraphNode(0, null, "Documents", @"C:\Documents", true, 0, 2, 0),
        new FileGraphNode(1, 0, "HAWARD HALEN DOMINCIL.docx", @"C:\Documents\HAWARD HALEN DOMINCIL.docx", false, 1, 0, 120),
        new FileGraphNode(2, 0, "Other report.pdf", @"C:\Documents\Other report.pdf", false, 1, 0, 900)
    ], 2, 1, 1, 3, false, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("HAW")]
    [InlineData("HAW OMIN")]
    [InlineData("OMINCIL .docx")]
    [InlineData("HAW .docx")]
    [InlineData("DOM HAW")]
    [InlineData("CIL LEN")]
    [InlineData("cil len")]
    public void Search_MatchesFilenameFragmentsInAnyOrder(string query)
    {
        var result = IndexedFileSearchService.Search(Snapshot, query);
        Assert.Equal("HAWARD HALEN DOMINCIL.docx", Assert.Single(result.Items).Name);
    }

    [Fact]
    public void Search_SortsLargestFilesFirst()
    {
        var result = IndexedFileSearchService.Search(Snapshot, string.Empty, FileSearchSort.Size);
        Assert.Equal("Other report.pdf", result.Items[0].Name);
    }

    [Fact]
    public void Search_ReportsEveryMatchWhileBoundingRenderedResults()
    {
        var result = IndexedFileSearchService.Search(Snapshot, string.Empty, maximumResults: 1);
        Assert.Equal(3, result.TotalMatches);
        Assert.Single(result.Items);
        Assert.True(result.IsLimited);
    }

    [Fact]
    public void Search_MatchesAndOpensDirectoryNamesAsFirstClassResults()
    {
        var result = IndexedFileSearchService.Search(Snapshot, "doc", kind: FileSearchKind.Directories);

        var directory = Assert.Single(result.Items);
        Assert.True(directory.IsDirectory);
        Assert.Equal("Documents", directory.Name);
    }

    [Fact]
    public void Search_KindFilterSeparatesFilesAndDirectories()
    {
        var files = IndexedFileSearchService.Search(Snapshot, string.Empty, kind: FileSearchKind.Files);
        var directories = IndexedFileSearchService.Search(Snapshot, string.Empty,
            kind: FileSearchKind.Directories);

        Assert.Equal(2, files.TotalMatches);
        Assert.All(files.Items, item => Assert.False(item.IsDirectory));
        Assert.Single(directories.Items);
        Assert.All(directories.Items, item => Assert.True(item.IsDirectory));
    }
}
