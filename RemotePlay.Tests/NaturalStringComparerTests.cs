using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class NaturalStringComparerTests
{
    private readonly NaturalStringComparer _comparer = new();

    [Fact]
    public void Compare_NullAndNull_ReturnsZero()
    {
        Assert.Equal(0, _comparer.Compare(null, null));
    }

    [Fact]
    public void Compare_NullAndNonNull_ReturnsNegative()
    {
        Assert.True(_comparer.Compare(null, "a") < 0);
    }

    [Fact]
    public void Compare_NonNullAndNull_ReturnsPositive()
    {
        Assert.True(_comparer.Compare("a", null) > 0);
    }

    [Fact]
    public void Compare_EqualStrings_ReturnsZero()
    {
        Assert.Equal(0, _comparer.Compare("Episode 1", "Episode 1"));
    }

    [Fact]
    public void Compare_SingleDigitBeforeDoubleDigit_ReturnsNegative()
    {
        Assert.True(_comparer.Compare("Episode 9", "Episode 10") < 0);
    }

    [Fact]
    public void Compare_DoubleDigitAfterSingleDigit_ReturnsPositive()
    {
        Assert.True(_comparer.Compare("Episode 10", "Episode 9") > 0);
    }

    [Fact]
    public void Compare_SortsList_InNaturalOrder()
    {
        var items = new[] { "Episode 10", "Episode 2", "Episode 1", "Episode 20" };
        var sorted = items.OrderBy(x => x, _comparer).ToList();

        Assert.Equal(["Episode 1", "Episode 2", "Episode 10", "Episode 20"], sorted);
    }

    [Fact]
    public void Compare_PureTextStrings_AreCaseInsensitive()
    {
        Assert.Equal(0, _comparer.Compare("Alpha", "alpha"));
    }

    [Fact]
    public void Compare_EmptyStrings_ReturnsZero()
    {
        Assert.Equal(0, _comparer.Compare(string.Empty, string.Empty));
    }

    [Fact]
    public void Compare_EmptyAndNonEmpty_ReturnsNegative()
    {
        Assert.True(_comparer.Compare(string.Empty, "a") < 0);
    }
}
