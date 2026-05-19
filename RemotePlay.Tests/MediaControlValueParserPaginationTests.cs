using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class MediaControlValueParserPaginationTests
{
    // ── ReadNonNegativeInt ─────────────────────────────────────────────────

    [Fact]
    public void ReadNonNegativeInt_NullValue_ReturnsZero()
    {
        Assert.Equal(0, MediaControlValueParser.ReadNonNegativeInt(null));
    }

    [Fact]
    public void ReadNonNegativeInt_EmptyString_ReturnsZero()
    {
        Assert.Equal(0, MediaControlValueParser.ReadNonNegativeInt(string.Empty));
    }

    [Fact]
    public void ReadNonNegativeInt_NonNumericString_ReturnsZero()
    {
        Assert.Equal(0, MediaControlValueParser.ReadNonNegativeInt("abc"));
    }

    [Fact]
    public void ReadNonNegativeInt_Zero_ReturnsZero()
    {
        Assert.Equal(0, MediaControlValueParser.ReadNonNegativeInt("0"));
    }

    [Fact]
    public void ReadNonNegativeInt_NegativeNumber_ReturnsZero()
    {
        Assert.Equal(0, MediaControlValueParser.ReadNonNegativeInt("-5"));
    }

    [Fact]
    public void ReadNonNegativeInt_PositiveNumber_ReturnsParsedValue()
    {
        Assert.Equal(42, MediaControlValueParser.ReadNonNegativeInt("42"));
    }

    // ── ReadPositiveInt ────────────────────────────────────────────────────

    [Fact]
    public void ReadPositiveInt_NullValue_ReturnsDefault()
    {
        Assert.Equal(20, MediaControlValueParser.ReadPositiveInt(null, 20, 100));
    }

    [Fact]
    public void ReadPositiveInt_EmptyString_ReturnsDefault()
    {
        Assert.Equal(20, MediaControlValueParser.ReadPositiveInt(string.Empty, 20, 100));
    }

    [Fact]
    public void ReadPositiveInt_Zero_ReturnsDefault()
    {
        Assert.Equal(20, MediaControlValueParser.ReadPositiveInt("0", 20, 100));
    }

    [Fact]
    public void ReadPositiveInt_NegativeValue_ReturnsDefault()
    {
        Assert.Equal(20, MediaControlValueParser.ReadPositiveInt("-1", 20, 100));
    }

    [Fact]
    public void ReadPositiveInt_ValidValueWithinMax_ReturnsParsedValue()
    {
        Assert.Equal(50, MediaControlValueParser.ReadPositiveInt("50", 20, 100));
    }

    [Fact]
    public void ReadPositiveInt_ValueExceedingMax_ReturnsClamped()
    {
        Assert.Equal(100, MediaControlValueParser.ReadPositiveInt("9999", 20, 100));
    }

    [Fact]
    public void ReadPositiveInt_ValueEqualToMax_ReturnsMax()
    {
        Assert.Equal(100, MediaControlValueParser.ReadPositiveInt("100", 20, 100));
    }
}
