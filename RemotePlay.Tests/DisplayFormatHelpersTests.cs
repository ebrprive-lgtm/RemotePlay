using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class DisplayFormatHelpersTests
{
    // ── CleanDisplayTitle ───────────────────────────────────────────────────

    [Fact]
    public void CleanDisplayTitle_NullWhitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DisplayFormatHelpers.CleanDisplayTitle("   "));
    }

    [Fact]
    public void CleanDisplayTitle_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DisplayFormatHelpers.CleanDisplayTitle(string.Empty));
    }

    [Fact]
    public void CleanDisplayTitle_DotsReplacedWithSpaces()
    {
        var result = DisplayFormatHelpers.CleanDisplayTitle("The.Dark.Knight");
        Assert.Equal("The Dark Knight", result);
    }

    [Fact]
    public void CleanDisplayTitle_UnderscoresReplacedWithSpaces()
    {
        var result = DisplayFormatHelpers.CleanDisplayTitle("The_Dark_Knight");
        Assert.Equal("The Dark Knight", result);
    }

    [Fact]
    public void CleanDisplayTitle_YearStripped()
    {
        var result = DisplayFormatHelpers.CleanDisplayTitle("Interstellar 2014");
        Assert.DoesNotContain("2014", result);
    }

    [Fact]
    public void CleanDisplayTitle_QualityTagStripped()
    {
        var result = DisplayFormatHelpers.CleanDisplayTitle("Movie.1080p.BluRay");
        Assert.DoesNotContain("1080p", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bluray", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanDisplayTitle_MultipleSpacesCollapsed()
    {
        var result = DisplayFormatHelpers.CleanDisplayTitle("Movie  Title");
        Assert.Equal("Movie Title", result);
    }

    [Fact]
    public void CleanDisplayTitle_WhenCleaningResultIsEmpty_ReturnOriginalName()
    {
        // Only a quality tag — after stripping nothing useful is left.
        // The fallback returns the original input.
        var input = "1080p";
        var result = DisplayFormatHelpers.CleanDisplayTitle(input);
        Assert.Equal(input, result);
    }

    // ── FormatTime ──────────────────────────────────────────────────────────

    [Fact]
    public void FormatTime_Zero_ReturnsMmSs()
    {
        Assert.Equal("0:00", DisplayFormatHelpers.FormatTime(0));
    }

    [Fact]
    public void FormatTime_NegativeSeconds_TreatedAsZero()
    {
        Assert.Equal("0:00", DisplayFormatHelpers.FormatTime(-5));
    }

    [Fact]
    public void FormatTime_UnderOneHour_OmitsHours()
    {
        Assert.Equal("5:30", DisplayFormatHelpers.FormatTime(330));
    }

    [Fact]
    public void FormatTime_ExactlyOneHour_IncludesHours()
    {
        Assert.Equal("1:00:00", DisplayFormatHelpers.FormatTime(3600));
    }

    [Fact]
    public void FormatTime_OverOneHour_FormatsHhMmSs()
    {
        Assert.Equal("1:23:45", DisplayFormatHelpers.FormatTime(5025));
    }

    [Fact]
    public void FormatTime_LargeValue_FormatsCorrectly()
    {
        // 2h 3m 4s = 7384 seconds
        Assert.Equal("2:03:04", DisplayFormatHelpers.FormatTime(7384));
    }
}
