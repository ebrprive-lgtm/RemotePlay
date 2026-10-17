using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class VideoByteRangeParserTests
{
    [Theory]
    [InlineData("bytes=2-5", 10, 2, 5)]
    [InlineData("bytes=4-", 10, 4, 9)]
    [InlineData("bytes=-3", 10, 7, 9)]
    [InlineData("bytes=0-99", 10, 0, 9)]
    [InlineData("BYTES=2-5", 10, 2, 5)]
    public void Parse_SingleSatisfiableRange_ReturnsClampedRange(
        string header,
        long length,
        long expectedStart,
        long expectedEnd)
    {
        var result = VideoByteRangeParser.Parse(header, length);

        Assert.Equal(VideoByteRangeKind.PartialContent, result.Kind);
        Assert.Equal(expectedStart, result.Start);
        Assert.Equal(expectedEnd, result.End);
    }

    [Theory]
    [InlineData("bytes=10-", 10)]
    [InlineData("bytes=10-11", 10)]
    [InlineData("bytes=-3", 0)]
    [InlineData("bytes=-0", 10)]
    public void Parse_UnsatisfiableRange_ReturnsUnsatisfiable(string header, long length)
    {
        var result = VideoByteRangeParser.Parse(header, length);

        Assert.Equal(VideoByteRangeKind.Unsatisfiable, result.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-1")]
    [InlineData("bytes=invalid")]
    [InlineData("bytes=0-1,4-5")]
    [InlineData("bytes=5-4")]
    [InlineData("bytes=-")]
    public void Parse_MalformedOrUnsupportedRange_IgnoresRange(string? header)
    {
        var result = VideoByteRangeParser.Parse(header, 10);

        Assert.Equal(VideoByteRangeKind.FullContent, result.Kind);
    }
}
