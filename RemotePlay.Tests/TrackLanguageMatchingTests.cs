using RemotePlay;
using Xunit;

namespace RemotePlay.Tests;

public sealed class TrackLanguageMatchingTests
{
    [Theory]
    [InlineData("English", "eng")]
    [InlineData("English SDH", "eng")]
    [InlineData("en", "eng")]
    [InlineData("en-US subtitles", "eng")]
    [InlineData("eng forced", "english")]
    public void EnglishAliasesMatchEnglishPreference(string trackName, string language)
    {
        var result = MainWindow.TrackNameMatchesLanguage(trackName, language);

        Assert.True(result);
    }

    [Fact]
    public void DifferentLanguageDoesNotMatchEnglishPreference()
    {
        var result = MainWindow.TrackNameMatchesLanguage("French", "eng");

        Assert.False(result);
    }
}
