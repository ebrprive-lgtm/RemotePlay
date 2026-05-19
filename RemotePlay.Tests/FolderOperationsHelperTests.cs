using System.IO;
using Xunit;
using RemotePlay;

namespace RemotePlay.Tests;

public class FolderOperationsHelperTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public FolderOperationsHelperTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── CountFolderContents ──────────────────────────────────────────

    [Fact]
    public void CountFolderContents_EmptyFolder_ReturnsBothZero()
    {
        var folder = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(folder);

        var (total, links) = FolderOperationsHelper.CountFolderContents(folder);

        Assert.Equal(0, total);
        Assert.Equal(0, links);
    }

    [Fact]
    public void CountFolderContents_FolderWithOnlyNonLinkFiles_ReturnsZeroLinks()
    {
        var folder = Path.Combine(_tempDir, "videos");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "movie.mkv"), "data");
        File.WriteAllText(Path.Combine(folder, "movie2.mp4"), "data");

        var (total, links) = FolderOperationsHelper.CountFolderContents(folder);

        Assert.Equal(2, total);
        Assert.Equal(0, links);
    }

    [Fact]
    public void CountFolderContents_FolderWithMixedFiles_CountsLinksCorrectly()
    {
        var folder = Path.Combine(_tempDir, "mixed");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "movie.mkv"), "data");
        File.WriteAllText(Path.Combine(folder, "link1.rplink"), @"C:\videos\a.mkv");
        File.WriteAllText(Path.Combine(folder, "link2.rplink"), @"C:\videos\b.mkv");

        var (total, links) = FolderOperationsHelper.CountFolderContents(folder);

        Assert.Equal(3, total);
        Assert.Equal(2, links);
    }

    [Fact]
    public void CountFolderContents_RecursivelyCountsSubfolderFiles()
    {
        var folder = Path.Combine(_tempDir, "parent");
        var sub    = Path.Combine(folder, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(folder, "root.rplink"), @"C:\a.mkv");
        File.WriteAllText(Path.Combine(sub,    "nested.rplink"), @"C:\b.mkv");
        File.WriteAllText(Path.Combine(sub,    "nested.mkv"), "data");

        var (total, links) = FolderOperationsHelper.CountFolderContents(folder);

        Assert.Equal(3, total);
        Assert.Equal(2, links);
    }

    // ── FindLinksPointingIntoFolder ──────────────────────────────────

    [Fact]
    public void FindLinksPointingIntoFolder_NoLinksInLibrary_ReturnsEmpty()
    {
        var library = Path.Combine(_tempDir, "library");
        var source  = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(source);

        var result = FolderOperationsHelper.FindLinksPointingIntoFolder(library, source);

        Assert.Empty(result);
    }

    [Fact]
    public void FindLinksPointingIntoFolder_LinkTargetingSourceDir_IsIncluded()
    {
        var library = Path.Combine(_tempDir, "library");
        var source  = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(source);

        var linkPath = Path.Combine(library, "folder.rplink");
        File.WriteAllText(linkPath, source);

        var result = FolderOperationsHelper.FindLinksPointingIntoFolder(library, source);

        Assert.Single(result);
        Assert.Equal(linkPath, result[0]);
    }

    [Fact]
    public void FindLinksPointingIntoFolder_LinkTargetingFileInsideSource_IsIncluded()
    {
        var library = Path.Combine(_tempDir, "library");
        var source  = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(source);

        var linkPath = Path.Combine(library, "movie.rplink");
        File.WriteAllText(linkPath, Path.Combine(source, "movie.mkv"));

        var result = FolderOperationsHelper.FindLinksPointingIntoFolder(library, source);

        Assert.Single(result);
    }

    [Fact]
    public void FindLinksPointingIntoFolder_LinkTargetingUnrelatedPath_IsExcluded()
    {
        var library  = Path.Combine(_tempDir, "library");
        var source   = Path.Combine(_tempDir, "source");
        var unrelated = Path.Combine(_tempDir, "other");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(unrelated);

        File.WriteAllText(Path.Combine(library, "other.rplink"), Path.Combine(unrelated, "a.mkv"));

        var result = FolderOperationsHelper.FindLinksPointingIntoFolder(library, source);

        Assert.Empty(result);
    }

    [Fact]
    public void FindLinksPointingIntoFolder_SearchIsRecursiveInLibrary()
    {
        var library = Path.Combine(_tempDir, "library");
        var sub     = Path.Combine(library, "sub");
        var source  = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(source);

        var link1 = Path.Combine(library, "a.rplink");
        var link2 = Path.Combine(sub,     "b.rplink");
        File.WriteAllText(link1, Path.Combine(source, "a.mkv"));
        File.WriteAllText(link2, Path.Combine(source, "b.mkv"));

        var result = FolderOperationsHelper.FindLinksPointingIntoFolder(library, source);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FindLinksPointingIntoFolder_ComparisonIsCaseInsensitive()
    {
        var library = Path.Combine(_tempDir, "library");
        var source  = Path.Combine(_tempDir, "SourceFolder");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(source);

        // Store path with different casing
        var linkPath = Path.Combine(library, "x.rplink");
        File.WriteAllText(linkPath, source.ToLower());

        var result = FolderOperationsHelper.FindLinksPointingIntoFolder(library, source);

        Assert.Single(result);
    }
}
