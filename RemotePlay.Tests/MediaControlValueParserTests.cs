using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class MediaControlValueParserTests
{
    [Fact]
    public void TryParseDoubleUsesInvariantCultureDecimalSeparator()
    {
        var parsed = MediaControlValueParser.TryParseDouble("1.25", out var result);

        Assert.True(parsed);
        Assert.Equal(1.25, result);
    }

    [Fact]
    public void TryParseDoubleReturnsFalseForInvalidValue()
    {
        var parsed = MediaControlValueParser.TryParseDouble("not-a-number", out _);

        Assert.False(parsed);
    }

    [Theory]
    [InlineData("-0.5", 0)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("2.5", 2)]
    public void TryParseClampedDoublePreservesZeroAndClampsRange(string value, double expected)
    {
        var parsed = MediaControlValueParser.TryParseClampedDouble(value, 0, 2, out var result);

        Assert.True(parsed);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryParseClampedDoubleReturnsFalseForInvalidValue()
    {
        var parsed = MediaControlValueParser.TryParseClampedDouble("", 0, 1, out _);

        Assert.False(parsed);
    }

    [Fact]
    public void TryParseIntegerUsesInvariantCulture()
    {
        var parsed = MediaControlValueParser.TryParseInteger("-1", out var result);

        Assert.True(parsed);
        Assert.Equal(-1, result);
    }
}
