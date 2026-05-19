using Xunit;

namespace RemotePlay.Tests;

public sealed class WebAssetRegressionTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string WebAssetsPath = Path.Combine(RepositoryRoot, "WebAssets");

    [Fact]
    public void IndexReferencesExtractedStylesheet()
    {
        var html = ReadWebAsset("index.html");

        Assert.Contains("href=\"/styles.css", html, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexReferencesExtractedScript()
    {
        var html = ReadWebAsset("index.html");

        Assert.Contains("src=\"/app.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MovieCardsRemainClickableForPlayback()
    {
        var appJs = ReadWebAsset("app.js");

        Assert.Contains("const action=isPlaying?'':' onclick=\"onCardClick", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public void MovieCardClickLaunchesPlaybackEndpoint()
    {
        var appJs = ReadWebAsset("app.js");

        Assert.Contains("/api/play?path=", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexLinksSetupPage()
    {
        var html = ReadWebAsset("index.html");

        Assert.Contains("href=\"/setup\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackProfilesAreAvailable()
    {
        var appJs = ReadWebAsset("app.js");

        Assert.Contains("applyPlaybackProfile", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public void ThumbnailQueueControlsUseServerEndpoints()
    {
        var appJs = ReadWebAsset("app.js");

        Assert.Contains("/api/thumbnails/status", appJs, StringComparison.Ordinal);
        Assert.Contains("/api/thumbnails/start", appJs, StringComparison.Ordinal);
        Assert.Contains("/api/thumbnails/cancel", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public void MovieGridUsesLazyThumbnailHydration()
    {
        var appJs = ReadWebAsset("app.js");
        var css = ReadWebAsset("styles.css");

        Assert.Contains("IntersectionObserver", appJs, StringComparison.Ordinal);
        Assert.Contains("data-thumb", appJs, StringComparison.Ordinal);
        Assert.Contains("#thumb-status", css, StringComparison.Ordinal);
    }

    private static string ReadWebAsset(string fileName) =>
        File.ReadAllText(Path.Combine(WebAssetsPath, fileName));
}
