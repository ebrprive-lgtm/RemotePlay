using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class LibraryIndexHelpersTests
{
    // ── BuildSearchText ────────────────────────────────────────────────────

    [Fact]
    public void BuildSearchText_ReturnsRelativePathWithSpaces()
    {
        var root = Path.Combine("C:", "Movies");
        var file = Path.Combine(root, "Action", "Die Hard.mp4");

        var result = LibraryIndexHelpers.BuildSearchText(root, file);

        Assert.Contains("Action", result);
        Assert.Contains("Die Hard.mp4", result);
        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), result);
    }

    [Fact]
    public void BuildSearchText_FileDirectlyUnderRoot_ReturnsSingleSegment()
    {
        var root = Path.Combine("C:", "Movies");
        var file = Path.Combine(root, "Top Gun.mp4");

        var result = LibraryIndexHelpers.BuildSearchText(root, file);

        Assert.Equal("Top Gun.mp4", result);
    }

    // ── BuildBreadcrumbs ───────────────────────────────────────────────────

    [Fact]
    public void BuildBreadcrumbs_TargetIsRoot_ReturnsSingleCrumb()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var crumbs = LibraryIndexHelpers.BuildBreadcrumbs(root, root);

        Assert.Single(crumbs);
    }

    [Fact]
    public void BuildBreadcrumbs_TargetIsSubDirectory_ReturnsTwoCrumbs()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sub = Path.Combine(root, "Action");

        var crumbs = LibraryIndexHelpers.BuildBreadcrumbs(root, sub);

        Assert.Equal(2, crumbs.Length);
    }

    [Fact]
    public void BuildBreadcrumbs_NestedSubDirectory_CorrectCrumbCount()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var nested = Path.Combine(root, "Action", "Marvel");

        var crumbs = LibraryIndexHelpers.BuildBreadcrumbs(root, nested);

        Assert.Equal(3, crumbs.Length);
    }

    // ── EnumerateLibraryVideoFiles ─────────────────────────────────────────

    [Fact]
    public void EnumerateLibraryVideoFiles_EmptyDirectory_ReturnsNoFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        var result = LibraryIndexHelpers.EnumerateLibraryVideoFiles(dir, new HashSet<string>()).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void EnumerateLibraryVideoFiles_FilesInRoot_ReturnsFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "movie.mp4");
        File.WriteAllText(file, string.Empty);

        var result = LibraryIndexHelpers.EnumerateLibraryVideoFiles(dir, new HashSet<string>()).ToList();

        Assert.Contains(file, result);
    }

    [Fact]
    public void EnumerateLibraryVideoFiles_IgnoredSubDirectory_IsSkipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var ignored = Path.Combine(dir, "Subs");
        Directory.CreateDirectory(ignored);
        var file = Path.Combine(ignored, "subtitle.srt");
        File.WriteAllText(file, string.Empty);

        var ignored1 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Subs" };
        var result = LibraryIndexHelpers.EnumerateLibraryVideoFiles(dir, ignored1).ToList();

        Assert.DoesNotContain(file, result);
    }

    [Fact]
    public void EnumerateLibraryVideoFiles_NonIgnoredSubDirectory_IsIncluded()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var sub = Path.Combine(dir, "Action");
        Directory.CreateDirectory(sub);
        var file = Path.Combine(sub, "movie.mp4");
        File.WriteAllText(file, string.Empty);

        var result = LibraryIndexHelpers.EnumerateLibraryVideoFiles(dir, new HashSet<string>()).ToList();

        Assert.Contains(file, result);
    }

    [Fact]
    public void EnumerateLibraryVideoFiles_InvokesOnFolderScannedCallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);

        int callCount = 0;
        LibraryIndexHelpers.EnumerateLibraryVideoFiles(dir, new HashSet<string>(), () => callCount++).ToList();

        Assert.True(callCount >= 1);
    }

    [Fact]
    public void EnumerateLibraryVideoFiles_NonExistentDirectory_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "nonexistent");

        var result = LibraryIndexHelpers.EnumerateLibraryVideoFiles(dir, new HashSet<string>()).ToList();

        Assert.Empty(result);
    }
}
