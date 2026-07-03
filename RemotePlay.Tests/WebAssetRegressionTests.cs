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

    [Fact]
    public void VideoFolderRowsUseCentralYearParsingHelper()
    {
        var appJs = ReadConcatenatedAppJs();

        AssertContainsAll(
            appJs,
            "function _parseLeadingYearTitle(name)",
            "rawName.match(/^((?:19|20)\\d{2})\\s*[-–—]\\s*(.+)$/)",
            "if (!m) return { title: rawName || 'Folder', year: '' };");
    }

    [Fact]
    public void VideoFolderRowsDoNotUseBroadYearPrefixSplit()
    {
        var appJs = ReadConcatenatedAppJs();

        Assert.DoesNotContain("rawName.match(/^\\d{4}", appJs, StringComparison.Ordinal);
        Assert.DoesNotContain("split('-')", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoFolderRowsExposeAccessibleLinkedFolderLabel()
    {
        var appJs = ReadConcatenatedAppJs();

        AssertContainsAll(
            appJs,
            "folderTypeText = f.isLink ? 'linked folder' : 'folder'",
            "aria-label=\"Open ",
            "'linked folder'",
            "'folder'");
    }

    [Fact]
    public void VideoFolderRowsKeepFoldersFirstRegardlessOfSort()
    {
        var appJs = ReadConcatenatedAppJs();

        AssertContainsAll(
            appJs,
            "// Keep folders and folder links at the top regardless of active video sort.",
            "return [..._currentFolderRows, ...sortedFiles];");
    }

    [Fact]
    public void VideoFolderRowsWireYearColumnAndTrimmedTitle()
    {
        var appJs = ReadConcatenatedAppJs();

        AssertContainsAll(
            appJs,
            "const folderName = f.folderDisplayName || f.displayName || 'Folder';",
            "const folderYear = f.folderDisplayYear || '';",
            "<div class=\"vlc-year\">' + esc(folderYear) + '</div>");
    }

    [Fact]
    public void VideoFolderRowsHaveDedicatedListViewStyling()
    {
        var css = ReadWebAsset("styles.css");

        AssertContainsAll(
            css,
            ".movie-grid.list-view .movie-card.video-folder-row",
            ".movie-grid.list-view .movie-card.video-folder-row-link",
            ".movie-grid.list-view .movie-card.video-folder-row .vlc-cb{opacity:.4}",
            ".movie-grid.list-view .movie-card.video-folder-row .vlc-cb{pointer-events:none}");
    }

    [Fact]
    public void MusicListViewComposesFolderRowsBeforeTracks()
    {
        var appJs = ReadConcatenatedAppJs();

        AssertContainsAll(
            appJs,
            "const listPlaylists = isList && !searching && Array.isArray(data.playlists)",
            "const listFolders = isList && !searching ? _sortedMusicFoldersForListRows(data.folders) : [];",
            "const rows = isList ? [...listFolders, ...listPlaylists, ...sortedFiles] : sortedFiles;",
            "if (f.isPlaylistRow) {",
            "if (f.isFolderRow) {");
    }

    [Fact]
    public void MusicFolderRowsUseTrackCardListStyling()
    {
        var css = ReadWebAsset("styles.css");

        AssertContainsAll(
            css,
            "music-track-card.music-folder-row{cursor:pointer",
            "music-track-card.music-folder-row.music-folder-row-link{border-left-color",
            "music-folder-row-cb{opacity:.35;pointer-events:none");
    }

    [Fact]
    public void MusicTrackBindingsSkipFolderRows()
    {
        var appJs = ReadConcatenatedAppJs();

        Assert.Contains("document.querySelectorAll('.music-track-card:not(.music-folder-row):not(.music-playlist-row)')", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicPlaylistRowsUseTrackCardListStyling()
    {
        var css = ReadWebAsset("styles.css");

        AssertContainsAll(
            css,
            "music-track-card.music-playlist-row{cursor:pointer",
            "music-track-card.music-playlist-row:hover{background",
            "music-playlist-row-cb{opacity:.35;pointer-events:none");
    }

    [Fact]
    public void AppJsModuleFilesExistBeforeConcatenation()
    {
        foreach (var module in AppJsModules)
        {
            Assert.True(
                File.Exists(Path.Combine(WebAssetsPath, module)),
                $"Expected JS module '{module}' to exist under WebAssets.");
        }
    }

    private static string ReadWebAsset(string fileName) =>
        File.ReadAllText(Path.Combine(WebAssetsPath, fileName));

    private static void AssertContainsAll(string source, params string[] expectedFragments)
    {
        foreach (var fragment in expectedFragments)
            Assert.Contains(fragment, source, StringComparison.Ordinal);
    }

    private static void AssertContainsAny(string source, params string[] expectedFragments)
    {
        var found = expectedFragments.Any(fragment =>
            source.Contains(fragment, StringComparison.Ordinal));

        Assert.True(found, $"Expected one of: {string.Join(", ", expectedFragments)}");
    }

    private static readonly string[] AppJsModules =
    [
        "app-core.js",
        "app-diagnostics.js",
        "app-playback.js",
        "app-context-menu.js",
        "app-library.js",
        "app-live-tv.js",
        "app-radio.js",
        "app-globe.js",
        "app-radio-status.js",
        "app-local.js",
    ];

    private static string ReadConcatenatedAppJs()
    {
        // Mirror the concatenation logic from WebServer.cs
        return string.Join("\n", AppJsModules.Select(ReadWebAsset));
    }
}
