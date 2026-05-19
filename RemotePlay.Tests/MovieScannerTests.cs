using System.IO;
using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class MovieScannerTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Touch(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, string.Empty);
    }

    // ── Basic filtering ──────────────────────────────────────────────────────

    [Fact]
    public void Scan_ReturnsVideoFilesOnly()
    {
        var dir = NewTempDir();
        Touch(Path.Combine(dir, "movie.mp4"));
        Touch(Path.Combine(dir, "clip.mkv"));
        Touch(Path.Combine(dir, "poster.jpg"));
        Touch(Path.Combine(dir, "readme.txt"));

        var result = MovieScanner.Scan(dir);

        Assert.Equal(2, result.Count);
        Assert.All(result, f => Assert.True(
            f.FullPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            f.FullPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Scan_IsCaseInsensitiveForExtensions()
    {
        var dir = NewTempDir();
        Touch(Path.Combine(dir, "Movie.MP4"));
        Touch(Path.Combine(dir, "Show.MKV"));

        var result = MovieScanner.Scan(dir);

        Assert.Equal(2, result.Count);
    }

    // ── Ordering ─────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_ReturnsFilesAlphabetically()
    {
        var dir = NewTempDir();
        Touch(Path.Combine(dir, "z_last.mp4"));
        Touch(Path.Combine(dir, "a_first.mkv"));
        Touch(Path.Combine(dir, "m_middle.avi"));

        var result = MovieScanner.Scan(dir);

        Assert.Equal(3, result.Count);
        Assert.True(
            string.Compare(result[0].FullPath, result[1].FullPath, StringComparison.Ordinal) < 0,
            "First entry should be alphabetically before second.");
        Assert.True(
            string.Compare(result[1].FullPath, result[2].FullPath, StringComparison.Ordinal) < 0,
            "Second entry should be alphabetically before third.");
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Scan_ReturnsEmpty_WhenDirectoryIsEmpty()
    {
        var dir = NewTempDir();

        var result = MovieScanner.Scan(dir);

        Assert.Empty(result);
    }

    [Fact]
    public void Scan_ReturnsEmptyAndCreatesDirectory_WhenDirectoryDoesNotExist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RemotePlay.Tests", Guid.NewGuid().ToString("N"), "NonExistent");

        var result = MovieScanner.Scan(dir);

        Assert.Empty(result);
        Assert.True(Directory.Exists(dir));
    }

    // ── Recursive scan ───────────────────────────────────────────────────────

    [Fact]
    public void Scan_ScansSubdirectoriesRecursively()
    {
        var dir = NewTempDir();
        var subDir = Path.Combine(dir, "Season1");
        Touch(Path.Combine(subDir, "episode1.mkv"));
        Touch(Path.Combine(subDir, "episode2.mkv"));
        Touch(Path.Combine(dir, "movie.mp4"));

        var result = MovieScanner.Scan(dir);

        Assert.Equal(3, result.Count);
    }

    // ── MovieFile record fields ───────────────────────────────────────────────

    [Fact]
    public void Scan_SetsNameToFileNameWithoutExtension()
    {
        var dir = NewTempDir();
        Touch(Path.Combine(dir, "My Great Film.mp4"));

        var result = MovieScanner.Scan(dir);

        Assert.Single(result);
        Assert.Equal("My Great Film", result[0].Name);
    }

    [Fact]
    public void Scan_SetsFullPathToAbsoluteFilePath()
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "film.mkv");
        Touch(file);

        var result = MovieScanner.Scan(dir);

        Assert.Single(result);
        Assert.Equal(file, result[0].FullPath);
    }

    // ── All supported extensions ─────────────────────────────────────────────

    [Theory]
    [InlineData("video.avi")]
    [InlineData("video.mov")]
    [InlineData("video.wmv")]
    [InlineData("video.m4v")]
    [InlineData("video.ts")]
    [InlineData("video.flv")]
    public void Scan_IncludesAllSupportedExtensions(string fileName)
    {
        var dir = NewTempDir();
        Touch(Path.Combine(dir, fileName));

        var result = MovieScanner.Scan(dir);

        Assert.Single(result);
    }
}
