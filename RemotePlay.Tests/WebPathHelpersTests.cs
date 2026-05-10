using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class WebPathHelpersTests
{
    [Fact]
    public void EncodePathThenDecodePathReturnsOriginalPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "Movies", "Example Movie.mkv");

        var encoded = WebPathHelpers.EncodePath(path);
        var decoded = WebPathHelpers.DecodePath(encoded);

        Assert.Equal(path, decoded);
    }

    [Fact]
    public void IsUnderRootReturnsTrueForRootItself()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemotePlayRoot");

        Assert.True(WebPathHelpers.IsUnderRoot(root, root));
    }

    [Fact]
    public void IsUnderRootReturnsTrueForChildPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemotePlayRoot");
        var child = Path.Combine(root, "Folder", "Movie.mp4");

        Assert.True(WebPathHelpers.IsUnderRoot(child, root));
    }

    [Fact]
    public void IsUnderRootReturnsFalseForPathWithSimilarPrefix()
    {
        var temp = Path.GetTempPath();
        var root = Path.Combine(temp, "RemotePlayRoot");
        var sibling = Path.Combine(temp, "RemotePlayRoot2", "Movie.mp4");

        Assert.False(WebPathHelpers.IsUnderRoot(sibling, root));
    }

    [Theory]
    [InlineData("movie.mp4", true)]
    [InlineData("movie.MKV", true)]
    [InlineData("poster.jpg", false)]
    public void IsVideoFileUsesConfiguredExtensions(string fileName, bool expected)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv" };

        var result = WebPathHelpers.IsVideoFile(fileName, extensions);

        Assert.Equal(expected, result);
    }
}
