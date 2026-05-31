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
        var appJs = ReadConcatenatedAppJs();

        Assert.Contains("const action = isPlaying ? '' : ' onclick=\"onCardClick(event,", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public void MovieCardClickLaunchesPlaybackEndpoint()
    {
        var appJs = ReadConcatenatedAppJs();

        Assert.Contains("/api/play?path=", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexLinksSettingsPage()
    {
        var html = ReadWebAsset("index.html");

        Assert.Contains("href=\"/settings\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackProfilesAreAvailable()
    {
        var appJs = ReadConcatenatedAppJs();

        Assert.Contains("applyPlaybackProfile", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public void ThumbnailQueueControlsUseServerEndpoints()
    {
        var appJs = ReadConcatenatedAppJs();

        Assert.Contains("/api/thumbnails/status", appJs, StringComparison.Ordinal);
        Assert.Contains("/api/thumbnails/start", appJs, StringComparison.Ordinal);
        Assert.Contains("/api/thumbnails/cancel", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public void MovieGridUsesLazyThumbnailHydration()
    {
        var appJs = ReadConcatenatedAppJs();
        var css = ReadWebAsset("styles.css");

        Assert.Contains("IntersectionObserver", appJs, StringComparison.Ordinal);
        Assert.Contains("data-thumb", appJs, StringComparison.Ordinal);
        Assert.Contains("#thumb-status", css, StringComparison.Ordinal);
    }

    private static string ReadWebAsset(string fileName) =>
        File.ReadAllText(Path.Combine(WebAssetsPath, fileName));

    private static string ReadConcatenatedAppJs()
    {
        // Mirror the concatenation logic from WebServer.cs
        string[] appJsModules =
        [
            "app-core.js",
            "app-diagnostics.js",
            "app-playback.js",
            "app-context-menu.js",
            "app-library.js",
            "app-radio.js",
            "app-globe.js",
            "app-radio-status.js",
            "app-local.js",
        ];

        return string.Join("\n", appJsModules.Select(ReadWebAsset));
    }
}
