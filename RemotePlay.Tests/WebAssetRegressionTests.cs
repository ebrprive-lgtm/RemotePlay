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

        Assert.Contains("/api/video/play?path=", appJs, StringComparison.Ordinal);
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
    public void PodcastDirectoryRendersSnapshotOrSkeletonBeforeProgressiveRefresh()
    {
        var podcastsJs = ReadWebAsset("app-podcasts.js");
        var css = ReadWebAsset("styles.css");

        AssertContainsAll(
            podcastsJs,
            "const _podcastDirectorySnapshotKey = 'rp_podcast_directory_v1';",
            "function _restorePodcastDirectorySnapshot()",
            "function _savePodcastDirectorySnapshot(shows)",
            "void _loadPodcastCountries();",
            "if (!_podcastFavoritesOnly) _loadPodcastDirectory();",
            "aria-busy=\"true\"",
            "podcast-directory-skeleton-card",
            "await _podcastWaitForPaint(request.signal);",
            "loadSection('', _podcastDirectorySnapshotPageSize, _podcastDirectorySnapshotPageSize)",
            "Showing saved recommendations from",
            "function podcastRetryDirectory()");
        AssertContainsAll(
            css,
            ".podcast-directory-skeleton-grid",
            ".podcast-directory-refresh-status",
            "prefers-reduced-motion:reduce");
    }

    [Fact]
    public void PodcastProgressShowsKnownDurationAndRequiresSeekCapabilityForSeeking()
    {
        var html = ReadWebAsset("index.html");
        var statusJs = ReadWebAsset("app-radio-status.js");
        var radioPlayer = ReadProjectFile(Path.Combine("Services", "RadioPlayer.cs"));
        var radioHandler = ReadProjectFile(Path.Combine("Services", "Web", "WebServer.Handlers.Radio.cs"));

        AssertContainsAll(
            html,
            "id=\"radio-bar-progress-row\" class=\"podcast-progress-row\" hidden",
            "aria-label=\"Podcast playback position\"");
        AssertContainsAll(
            statusJs,
            "const isSeekable = Boolean(canSeek && duration > 0);",
            "if (row) row.hidden = duration <= 0;",
            "seek.disabled = !isSeekable;",
            "lastEnd >= duration - 1",
            "'/api/radio/seek?' + new URLSearchParams");
        Assert.DoesNotContain("row.hidden = !isSeekable", statusJs, StringComparison.Ordinal);
        AssertContainsAll(
            radioPlayer,
            "if (!_reader.CanSeek)",
            "CanSeek: canSeek",
            "DurationSeconds: durationSeconds");
        Assert.Contains("TrySendResponse(ctx, 409", radioHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void PodcastPlayerUsesAnEpisodeBadgeAndAvoidsDuplicateControlHeadings()
    {
        var html = ReadWebAsset("index.html");
        var css = ReadWebAsset("styles.css");
        var start = html.IndexOf("id=\"radio-player-bar\"", StringComparison.Ordinal);
        var end = html.IndexOf("id=\"music-player-bar\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The Radio/Podcast player markup should be present.");
        var player = html[start..end];

        AssertContainsAll(
            player,
            "class=\"transport-button-row\"",
            "class=\"button-group transport-main-group\"",
            "class=\"transport-secondary-group\"",
            "class=\"radio-bar-meta-row\"",
            "id=\"local-podcast-index\" class=\"podcast-episode-index\"",
            "id=\"radio-bar-episode-description\" class=\"podcast-episode-description\" hidden",
            "Episode description",
            "id=\"radio-btn-stop\"",
            "for=\"radio-bar-volume\">Volume</label>",
            "for=\"radio-bar-boost\">Boost</label>");
        var episodeIndex = player.IndexOf("id=\"local-podcast-index\"", StringComparison.Ordinal);
        var transport = player.IndexOf("class=\"transport-button-row\"", StringComparison.Ordinal);
        Assert.True(episodeIndex >= 0 && episodeIndex < transport,
            "The episode position should appear in the player metadata above the transport controls.");
        var secondaryGroup = player.IndexOf("class=\"transport-secondary-group\"", StringComparison.Ordinal);
        Assert.DoesNotContain("local-podcast-index", player[secondaryGroup..], StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"control-title\">Volume</div>", player, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"control-title\">Boost and effects</div>", player, StringComparison.Ordinal);
        AssertContainsAll(
            css,
            "#radio-bar-meta-row{display:flex;align-items:center;gap:.4rem;min-width:0}",
            "#radio-player-bar .podcast-episode-index{",
            "#radio-player-bar .podcast-episode-description[hidden]{display:none!important}",
            "#radio-player-bar #radio-btn-stop{flex:0 0 auto;min-width:5rem;min-height:44px;margin-left:0}",
            "body.phone-remote-only #radio-player-bar .transport-main-group{display:grid;grid-template-columns:repeat(3,minmax(0,1fr))",
            "body.phone-remote-only #radio-player-bar .transport-secondary-group #radio-btn-stop{width:auto");
    }

    [Fact]
    public void RadioPodcastPlayerDockIsHiddenOutsideAudioModes()
    {
        var libraryJs = ReadWebAsset("app-library.js");
        var css = ReadWebAsset("styles.css");

        Assert.Contains(
            "document.body.classList.toggle('podcasts-mode', mode === 'podcasts');",
            libraryJs,
            StringComparison.Ordinal);
        AssertContainsAll(
            css,
            "body:not(.radio-mode):not(.podcasts-mode) #radio-player-bar{display:none!important}",
            "body.radio-player-docked:is(.radio-mode,.podcasts-mode) #main-layout{grid-template-columns:1fr 280px}",
            "body.radio-player-docked:is(.radio-mode,.podcasts-mode) #radio-player-bar{display:flex!important}");
        Assert.DoesNotContain(
            "body.radio-player-docked #radio-player-bar",
            css,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PodcastServerLocalHandoffKeepsPodcastModeAndResumePosition()
    {
        var coreJs = ReadWebAsset("app-core.js");
        var localJs = ReadWebAsset("app-local.js");
        var toggleStart = coreJs.IndexOf("function togglePlayLocal()", StringComparison.Ordinal);
        var podcastBranch = coreJs.IndexOf("_localMediaType === 'PodcastServer'", toggleStart, StringComparison.Ordinal);
        var radioBranch = coreJs.IndexOf("} else if (typeof _radioIsPlaying", podcastBranch, StringComparison.Ordinal);
        Assert.True(toggleStart >= 0 && podcastBranch > toggleStart && radioBranch > podcastBranch,
            "The Podcast handoff should run before the generic Radio handoff.");
        var handoff = coreJs[podcastBranch..radioBranch];

        AssertContainsAll(
            handoff,
            "const seekDuration = seekEl ? Number(seekEl.max) : 0;",
            "elapsedBase + elapsedSincePoll",
            "startAt: resumePos",
            "mode: 'podcast'",
            "localPlay(_radioCurrentUrl, handoffName || 'Podcast episode', 'Podcast', podcastMetadata)");
        Assert.DoesNotContain("stream-proxy", handoff, StringComparison.Ordinal);
        AssertContainsAll(
            localJs,
            "function _prepareLocalPodcastResume(audio, requestedPosition, generation)",
            "audio.addEventListener('loadedmetadata', onLoadedMetadata, { once: true });",
            "audio.currentTime = target;",
            "audio.muted = false;");
    }

    [Fact]
    public void LocalPodcastWaveformUsesTheSharedAnalyserAndRangeAwareProxy()
    {
        var localJs = ReadWebAsset("app-local.js");
        var radioJs = ReadWebAsset("app-radio.js");
        var statusJs = ReadWebAsset("app-radio-status.js");
        var radioHandler = ReadProjectFile(Path.Combine("Services", "Web", "WebServer.Handlers.Radio.cs"));

        AssertContainsAll(
            localJs,
            "const playbackUrl = _localMediaType === 'Podcast'",
            "'/api/radio/stream-proxy?url=' + encodeURIComponent(url)",
            "a.src = playbackUrl;");
        AssertContainsAll(
            radioJs,
            "let _localBoostAnalyser = null;",
            "_localBoostGain.connect(_localBoostAnalyser);",
            "_localBoostAnalyser.connect(_localBoostCtx.destination);",
            "function _getLocalAudioAnalyser()");
        AssertContainsAll(
            statusJs,
            "return _getLocalAudioAnalyser();",
            "analyser.getByteFrequencyData(freqData);");
        Assert.DoesNotContain("Simulated animation", statusJs, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.abs(Math.sin(t))", statusJs, StringComparison.Ordinal);
        AssertContainsAll(
            radioHandler,
            "var rangeHeader = ctx.Request.Headers[\"Range\"];",
            "proxyRequest.Headers.Range = range;",
            "upstream.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable",
            "ctx.Response.Headers[\"Content-Range\"] = contentRange.ToString();",
            "ctx.Response.ContentLength64 = contentLength;");
    }

    [Fact]
    public void PodcastPlayerExposesTheFullCurrentEpisodeDescription()
    {
        var html = ReadWebAsset("index.html");
        var podcastsJs = ReadWebAsset("app-podcasts.js");
        var radioJs = ReadWebAsset("app-radio.js");
        var start = html.IndexOf("id=\"radio-player-bar\"", StringComparison.Ordinal);
        var end = html.IndexOf("id=\"music-player-bar\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The Radio/Podcast player markup should be present.");
        var player = html[start..end];

        AssertContainsAll(
            player,
            "id=\"radio-bar-episode-description\"",
            "id=\"radio-bar-episode-description-text\"");
        AssertContainsAll(
            podcastsJs,
            "const description = _podcastPlainText(episode?.description || '');",
            "description };");
        AssertContainsAll(
            radioJs,
            "episodeDescriptionText.textContent = fullEpisodeDescription;",
            "episodeDescription.hidden = !hasEpisodeDescription;",
            "episodeDescription.open = false;");
    }

    [Fact]
    public void RadioLiveStatusDoesNotReplaceMusicSeeking()
    {
        var html = ReadWebAsset("index.html");
        var radioJs = ReadWebAsset("app-radio.js");
        var statusJs = ReadWebAsset("app-radio-status.js");

        AssertContainsAll(
            html,
            "id=\"radio-bar-live\"",
            "id=\"radio-bar-elapsed\"",
            "id=\"music-seek\"");
        Assert.Contains("liveIndicator.hidden = !(isLiveRadio && playing)", radioJs, StringComparison.Ordinal);
        Assert.Contains("_localMediaType === 'Radio'", statusJs, StringComparison.Ordinal);
        Assert.Contains("onpointerdown=\"onMusicSeekDrag()\" onpointerup=\"onMusicSeekCommit()\"", html, StringComparison.Ordinal);
        Assert.Contains("oninput=\"onMusicSeekDrag()\" onchange=\"onMusicSeekCommit()\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicSeekTimelineRequiresKnownDuration()
    {
        var html = ReadWebAsset("index.html");
        var libraryJs = ReadWebAsset("app-library.js");
        var statusJs = ReadWebAsset("app-radio-status.js");

        Assert.Contains("id=\"music-progress-row\" style=\"display:none\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Music position\" disabled", html, StringComparison.Ordinal);
        AssertContainsAll(
            libraryJs,
            "progressRow.style.display = duration > 0 ? 'grid' : 'none';",
            "seek.disabled = duration <= 0;",
            "if (!Number.isFinite(duration) || duration <= 0 || !Number.isFinite(rawPosition)) return;");
        AssertContainsAll(
            statusJs,
            "musicProgressRow.style.display = dur > 0 ? 'grid' : 'none';",
            "mSeek.disabled = dur <= 0;");
    }

    [Fact]
    public void RadioAndMusicAudioControlsSeparateVolumeBoostAndKeepQueueControls()
    {
        var html = ReadWebAsset("index.html");
        var appJs = ReadConcatenatedAppJs();
        var css = ReadWebAsset("styles.css");

        AssertContainsAll(
            html,
            "id=\"radio-bar-volume\" type=\"range\" min=\"0\" max=\"1\"",
            "id=\"radio-bar-boost\" type=\"range\" min=\"1\" max=\"3\"",
            "id=\"radio-audio-reset\"",
            "id=\"radio-audio-settings\"",
            "id=\"radio-eq-select-wrap\"",
            "id=\"music-bar-volume\" type=\"range\" min=\"0\" max=\"1\"",
            "id=\"music-bar-boost\" type=\"range\" min=\"1\" max=\"3\"",
            "id=\"music-audio-reset\"",
            "id=\"music-audio-settings\"",
            "id=\"music-reverb-select-wrap\"",
            "id=\"music-btn-queue-next\"",
            "id=\"music-queue-peek-card\"");
        AssertContainsAll(
            appJs,
            "function radioResetAudioDefaults()",
            "function musicResetAudioDefaults()",
            "'/api/radio/volume?v='",
            "'/api/radio/boost?v='",
            "'/api/music/volume?v='",
            "'/api/music/boost?v='",
            "radioVolume(0.8);",
            "musicVolume(0.8);",
            "classList.toggle('queue-mode', hasQueue)");
        Assert.Contains("queue-mode #music-btn-queue-next", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioPlayersExposeNamedRegionsLiveStatusAndControlStates()
    {
        var html = ReadWebAsset("index.html");

        AssertContainsAll(
            html,
            "id=\"radio-player-bar\" style=\"display:none\" role=\"region\" aria-label=\"Radio player\" aria-busy=\"false\"",
            "id=\"radio-player-announcement\" class=\"sr-only\" role=\"status\" aria-live=\"polite\"",
            "id=\"radio-health-dot\" class=\"radio-health-dot\" title=\"Stream health\" role=\"img\" aria-label=\"Stream status: idle\"",
            "id=\"radio-btn-play\" class=\"btn btn-red\" onclick=\"radioToggle()\" aria-label=\"Play radio playback\" aria-pressed=\"false\"",
            "id=\"music-player-bar\" style=\"display:none\" role=\"region\" aria-label=\"Music player\" aria-busy=\"false\"",
            "id=\"music-bar-state-status\" class=\"player-state-status\" aria-live=\"off\" hidden",
            "id=\"music-player-announcement\" class=\"sr-only\" role=\"status\" aria-live=\"polite\"",
            "id=\"music-btn-play\" class=\"btn btn-red\" onclick=\"musicToggle()\" aria-label=\"Play music\" aria-pressed=\"false\"",
            "id=\"music-btn-repeat\"",
            "aria-pressed=\"false\"");
    }

    [Fact]
    public void PlayerStatusAndPressedStatesTrackPlaybackChanges()
    {
        var coreJs = ReadWebAsset("app-core.js");
        var libraryJs = ReadWebAsset("app-library.js");
        var radioJs = ReadWebAsset("app-radio.js");
        var statusJs = ReadWebAsset("app-radio-status.js");
        var localJs = ReadWebAsset("app-local.js");

        AssertContainsAll(
            coreJs,
            "function _announcePlayerStatus(playerId, state, message)",
            "status.dataset.state === state",
            "status.dataset.state = state;");
        AssertContainsAll(
            libraryJs,
            "bar.setAttribute('aria-busy', String(isBuffering));",
            "playerStatus.textContent = statusText;",
            "btn.setAttribute('aria-pressed', String(isPlaying));",
            "btn.setAttribute('aria-pressed', String(_musicAutoPlay));",
            "btn.setAttribute('aria-pressed', String(_musicShuffle));",
            "btn.setAttribute('aria-pressed', String(_musicRepeat !== 'none'));",
            "'Music playback stopped.'");
        AssertContainsAll(
            radioJs,
            "playerBar.setAttribute('aria-label', isPodcast ? 'Podcast player' : 'Radio player');",
            "btn.setAttribute('aria-pressed', String(Boolean(playing)));",
            "stoppingPodcast ? { mode: 'podcast' } : {}");
        AssertContainsAll(
            statusJs,
            "visibleStatus.hidden = !isBusy;",
            "_announcePlayerStatus(statusId, `${mediaType.toLowerCase()}:loading`",
            "btn.setAttribute('aria-pressed', String(Boolean(effectivePlaying)));");
        AssertContainsAll(
            localJs,
            "bar.setAttribute('aria-busy', 'false');",
            "playButton.setAttribute('aria-pressed', 'false');");
    }

    [Fact]
    public void LocalAudioErrorsUseTheActivePlayersTitle()
    {
        var statusJs = ReadWebAsset("app-radio-status.js").Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "const nmEl = isMusic\n          ? document.getElementById('music-bar-title')\n          : document.getElementById('radio-bar-title');",
            statusJs,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerAccessibilityHonorsKeyboardFocusMotionAndDialogBehavior()
    {
        var css = ReadWebAsset("styles.css");
        var coreJs = ReadWebAsset("app-core.js");
        var statusJs = ReadWebAsset("app-radio-status.js");

        AssertContainsAll(
            css,
            "):focus-visible{",
            "@media(forced-colors:active)",
            "@media(pointer:coarse)",
            "min-height:44px",
            ".music-queue-remove-btn:focus-visible{opacity:1;transition:none}",
            "#music-player-bar .music-queue-remove-btn{min-width:44px;min-height:44px;opacity:1;transition:none}",
            "@media(prefers-reduced-motion:reduce)",
            "animation-duration:.01ms!important");
        AssertContainsAll(
            statusJs,
            "_radioReducedMotionQuery?.addEventListener('change'",
            "if (event.matches) stopWaveform();");
        Assert.DoesNotContain("songTextEl.style.animation", statusJs, StringComparison.Ordinal);
        AssertContainsAll(
            coreJs,
            "overlay.setAttribute('role', 'alertdialog');",
            "overlay.setAttribute('aria-modal', 'true');",
            "overlay.addEventListener('keydown'",
            "dismissBtn.focus();",
            "if (previousFocus?.isConnected) previousFocus.focus();");
    }

    [Fact]
    public void MusicQueueTransportUpdatesWithoutPendingNoticeElement()
    {
        var appLibraryJs = ReadWebAsset("app-library.js");
        var methodStart = appLibraryJs.IndexOf(
            "function _updateMusicQueuePendingBar() {",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "The Music queue state updater should exist.");

        var methodEnd = appLibraryJs.IndexOf(
            "\nfunction _musicCardContextBind",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "The Music queue state updater should be a complete function.");
        var method = appLibraryJs[methodStart..methodEnd];

        Assert.Contains("if (bar) {", method, StringComparison.Ordinal);
        Assert.Contains("if (navNormal && btnQueueNext)", method, StringComparison.Ordinal);
        Assert.Contains("classList.toggle('queue-mode', hasQueue)", method, StringComparison.Ordinal);
        Assert.False(method.Contains("if (!bar) return;", StringComparison.Ordinal));
    }

    [Fact]
    public void MusicQueueRemoveControlsHaveTrackSpecificAccessibleNames()
    {
        var appLibraryJs = ReadWebAsset("app-library.js");

        Assert.Contains(
            "aria-label=\"Remove ' + esc(item.name || item.path) + ' from queue\"",
            appLibraryJs,
            StringComparison.Ordinal);
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
        Assert.DoesNotContain("rawName.split('-')", appJs, StringComparison.Ordinal);
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

    private static string ReadProjectFile(params string[] pathParts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, Path.Combine(pathParts)));

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
        "app-sse.js",
        "app-core.js",
        "app-diagnostics.js",
        "app-playback.js",
        "app-context-menu.js",
        "app-library.js",
        "app-live-tv.js",
        "app-podcasts.js",
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
