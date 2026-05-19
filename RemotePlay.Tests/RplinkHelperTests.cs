using System.IO;
using Xunit;
using RemotePlay;

namespace RemotePlay.Tests;

public class RplinkHelperTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public RplinkHelperTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // -- IsRplinkFile ---------------------------------------------------------

    [Fact]
    public void IsRplinkFile_WithRplinkExtension_ReturnsTrue()
    {
        Assert.True(RplinkHelper.IsRplinkFile("movie.rplink"));
    }

    [Fact]
    public void IsRplinkFile_WithUppercaseExtension_ReturnsTrue()
    {
        Assert.True(RplinkHelper.IsRplinkFile("movie.RPLINK"));
    }

    [Fact]
    public void IsRplinkFile_WithDifferentExtension_ReturnsFalse()
    {
        Assert.False(RplinkHelper.IsRplinkFile("movie.mp4"));
    }

    // -- TryReadTargetRaw -----------------------------------------------------

    [Fact]
    public void TryReadTargetRaw_WithAbsolutePath_ReturnsThatPath()
    {
        var targetPath = @"C:\movies\film.mkv";
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        File.WriteAllText(linkPath, targetPath);

        var result = RplinkHelper.TryReadTargetRaw(linkPath);

        Assert.Equal(targetPath, result);
    }

    [Fact]
    public void TryReadTargetRaw_WithRelativePath_ReturnsAbsolutePath()
    {
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        File.WriteAllText(linkPath, "film.mkv");

        var result = RplinkHelper.TryReadTargetRaw(linkPath);

        Assert.Equal(Path.Combine(_tempDir, "film.mkv"), result);
    }

    [Fact]
    public void TryReadTargetRaw_EmptyFile_ReturnsNull()
    {
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        File.WriteAllText(linkPath, "   ");

        Assert.Null(RplinkHelper.TryReadTargetRaw(linkPath));
    }

    [Fact]
    public void TryReadTargetRaw_NonExistentFile_ReturnsNull()
    {
        Assert.Null(RplinkHelper.TryReadTargetRaw(Path.Combine(_tempDir, "missing.rplink")));
    }

    // -- IsTargetFolder -------------------------------------------------------

    [Fact]
    public void IsTargetFolder_WhenTargetIsExistingDirectory_ReturnsTrue()
    {
        var subDir = Path.Combine(_tempDir, "subdir");
        Directory.CreateDirectory(subDir);
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        File.WriteAllText(linkPath, subDir);

        Assert.True(RplinkHelper.IsTargetFolder(linkPath));
    }

    [Fact]
    public void IsTargetFolder_WhenTargetIsExistingFile_ReturnsFalse()
    {
        var filePath = Path.Combine(_tempDir, "video.mkv");
        File.WriteAllText(filePath, "data");
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        File.WriteAllText(linkPath, filePath);

        Assert.False(RplinkHelper.IsTargetFolder(linkPath));
    }

    // -- TryReadTarget --------------------------------------------------------

    [Fact]
    public void TryReadTarget_WhenTargetFileExists_ReturnsPath()
    {
        var filePath = Path.Combine(_tempDir, "video.mkv");
        File.WriteAllText(filePath, "data");
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        File.WriteAllText(linkPath, filePath);

        Assert.Equal(filePath, RplinkHelper.TryReadTarget(linkPath));
    }

    [Fact]
    public void TryReadTarget_WhenTargetDoesNotExist_ReturnsNull()
    {
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        File.WriteAllText(linkPath, Path.Combine(_tempDir, "ghost.mkv"));

        Assert.Null(RplinkHelper.TryReadTarget(linkPath));
    }

    // -- Create ---------------------------------------------------------------

    [Fact]
    public void Create_WritesTargetPathToFile()
    {
        var linkPath = Path.Combine(_tempDir, "link.rplink");
        var targetPath = @"C:\movies\film.mkv";

        RplinkHelper.Create(linkPath, targetPath);

        Assert.Equal(targetPath, File.ReadAllText(linkPath));
    }

    [Fact]
    public void Create_WithEmptyLinkPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => RplinkHelper.Create("", @"C:\target.mkv"));
    }

    // -- MakeRelativeIfPossible -----------------------------------------------

    [Fact]
    public void MakeRelativeIfPossible_SameVolume_ReturnsRelativePath()
    {
        var linkPath = Path.Combine(_tempDir, "sub", "link.rplink");
        var targetPath = Path.Combine(_tempDir, "sub", "video.mkv");

        var result = RplinkHelper.MakeRelativeIfPossible(linkPath, targetPath);

        Assert.Equal("video.mkv", result);
    }
}
