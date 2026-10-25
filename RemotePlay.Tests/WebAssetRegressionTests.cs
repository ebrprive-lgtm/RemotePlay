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
    public void LocalMusicStartsBeforeNetworkCleanupAndPreservesMutedHandoff()
    {
        var localJs = ReadWebAsset("app-local.js");
        var libraryJs = ReadWebAsset("app-library.js").Replace("\r\n", "\n", StringComparison.Ordinal);
        var coreJs = ReadWebAsset("app-core.js");
        var radioJs = ReadWebAsset("app-radio.js");
        var statusJs = ReadWebAsset("app-radio-status.js");
        var localPlayStart = localJs.IndexOf("function localPlay(", StringComparison.Ordinal);
        var localPlayEnd = localJs.IndexOf("async function localToggle()", localPlayStart, StringComparison.Ordinal);
        Assert.True(localPlayStart >= 0 && localPlayEnd > localPlayStart);
        var localPlay = localJs[localPlayStart..localPlayEnd];
        var audioGraph = localPlay.IndexOf("_ensureLocalBoostGraph();", StringComparison.Ordinal);
        var audioStart = localPlay.IndexOf("const playPromise = a.play();", StringComparison.Ordinal);
        var remoteCleanup = localPlay.IndexOf("const remoteStopPromise = _stopRemotePlayersForLocalStart();", StringComparison.Ordinal);
        Assert.True(
            audioGraph >= 0 && audioStart > audioGraph,
            "Local Music must resume its persistent Web Audio graph in the playback gesture.");
        Assert.True(
            audioStart >= 0 && remoteCleanup > audioStart,
            "Local audio must start in the click gesture before any server requests.");
        var toggleStart = localJs.IndexOf("async function localToggle()", StringComparison.Ordinal);
        var toggleEnd = localJs.IndexOf("async function localStop(", toggleStart, StringComparison.Ordinal);
        Assert.True(toggleStart >= 0 && toggleEnd > toggleStart);
        var localToggle = localJs[toggleStart..toggleEnd];
        var toggleGraph = localToggle.IndexOf("_ensureLocalBoostGraph();", StringComparison.Ordinal);
        var togglePlay = localToggle.IndexOf("a.play().catch(() => {});", StringComparison.Ordinal);
        Assert.True(
            toggleGraph >= 0 && togglePlay > toggleGraph,
            "Resuming Local Music must also resume the Web Audio graph in the click gesture.");
        Assert.Contains("a.muted = options.startMuted === true;", localPlay, StringComparison.Ordinal);
        Assert.Contains(
            "playPromise.then(",
            localPlay[remoteCleanup..],
            StringComparison.Ordinal);

        var playMusicStart = libraryJs.IndexOf("async function playMusic(", StringComparison.Ordinal);
        var playMusicEnd = libraryJs.IndexOf("function openArtistWiki()", playMusicStart, StringComparison.Ordinal);
        Assert.True(playMusicStart >= 0 && playMusicEnd > playMusicStart);
        var playMusic = libraryJs[playMusicStart..playMusicEnd];
        Assert.Contains(
            "const isSwitchingServerMusic = !useLocalPlayback && currentMode === 'music' && musicIsPlaying;",
            playMusic,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (isSwitchingServerMusic) stopMusicPlaybackPoll();\n  else if (!useLocalPlayback) await _stopAllPlaybackForNewStart();",
            playMusic,
            StringComparison.Ordinal);
        var localBranchStart = playMusic.IndexOf("if (useLocalPlayback)", StringComparison.Ordinal);
        var localBranchEnd = playMusic.IndexOf("const _inQueue", localBranchStart, StringComparison.Ordinal);
        Assert.True(localBranchStart >= 0 && localBranchEnd > localBranchStart);
        var localBranch = playMusic[localBranchStart..localBranchEnd];
        Assert.DoesNotContain("await fetch('/api/music/stop')", localBranch, StringComparison.Ordinal);
        Assert.Contains(
            "{ startMuted: serverWasPlaying, unmuteAfterServerStop: serverWasPlaying }",
            localBranch,
            StringComparison.Ordinal);
        Assert.Contains("{ startMuted: true }", coreJs, StringComparison.Ordinal);
        AssertContainsAll(
            radioJs,
            "function _resumeLocalBoostContext()",
            "_localBoostCtx.state === 'running'",
            "_localBoostCtx.state === 'closed'",
            "_localBoostCtx.resume().catch((error) =>",
            "if (_localBoostGraphBuilt) {",
            "_resumeLocalBoostContext();");
        AssertContainsAll(
            statusJs,
            "const graphContext = typeof _localBoostCtx !== 'undefined' ? _localBoostCtx : null;",
            "if (graphContext) acState = graphContext.state;");
        Assert.DoesNotContain("new AC()", statusJs, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsProbeTheActiveLocalVideoForTrackInformation()
    {
        var diagnosticsJs = ReadWebAsset("app-diagnostics.js");

        AssertContainsAll(
            diagnosticsJs,
            "const localVideoActive = Boolean(",
            "const query = new URLSearchParams({ path: localVideoState.path });",
            "'/api/video/codec-info?' + query.toString()",
            "Track information unavailable:",
            "The media probe found no video tracks in this file.",
            "The media probe found no audio tracks in this file.",
            "Browser-aware audio compatibility selection requested",
            "Supported audio may be streamed or remuxed; other audio is converted to stereo AAC.",
            "'Browser audio codecs'");
    }

    [Fact]
    public void LocalVideoSurfacePreservesServerPlaybackAndStartsWithinTheUserGesture()
    {
        var html = ReadWebAsset("index.html");
        var libraryJs = ReadWebAsset("app-library.js");
        var playbackJs = ReadWebAsset("app-playback.js");
        var css = ReadWebAsset("styles.css");
        var nowPlayingStart = html.IndexOf("<div id=\"now-playing-bar\">", StringComparison.Ordinal);
        var surfaceStart = html.IndexOf("<section id=\"local-video-surface\"", StringComparison.Ordinal);
        var playerTopStart = nowPlayingStart < 0
            ? -1
            : html.IndexOf("<div class=\"player-top\">", nowPlayingStart, StringComparison.Ordinal);
        var surfaceEnd = surfaceStart < 0 ? -1 : html.IndexOf("</section>", surfaceStart, StringComparison.Ordinal);
        Assert.True(
            nowPlayingStart >= 0 && surfaceStart > nowPlayingStart && surfaceEnd > surfaceStart && playerTopStart > surfaceStart,
            "The Local Video surface should be inside the player dock, above its controls.");

        var surface = html[surfaceStart..surfaceEnd];
        var stageStart = surface.IndexOf("<div id=\"local-video-stage\"", StringComparison.Ordinal);
        var stageEnd = stageStart < 0
            ? -1
            : surface.IndexOf("</div>", stageStart, StringComparison.Ordinal);
        var thumbnailOverlayButton = surface.IndexOf("id=\"local-video-thumbnail-overlay\"", StringComparison.Ordinal);
        Assert.True(
            stageStart >= 0 && thumbnailOverlayButton > stageStart && stageEnd > thumbnailOverlayButton,
            "The fullscreen return overlay must stay inside the element that enters fullscreen.");
        var videoStart = surface.IndexOf("<video", stageStart, StringComparison.Ordinal);
        var videoEnd = videoStart < 0
            ? -1
            : surface.IndexOf("</video>", videoStart, StringComparison.Ordinal);
        Assert.True(videoStart >= 0 && videoEnd > videoStart);
        Assert.DoesNotContain("controls", surface[videoStart..videoEnd], StringComparison.Ordinal);
        AssertContainsAll(
            surface,
            "id=\"local-video-element\"",
            "playsinline",
            "preload=\"metadata\"",
            "aria-label=\"Local video player\"",
            "id=\"local-video-expand-overlay\"",
            "onclick=\"toggleLocalVideoFullscreen()\"",
            "aria-label=\"Expand Local Video to fullscreen\"",
            "id=\"local-video-expand-prompt\"",
            "Tap to expand",
            "id=\"local-video-thumbnail-overlay\"",
            "onclick=\"exitLocalVideoFullscreen()\"",
            "id=\"local-video-thumbnail-prompt\"",
            "id=\"local-video-thumbnail-prompt-label\"",
            "Tap to go to thumbnail",
            "id=\"local-video-transcode-indicator\"",
            "class=\"local-video-transcode-spinner\"",
            "aria-live=\"polite\"",
            "Preparing tablet-compatible audio",
            "id=\"local-video-transcode-cancel\"",
            "onclick=\"cancelLocalVideoTranscode()\"",
            "onclick=\"playLocalVideoOnServer()\"");
        Assert.DoesNotContain("id=\"local-video-fullscreen-exit\"", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("Back to small player", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"local-video-audio-enable\"", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick=\"stop()\"", surface, StringComparison.Ordinal);
        AssertContainsAll(
            css,
            ".local-video-stage{",
            "width:min(100%,320px)",
            "max-height:180px",
            ".local-video-expand-overlay{",
            ".local-video-expand-overlay[hidden]{display:none!important}",
            ".local-video-thumbnail-overlay{",
            ".local-video-thumbnail-overlay[hidden]{display:none!important}",
            ".local-video-transcode-indicator{position:absolute;",
            ".local-video-transcode-indicator[hidden]{display:none!important}",
            "pointer-events:none",
            ".local-video-transcode-spinner{",
            "animation:local-video-transcode-spin .8s linear infinite",
            "@keyframes local-video-transcode-spin{to{transform:rotate(360deg)}}",
            ".local-video-transcode-spinner{animation:none}",
            ".local-video-overlay-prompt.is-faded{opacity:0;transform:scale(.94)}",
            "transition:opacity .45s ease,transform .45s ease",
            "#local-video-stage:fullscreen",
            "#local-video-stage:fullscreen .local-video-expand-overlay",
            "body.local-video-css-fullscreen #local-video-surface{",
            "body.local-video-css-fullscreen #local-video-element{",
            "cursor:zoom-in",
            "cursor:zoom-out");
        Assert.DoesNotContain(".local-video-fullscreen-exit", css, StringComparison.Ordinal);
        AssertContainsAll(
            libraryJs,
            "if (isPlayLocal())",
            "if (_playLocalVideo(p, title, _resolveVideoLinkPath(p, linkPath), {",
            "await api('/api/video/play?path='");

        var localPlayStart = playbackJs.IndexOf("function _playLocalVideo(", StringComparison.Ordinal);
        var localPlayEnd = localPlayStart < 0
            ? -1
            : playbackJs.IndexOf("function stopLocalVideo()", localPlayStart, StringComparison.Ordinal);
        Assert.True(localPlayStart >= 0 && localPlayEnd > localPlayStart, "The Local Video launch flow should exist.");
        var localPlay = playbackJs[localPlayStart..localPlayEnd];
        var mediaPlay = localPlay.IndexOf("video.play()", StringComparison.Ordinal);
        var playbackCleanup = localPlay.IndexOf("_stopAllPlaybackForNewStart()", StringComparison.Ordinal);
        Assert.True(mediaPlay >= 0 && playbackCleanup > mediaPlay,
            "Local video must start before asynchronous playback cleanup to preserve browser user activation.");
        Assert.Contains("'/api/video/stream?' + query.toString()", localPlay, StringComparison.Ordinal);

        var cleanupRestoreStart = localPlay.IndexOf(".then(() => {", playbackCleanup, StringComparison.Ordinal);
        var cleanupRestoreEnd = cleanupRestoreStart < 0
            ? -1
            : localPlay.IndexOf(".catch((error) => {", cleanupRestoreStart, StringComparison.Ordinal);
        Assert.True(
            cleanupRestoreStart > playbackCleanup && cleanupRestoreEnd > cleanupRestoreStart,
            "Local playback cleanup should restore the player dock before marking the stream active.");
        AssertContainsAll(
            localPlay[cleanupRestoreStart..cleanupRestoreEnd],
            "if (_localVideoState?.generation !== generation) return;",
            "_syncLocalVideoSurfaceVisibility();",
            "fetch('/api/local-playing', { method: 'POST', body: 'true' })");

        var serverPlayStart = libraryJs.IndexOf("async function play(p, name, linkPath)", StringComparison.Ordinal);
        var localRouteStart = libraryJs.IndexOf("if (isPlayLocal())", serverPlayStart, StringComparison.Ordinal);
        var serverRouteStart = libraryJs.IndexOf("await api('/api/video/play?path='", localRouteStart, StringComparison.Ordinal);
        Assert.True(serverPlayStart >= 0 && localRouteStart > serverPlayStart && serverRouteStart > localRouteStart);
        var localRoute = libraryJs[localRouteStart..serverRouteStart];
        Assert.Contains("_playLocalVideo(", localRoute, StringComparison.Ordinal);
        Assert.Contains("return;", localRoute, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/video/play", localRoute, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalVideoExpandOverlayOpensFullscreenAndEnablesNativeControlsThere()
    {
        var html = ReadWebAsset("index.html");
        var playbackJs = ReadWebAsset("app-playback.js");
        var fullscreenStateStart = playbackJs.IndexOf(
            "function _renderLocalVideoFullscreenState()",
            StringComparison.Ordinal);
        var fullscreenStart = playbackJs.IndexOf(
            "async function toggleLocalVideoFullscreen()",
            fullscreenStateStart,
            StringComparison.Ordinal);
        var resumeStart = fullscreenStart < 0
            ? -1
            : playbackJs.IndexOf("function updateResumePrompt", fullscreenStart, StringComparison.Ordinal);
        Assert.True(
            fullscreenStateStart >= 0 && fullscreenStart > fullscreenStateStart && resumeStart > fullscreenStart);
        AssertContainsAll(
            html,
            "id=\"local-video-expand-overlay\"",
            "onclick=\"toggleLocalVideoFullscreen()\"");

        var fullscreenStateHandler = playbackJs[fullscreenStateStart..fullscreenStart];
        AssertContainsAll(
            fullscreenStateHandler,
            "video.controls = isFullscreen;",
            "expandOverlay.hidden = isFullscreen;",
            "thumbnailOverlay.hidden = !isFullscreen;");
        Assert.DoesNotContain("exitButton", fullscreenStateHandler, StringComparison.Ordinal);
        AssertContainsAll(
            playbackJs,
            "const LOCAL_VIDEO_OVERLAY_AUTOHIDE_MS = 1500;",
            "function _showLocalVideoOverlayPrompt()",
            "prompt.classList.add('is-faded');",
            "stage?.addEventListener('pointerdown', _showLocalVideoOverlayPrompt, { passive: true });",
            "stage?.addEventListener('pointermove', _showLocalVideoOverlayPrompt, { passive: true });");
        AssertContainsAll(
            playbackJs,
            "function _renderLocalVideoFullscreenOverlayStatus(state = _localVideoState)",
            "const isTranscoding = Boolean(",
            "isFullscreen &&",
            "state?.transcodeProgress?.isTranscoding",
            "!state.transcodeProgress.isComplete",
            "transcodeIndicator.hidden = !isTranscoding;",
            "promptLabel.textContent = 'Tap to go to thumbnail';",
            "thumbnailOverlay.setAttribute(",
            "Return to the Local Video thumbnail");
        var overlayStatusStart = playbackJs.IndexOf(
            "function _renderLocalVideoFullscreenOverlayStatus(",
            StringComparison.Ordinal);
        var overlayStatusEnd = playbackJs.IndexOf(
            "function _renderLocalVideoFullscreenState()",
            overlayStatusStart,
            StringComparison.Ordinal);
        Assert.True(overlayStatusStart >= 0 && overlayStatusEnd > overlayStatusStart);
        Assert.DoesNotContain(
            "_formatLocalVideoTranscodeStatus(",
            playbackJs[overlayStatusStart..overlayStatusEnd],
            StringComparison.Ordinal);
        var overlayPromptStart = playbackJs.IndexOf(
            "function _showLocalVideoOverlayPrompt()",
            StringComparison.Ordinal);
        var overlayPromptEnd = playbackJs.IndexOf(
            "function _renderLocalVideoFullscreenOverlayStatus(",
            overlayPromptStart,
            StringComparison.Ordinal);
        Assert.True(overlayPromptStart >= 0 && overlayPromptEnd > overlayPromptStart);
        Assert.DoesNotContain(
            "transcodeProgress",
            playbackJs[overlayPromptStart..overlayPromptEnd],
            StringComparison.Ordinal);

        var fullscreenHandler = playbackJs[fullscreenStart..resumeStart];
        AssertContainsAll(
            fullscreenHandler,
            "if (_isLocalVideoFullscreen(stage, video))",
            "await document.exitFullscreen();",
            "await stage.requestFullscreen();",
            "document.body.classList.add('local-video-css-fullscreen')",
            "async function exitLocalVideoFullscreen()",
            "video.webkitExitFullscreen();");
        Assert.DoesNotContain("handleLocalVideoClick", playbackJs, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerToLocalHandoffKeepsCompactPlayerExpandable()
    {
        var playbackJs = ReadWebAsset("app-playback.js");
        var css = ReadWebAsset("styles.css");

        var syncStart = playbackJs.IndexOf("function _syncLocalVideoModeToggle()", StringComparison.Ordinal);
        var syncEnd = syncStart < 0
            ? -1
            : playbackJs.IndexOf("function _syncLocalVideoSurfaceVisibility()", syncStart, StringComparison.Ordinal);
        Assert.True(syncStart >= 0 && syncEnd > syncStart);
        AssertContainsAll(
            playbackJs[syncStart..syncEnd],
            "document.body.classList.toggle('local-video-handoff-pending', _localVideoHandoffPending);",
            "'local-video-server-handoff-pending',",
            "_localVideoHandoffPending && _localVideoState?.handoff === 'server-to-local'");
        AssertContainsAll(
            css,
            "body.local-video-handoff-pending:not(.local-video-server-handoff-pending) #local-video-stage,",
            "body.local-video-handoff-pending #transport-controls,");
    }

    [Fact]
    public void ServerToLocalHandoffStartsFullscreenAfterLocalPlayerSetup()
    {
        var playbackJs = ReadWebAsset("app-playback.js");
        var handoffStart = playbackJs.IndexOf("function _handoffServerVideoToLocal()", StringComparison.Ordinal);
        var handoffEnd = handoffStart < 0
            ? -1
            : playbackJs.IndexOf(
                "async function _handoffLocalVideoToServer(forcePlay = false)",
                handoffStart,
                StringComparison.Ordinal);
        Assert.True(handoffStart >= 0 && handoffEnd > handoffStart);

        var handoff = playbackJs[handoffStart..handoffEnd];
        var playStart = handoff.IndexOf("const started = _playLocalVideo(", StringComparison.Ordinal);
        var failedStart = playStart < 0
            ? -1
            : handoff.IndexOf("if (!started || !_localVideoState)", playStart, StringComparison.Ordinal);
        var fullscreenStart = failedStart < 0
            ? -1
            : handoff.IndexOf("void toggleLocalVideoFullscreen();", failedStart, StringComparison.Ordinal);

        Assert.True(
            playStart >= 0 && failedStart > playStart && fullscreenStart > failedStart,
            "A successful Server-to-Local handoff must enter fullscreen after setting up the Local player.");
    }

    [Fact]
    public void LocalVideoTranscodeCancellationStopsPlaybackAndPreservesTouchCompatibility()
    {
        var playbackJs = ReadWebAsset("app-playback.js");

        AssertContainsAll(
            playbackJs,
            "query.set('audioCompat', 'auto');",
            "query.set('audioCodecs', supportedAudioCodecs.join(','));",
            "query.set('playbackId', playbackTelemetryId);",
            "function _sendLocalVideoTiming(state, eventName, source = '')",
            "video.requestVideoFrameCallback",
            "_sendLocalVideoTiming(state, 'loadeddata');",
            "'[LOCAL-PLAYBACK] id='",
            "function _getLocalVideoSupportedAudioCodecs(video)",
            "audioCompatibilityRequested: touchClient",
            "state.audioCompatibilityRequested",
            "The tablet-compatible Local stream failed. Check FFmpeg on the RemotePlay host, or choose Play on Server if the video codec is unsupported.",
            "Number(navigator.maxTouchPoints) > 0",
            "async function cancelLocalVideoTranscode()",
            "!state.transcodeProgress?.isTranscoding",
            "state.transcodeCancelPending = true;",
            "function _requestLocalVideoTranscodeCancellation(state)",
            "void _cancelLocalVideoTranscodeOnServer(state).catch((error) =>",
            "'/api/video/transcode-cancel?'",
            "if (!state?.path || mediaError?.code === 1 || state.transcodeCancelPending) return;");
        Assert.DoesNotContain("function enableLocalVideoAudio()", playbackJs, StringComparison.Ordinal);
        Assert.DoesNotContain("audioGestureRequired", playbackJs, StringComparison.Ordinal);

        var cancelStart = playbackJs.IndexOf("async function cancelLocalVideoTranscode()", StringComparison.Ordinal);
        var cancelEnd = cancelStart < 0
            ? -1
            : playbackJs.IndexOf("async function _pollLocalVideoTranscodeStatus", cancelStart, StringComparison.Ordinal);
        Assert.True(cancelStart >= 0 && cancelEnd > cancelStart);
        var cancelHandler = playbackJs[cancelStart..cancelEnd];
        var requestStart = cancelHandler.IndexOf(
            "const cancellationRequest = _cancelLocalVideoTranscodeOnServer(state).then(",
            StringComparison.Ordinal);
        var stopPlayback = requestStart < 0
            ? -1
            : cancelHandler.IndexOf("await stop();", requestStart, StringComparison.Ordinal);
        var awaitCancellation = stopPlayback < 0
            ? -1
            : cancelHandler.IndexOf(
                "const cancellationError = await cancellationRequest;",
                stopPlayback,
                StringComparison.Ordinal);
        Assert.True(
            requestStart >= 0 && stopPlayback > requestStart && awaitCancellation > stopPlayback,
            "The Local player should stop promptly while the server transcode cancellation completes.");

        var stopStart = playbackJs.IndexOf("function stopLocalVideo()", StringComparison.Ordinal);
        var stopEnd = stopStart < 0
            ? -1
            : playbackJs.IndexOf("function _pauseLocalVideoForNavigation()", stopStart, StringComparison.Ordinal);
        Assert.True(stopStart >= 0 && stopEnd > stopStart);
        Assert.Contains(
            "_requestLocalVideoTranscodeCancellation(state);",
            playbackJs[stopStart..stopEnd],
            StringComparison.Ordinal);
    }

    [Fact]
    public void LocalVideoCodecPreflightIsNonBlockingAndKeepsServerFallbackVisible()
    {
        var playbackJs = ReadWebAsset("app-playback.js");

        AssertContainsAll(
            playbackJs,
            "function _localVideoCompatibilityWarning(video, mediaName)",
            "video.canPlayType(mimeType)",
            "Playback will still be attempted; choose Play on Server if it fails.",
            "audio/mp4; codecs=\"mp4a.40.2\"",
            "video.canPlayType(mimeType)",
            "compatibilityWarning: touchClient ? '' : _localVideoCompatibilityWarning(video, name || path)",
            "Checking browser audio codec support…",
            "Transcoding audio to AAC…",
            "fallback.hidden = !state.errorMessage && !state.compatibilityWarning;",
            "video.addEventListener('error'",
            "This browser cannot play this video format or codec. Choose Play on Server.");

        var localPlayStart = playbackJs.IndexOf("function _playLocalVideo(", StringComparison.Ordinal);
        var localPlayEnd = localPlayStart < 0
            ? -1
            : playbackJs.IndexOf("function stopLocalVideo()", localPlayStart, StringComparison.Ordinal);
        Assert.True(localPlayStart >= 0 && localPlayEnd > localPlayStart);
        var localPlay = playbackJs[localPlayStart..localPlayEnd];
        var sourceAssignment = localPlay.IndexOf("video.src = source;", StringComparison.Ordinal);
        var playAttempt = localPlay.IndexOf("video.play()", StringComparison.Ordinal);
        Assert.True(sourceAssignment >= 0 && playAttempt > sourceAssignment,
            "A negative support hint must not prevent trying the unchanged source.");
    }

    [Fact]
    public void LocalVideoTranscodeEstimateIsPolledAndShownInDiagnostics()
    {
        var playbackJs = ReadWebAsset("app-playback.js");
        var diagnosticsJs = ReadWebAsset("app-diagnostics.js");

        AssertContainsAll(
            playbackJs,
            "function _formatLocalVideoTranscodeStatus(progress, unavailable = false)",
            "progress.mode === 'finalizing'",
            "'Finalizing seekable video cache…'",
            "progress.progressPercent",
            "progress.estimatedRemainingSeconds",
            "'/api/video/transcode-status?' + query.toString()",
            "state.transcodeProgress = progress;",
            "_scheduleLocalVideoTranscodeStatusPoll(_localVideoState)",
            "_cancelLocalVideoTranscodeStatusPoll(state)",
            "'about ' + _formatLocalVideoTime(Math.ceil(progress.estimatedRemainingSeconds))");
        AssertContainsAll(
            diagnosticsJs,
            "const localVideoTranscodeStatus =",
            "rows.push(['Local transcode', localVideoTranscodeStatus]);",
            "esc(localVideoTranscodeStatus)");
    }

    [Fact]
    public void LocalVideoHeaderOmitsRedundantPlayingLocallyLabel()
    {
        var playbackJs = ReadWebAsset("app-playback.js");
        var renderStart = playbackJs.IndexOf("function _renderLocalVideoUi()", StringComparison.Ordinal);
        var statusStart = renderStart < 0
            ? -1
            : playbackJs.IndexOf("if (status) {", renderStart, StringComparison.Ordinal);
        var statusEnd = statusStart < 0
            ? -1
            : playbackJs.IndexOf("if (time) {", statusStart, StringComparison.Ordinal);
        Assert.True(renderStart >= 0 && statusStart > renderStart && statusEnd > statusStart);

        var headerStatus = playbackJs[statusStart..statusEnd];
        Assert.DoesNotContain("Playing locally", headerStatus, StringComparison.Ordinal);
        AssertContainsAll(headerStatus, "transcodeStatus", "? 'Paused'", ": '';");
    }

    [Fact]
    public void ProgressiveLocalVideoKeepsTranscodeStatusAndRestrictsUnbufferedSeeking()
    {
        var playbackJs = ReadWebAsset("app-playback.js");
        var diagnosticsJs = ReadWebAsset("app-diagnostics.js");
        var pollStart = playbackJs.IndexOf(
            "function _shouldPollLocalVideoTranscodeStatus(state)",
            StringComparison.Ordinal);
        var pollEnd = pollStart < 0
            ? -1
            : playbackJs.IndexOf("function _isLocalVideoProgressiveTranscodeActive", pollStart, StringComparison.Ordinal);
        Assert.True(pollStart >= 0 && pollEnd > pollStart);

        AssertContainsAll(
            playbackJs,
            "query.set('progressive', progressiveRequested ? '1' : '0');",
            "query.set('transcodeId', transcodeId);",
            "progressiveRequested,",
            "transcodeId,",
            "if (progress?.errorMessage)",
            "function _canSeekLocalVideoTo(video, state, position)",
            "if (eventName === 'seeking' && _rejectUnbufferedLocalVideoSeek(video, state))",
            "progress?.isTranscoding &&",
            "Seeking outside the buffered video is available after audio conversion completes.");
        Assert.DoesNotContain("state.loading", playbackJs[pollStart..pollEnd], StringComparison.Ordinal);
        Assert.Contains("localVideoState.transcodeProgress?.isTranscoding", diagnosticsJs, StringComparison.Ordinal);
        Assert.Contains("Playing progressively · ", diagnosticsJs, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalVideoTranscodePollingStopsWhenNoConversionIsRequired()
    {
        var playbackJs = ReadWebAsset("app-playback.js");

        AssertContainsAll(
            playbackJs,
            "!state.transcodeStatusResolved",
            "state.transcodeStatusResolved = Boolean(",
            "state.localVideoMediaReady = true;",
            "state.transcodeStatusNoConversionResponses >= 2",
            "progress?.isComplete ||",
            "progress?.errorMessage ||",
            "const noConversionReported = Boolean(",
            "!progress.isTranscoding &&");
    }

    [Fact]
    public void LocalVideoStatusPollingPreservesBrowserPlaybackState()
    {
        var coreJs = ReadWebAsset("app-core.js");
        var pollStart = coreJs.IndexOf("async function pollStatus()", StringComparison.Ordinal);
        var ownershipStart = coreJs.IndexOf("const localVideoOwnsPlayback =", pollStart, StringComparison.Ordinal);
        var localBranchStart = coreJs.IndexOf("if (localVideoOwnsPlayback)", ownershipStart, StringComparison.Ordinal);
        var serverUiStart = coreJs.IndexOf(
            "const bar = document.getElementById('now-playing-bar');",
            localBranchStart,
            StringComparison.Ordinal);

        Assert.True(
            pollStart >= 0
            && ownershipStart > pollStart
            && localBranchStart > ownershipStart
            && serverUiStart > localBranchStart,
            "Local playback status must be handled before server player UI updates.");

        var localBranch = coreJs[localBranchStart..serverUiStart];
        AssertContainsAll(
            localBranch,
            "updateQueueControls({ queue: localQueue });",
            "_syncLocalVideoAdjacentButtons();",
            "_renderLocalVideoUi();",
            "return s;");
        Assert.DoesNotContain("progress.value", localBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("time-label", localBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("player-title", localBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalVideoTransportControlsUseTheHtmlMediaElementAndKeepServerCommands()
    {
        var html = ReadWebAsset("index.html");
        var playbackJs = ReadWebAsset("app-playback.js");
        var libraryJs = ReadWebAsset("app-library.js");
        Assert.Contains(
            "onpointerdown=\"onSeekDrag()\" onpointerup=\"onSeekCommit()\" ontouchend=\"onSeekCommit()\"",
            html,
            StringComparison.Ordinal);

        var seekStart = playbackJs.IndexOf("async function onSeekCommit()", StringComparison.Ordinal);
        var seekEnd = playbackJs.IndexOf("function updateVolumeIcon", seekStart, StringComparison.Ordinal);
        Assert.True(seekStart >= 0 && seekEnd > seekStart);
        var seek = playbackJs[seekStart..seekEnd];
        AssertContainsAll(
            seek,
            "if (!seekDragging) return;",
            "if (_isLocalVideoActive())",
            "const target = Math.min(Math.max(0, pos), duration);",
            "if (!_canSeekLocalVideoTo(video, _localVideoState, target))",
            "video.currentTime = target;",
            "await api('/api/seek?pos='",
            "finally {",
            "seekDragging = false;");

        var pauseStart = playbackJs.IndexOf("async function togglePause()", StringComparison.Ordinal);
        var pauseEnd = playbackJs.IndexOf("async function skip(", pauseStart, StringComparison.Ordinal);
        Assert.True(pauseStart >= 0 && pauseEnd > pauseStart);
        var pause = playbackJs[pauseStart..pauseEnd];
        AssertContainsAll(pause, "_isLocalVideoActive()", "video.play()", "video.pause();", "await api('/api/pause');");

        var skipStart = playbackJs.IndexOf("async function skip(", StringComparison.Ordinal);
        var skipEnd = playbackJs.IndexOf("async function quickSkip(", skipStart, StringComparison.Ordinal);
        Assert.True(skipStart >= 0 && skipEnd > skipStart);
        var skip = playbackJs[skipStart..skipEnd];
        AssertContainsAll(skip, "_isLocalVideoActive()", "video.currentTime =", "await api('/api/skip?seconds='");

        var volumeStart = playbackJs.IndexOf("async function setVolume(value)", StringComparison.Ordinal);
        var volumeEnd = playbackJs.IndexOf("async function toggleVolumeMute()", volumeStart, StringComparison.Ordinal);
        Assert.True(volumeStart >= 0 && volumeEnd > volumeStart);
        var volume = playbackJs[volumeStart..volumeEnd];
        AssertContainsAll(
            volume,
            "_isLocalVideoActive()",
            "video.volume = volume;",
            "video.muted = volume <= 0.001;",
            "await api('/api/volume?value='");

        var stopStart = libraryJs.IndexOf("async function stop()", StringComparison.Ordinal);
        var stopEnd = libraryJs.IndexOf("function loadPlayedVideos()", stopStart, StringComparison.Ordinal);
        Assert.True(stopStart >= 0 && stopEnd > stopStart);
        var stop = libraryJs[stopStart..stopEnd];
        AssertContainsAll(stop, "_localVideoHasSource()", "stopLocalVideo();", "await api('/api/stop');");

        var localStopStart = stop.IndexOf("if (typeof _localVideoHasSource", StringComparison.Ordinal);
        var localStopPoll = localStopStart < 0
            ? -1
            : stop.IndexOf("await pollStatus();", localStopStart, StringComparison.Ordinal);
        Assert.True(localStopStart >= 0 && localStopPoll > localStopStart);
        var localStop = stop[localStopStart..(localStopPoll + "await pollStatus();".Length)];
        AssertContainsAll(localStop, "stopLocalVideo();", "api('/api/stop')", "fetch('/api/local-playing'");
        Assert.DoesNotContain("setPlayLocal(false)", localStop, StringComparison.Ordinal);
    }

    [Fact]
    public void SeekHoldControlsDeclareTheirTimerAndGestureState()
    {
        var coreJs = ReadWebAsset("app-core.js");
        var playbackJs = ReadWebAsset("app-playback.js");

        AssertContainsAll(
            coreJs,
            "let seekHoldTimer = null,",
            "seekHoldInterval = null,",
            "seekHoldTriggered = false,",
            "suppressNextSeekTap = false;");
        AssertContainsAll(
            playbackJs,
            "function beginSeekHold(event, step)",
            "clearTimeout(seekHoldTimer);",
            "clearInterval(seekHoldInterval);",
            "function endSeekHold()");
    }

    [Fact]
    public void LocalVideoRuntimeErrorsExplainFailureAndKeepTheServerFallbackAvailable()
    {
        var playbackJs = ReadWebAsset("app-playback.js");
        var messageStart = playbackJs.IndexOf("function _localVideoErrorMessage(error)", StringComparison.Ordinal);
        var messageEnd = playbackJs.IndexOf("function _showLocalVideoError", messageStart, StringComparison.Ordinal);
        Assert.True(messageStart >= 0 && messageEnd > messageStart);
        var message = playbackJs[messageStart..messageEnd];
        AssertContainsAll(
            message,
            "NotAllowedError",
            "NotSupportedError",
            "case 2:",
            "case 3:",
            "case 4:",
            "This browser does not support this video format or codec. Choose Play on Server.");

        var errorStart = playbackJs.IndexOf("video.addEventListener('error', () => {", StringComparison.Ordinal);
        var errorEnd = playbackJs.IndexOf("document.addEventListener('fullscreenchange'", errorStart, StringComparison.Ordinal);
        Assert.True(errorStart >= 0 && errorEnd > errorStart);
        var errorHandler = playbackJs[errorStart..errorEnd];
        AssertContainsAll(
            errorHandler,
            "if (!state?.path || mediaError?.code === 1 || state.transcodeCancelPending) return;",
            "state.loading = false;",
            "_showLocalVideoError(_localVideoErrorMessage(mediaError), state.generation);");
        Assert.Contains(
            "fallback.hidden = !state.errorMessage && !state.compatibilityWarning;",
            playbackJs,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LocalVideoHandoffKeepsControlsAndQueueClientScoped()
    {
        var coreJs = ReadWebAsset("app-core.js");
        var libraryJs = ReadWebAsset("app-library.js");
        var playbackJs = ReadWebAsset("app-playback.js");
        var diagnosticsJs = ReadWebAsset("app-diagnostics.js");

        AssertContainsAll(
            playbackJs,
            "function _handoffServerVideoToLocal()",
            "async function _handoffLocalVideoToServer(forcePlay = false)",
            "await _handoffLocalVideoToServer(true)",
            "if ((status.isPlaying || status.isPaused) && status.filePath === state.path)",
            "const LOCAL_VIDEO_COMPATIBILITY_HANDOFF_TIMEOUT_MS = 10 * 60 * 1000;",
            "_waitForLocalVideoReady(generation, readinessTimeout)",
            "await _waitForServerPlaybackStatus(",
            "'/api/video/play?path=' + encodeURIComponent(path) + '&pos='",
            "(next) => !next.isPlaying && !next.isPaused",
            "(status) => (status.isPlaying || status.isPaused) && status.filePath === path",
            "state.loading && state.wasPlayingBeforeHandoff",
            "if (_isLocalVideoActive())",
            "if (eventName === 'ended') void _advanceLocalVideoAfterEnd();",
            "function _consumeLocalVideoQueueItem(path)",
            "const LOCAL_VIDEO_HANDOFF_REQUEST_TIMEOUT_MS = 8000;",
            "const LOCAL_VIDEO_STATUS_REQUEST_TIMEOUT_MS = 2500;",
            "controller?.abort();",
            "return await Promise.race([request, timeout]);",
            "Math.min(LOCAL_VIDEO_STATUS_REQUEST_TIMEOUT_MS, remainingMs)",
            "void pollStatus().catch((error) => {",
            "status.textContent = _localVideoHandoffPending",
            "Switching to Server playback…",
            "if (wasPlaying && serverStoppedSafely)",
            "The server could not be stopped safely. Local Video is paused and muted to prevent duplicate audio;",
            "Local Video is paused and muted because server playback may still be active.");
        AssertContainsAll(diagnosticsJs, "localVideoHandoffStatus", "'Switching to Local'", "'Switching to Server'");
        AssertContainsAll(
            coreJs,
            "const localQueue = Array.isArray(s.queue)",
            "_syncLocalVideoAdjacentButtons();",
            "_resolveLocalVideoServerHandoffStatus(s)",
            "const playbackActive = Boolean(s.isPlaying || s.isPaused);",
            "updateStopBtn(playbackActive, isLiveTvMode ? false : hasQueue);",
            "if (playbackActive) {");

        var localQueueStart = libraryJs.IndexOf("async function playQueueStart()", StringComparison.Ordinal);
        var localQueueEnd = localQueueStart < 0
            ? -1
            : libraryJs.IndexOf("async function stop()", localQueueStart, StringComparison.Ordinal);
        Assert.True(localQueueStart >= 0 && localQueueEnd > localQueueStart, "The queue-start flow should exist.");
        var localQueue = libraryJs[localQueueStart..localQueueEnd];
        var localQueueBranchStart = localQueue.IndexOf("if (isPlayLocal())", StringComparison.Ordinal);
        var localQueueBranchEnd = localQueue.IndexOf("  startSse();", localQueueBranchStart, StringComparison.Ordinal);
        Assert.True(
            localQueueBranchStart >= 0 && localQueueBranchEnd > localQueueBranchStart,
            "The Local queue-start branch should remain separate from Server playback.");
        var localQueueBranch = localQueue[localQueueBranchStart..localQueueBranchEnd];
        Assert.Contains("_playLocalVideo(", localQueueBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/adjacent", localQueueBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerToLocalHandoffStopsAtTranscodeStartAndOnlyOnce()
    {
        var playbackJs = ReadWebAsset("app-playback.js");
        var pollStart = playbackJs.IndexOf("async function _pollLocalVideoTranscodeStatus", StringComparison.Ordinal);
        var pollEnd = pollStart < 0
            ? -1
            : playbackJs.IndexOf("function _isLocalVideoFullscreen", pollStart, StringComparison.Ordinal);
        Assert.True(pollStart >= 0 && pollEnd > pollStart);
        var poll = playbackJs[pollStart..pollEnd];
        AssertContainsAll(
            poll,
            "progress?.isTranscoding",
            "state.handoff === 'server-to-local'",
            "_stopServerPlaybackForLocalHandoff(state)");

        var stopStart = playbackJs.IndexOf("function _stopServerPlaybackForLocalHandoff(state)", StringComparison.Ordinal);
        var handoffStart = stopStart < 0
            ? -1
            : playbackJs.IndexOf("function _handoffServerVideoToLocal()", stopStart, StringComparison.Ordinal);
        Assert.True(stopStart >= 0 && handoffStart > stopStart);
        var stop = playbackJs[stopStart..handoffStart];
        AssertContainsAll(
            stop,
            "if (state.serverStopPromise) return state.serverStopPromise;",
            "state.serverStopRequestStarted = true;",
            "state.serverStopConfirmed = true;");
        Assert.Equal(1, stop.Split("'/api/stop'", StringSplitOptions.None).Length - 1);

        var localHandoffStart = playbackJs.IndexOf(
            "async function _handoffLocalVideoToServer(forcePlay = false)",
            handoffStart,
            StringComparison.Ordinal);
        Assert.True(localHandoffStart > handoffStart);
        var serverHandoff = playbackJs[handoffStart..localHandoffStart];
        var localReadyIndex = serverHandoff.IndexOf("state.localReadyForHandoff = true;", StringComparison.Ordinal);
        var stopAwaitIndex = serverHandoff.IndexOf("await _stopServerPlaybackForLocalHandoff(state);", StringComparison.Ordinal);
        Assert.True(localReadyIndex >= 0 && stopAwaitIndex > localReadyIndex);
        AssertContainsAll(
            serverHandoff,
            "Promise.race([",
            "handoffFailure.then((error) => ({ error }))",
            "_failServerToLocalHandoff(generation, error)");
        Assert.Contains(
            "if (!state.localReadyForHandoff) return;",
            playbackJs,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.serverToLocalHandoffFailed && state.wasPlayingBeforeHandoff",
            playbackJs[localHandoffStart..],
            StringComparison.Ordinal);
        AssertContainsAll(
            playbackJs[localHandoffStart..],
            "serverHandoffVolume",
            "serverHandoffPlaybackSpeed",
            "localIsMuted ? 0 : serverVolume");
    }

    [Fact]
    public void PausedServerToLocalHandoffDoesNotTreatItsStartupAsPlayback()
    {
        var playbackJs = ReadWebAsset("app-playback.js");
        var eventStart = playbackJs.IndexOf("if (eventName === 'play')", StringComparison.Ordinal);
        var eventEnd = eventStart < 0
            ? -1
            : playbackJs.IndexOf("if (eventName === 'timeupdate')", eventStart, StringComparison.Ordinal);
        Assert.True(eventStart >= 0 && eventEnd > eventStart, "The Local Video play event handler should exist.");

        var playEvent = playbackJs[eventStart..eventEnd];
        var handoffGuard = playEvent.IndexOf("if (state.handoff !== 'server-to-local')", StringComparison.Ordinal);
        var playbackStateUpdate = playEvent.IndexOf("state.wasPlayingBeforeHandoff = true;", StringComparison.Ordinal);
        Assert.True(
            handoffGuard >= 0 && playbackStateUpdate > handoffGuard,
            "The startup play event must not overwrite a paused Server-to-Local handoff.");

        var serverHandoffStart = playbackJs.IndexOf("function _handoffServerVideoToLocal()", StringComparison.Ordinal);
        var localHandoffStart = serverHandoffStart < 0
            ? -1
            : playbackJs.IndexOf(
                "async function _handoffLocalVideoToServer(forcePlay = false)",
                serverHandoffStart,
                StringComparison.Ordinal);
        Assert.True(serverHandoffStart >= 0 && localHandoffStart > serverHandoffStart);
        Assert.Contains(
            "if (!state.wasPlayingBeforeHandoff) video.pause();",
            playbackJs[serverHandoffStart..localHandoffStart],
            StringComparison.Ordinal);
    }

    [Fact]
    public void LocalVideoProgressAndResumeUseTheBrowserPlaybackPosition()
    {
        var playbackJs = ReadWebAsset("app-playback.js");
        var libraryJs = ReadWebAsset("app-library.js");
        var css = ReadWebAsset("styles.css");

        AssertContainsAll(
            playbackJs,
            "const LOCAL_VIDEO_PROGRESS_SAVE_INTERVAL_MS = 15000;",
            "function _saveLocalVideoProgress(state, position, duration, options = {})",
            "'/api/video/local-progress?' + query.toString()",
            "addEventListener('pagehide'",
            "completed: true",
            "function _showLocalVideoResumePrompt(state)",
            "video.currentTime = Math.min(position, duration)",
            "video.currentTime = 0",
            "await fetch('/api/history/clear?' + clearQuery.toString())");
        Assert.DoesNotContain("body.local-video-active #resume-card", css, StringComparison.Ordinal);

        var localPlayStart = playbackJs.IndexOf("function _playLocalVideo(", StringComparison.Ordinal);
        var localPlayEnd = localPlayStart < 0
            ? -1
            : playbackJs.IndexOf("function stopLocalVideo()", localPlayStart, StringComparison.Ordinal);
        Assert.True(localPlayStart >= 0 && localPlayEnd > localPlayStart);
        Assert.DoesNotContain("markPlayed(path);", playbackJs[localPlayStart..localPlayEnd], StringComparison.Ordinal);

        var serverPlayStart = libraryJs.IndexOf("async function play(p, name, linkPath)", StringComparison.Ordinal);
        var serverPlayEnd = serverPlayStart < 0
            ? -1
            : libraryJs.IndexOf("function setPlayerPoster", serverPlayStart, StringComparison.Ordinal);
        Assert.True(serverPlayStart >= 0 && serverPlayEnd > serverPlayStart);
        Assert.DoesNotContain("markPlayed(p);", libraryJs[serverPlayStart..serverPlayEnd], StringComparison.Ordinal);
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
        var musicSeekStart = libraryJs.IndexOf("async function onMusicSeekCommit()", StringComparison.Ordinal);
        var musicSeekEnd = libraryJs.IndexOf("function toggleMusicAutoPlay()", musicSeekStart, StringComparison.Ordinal);
        Assert.True(musicSeekStart >= 0 && musicSeekEnd > musicSeekStart);
        AssertContainsAll(
            libraryJs[musicSeekStart..musicSeekEnd],
            "if (isPlayLocal() && typeof _localMediaType !== 'undefined' && _localMediaType === 'Music')",
            "a.currentTime = pos;",
            "Could not seek Local Music playback.");
        AssertContainsAll(
            statusJs,
            "const statusDuration = Number(window._lastMusicBarState?.duration);",
            "const metadataDuration = Number(window._lastMusicBarState?.durationSec);",
            "const musicDuration = dur > 0",
            "musicProgressRow.style.display = musicDuration > 0 ? 'grid' : 'none';",
            "mSeek.disabled = musicDuration <= 0;",
            "mSeek.max = musicDuration;");
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
    public void MusicQueuePeekShowsTrackDurationsAndPreservesManualScroll()
    {
        var appLibraryJs = ReadWebAsset("app-library.js").Replace("\r\n", "\n", StringComparison.Ordinal);
        var musicHandler = ReadProjectFile("Services", "Web", "WebServer.Handlers.MusicLibrary.cs");
        var css = ReadWebAsset("styles.css");
        var methodStart = appLibraryJs.IndexOf("function _renderMusicQueuePeek() {", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "The Music queue renderer should exist.");
        var methodEnd = appLibraryJs.IndexOf("\nfunction toggleMusicRepeat()", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "The Music queue renderer should be a complete function.");
        var method = appLibraryJs[methodStart..methodEnd];
        var playlistStart = musicHandler.IndexOf("private void HandleMusicPlaylist(", StringComparison.Ordinal);
        Assert.True(playlistStart >= 0, "The Music playlist handler should exist.");
        var playlistEnd = musicHandler.IndexOf("private void HandleMusicDynamicCreate(", playlistStart, StringComparison.Ordinal);
        Assert.True(playlistEnd > playlistStart, "The Music playlist handler should be a complete method.");
        var playlistHandler = musicHandler[playlistStart..playlistEnd];

        AssertContainsAll(
            method,
            "const durationSec = _musicQueuePeekDurationSec(item);",
            "class=\"music-queue-peek-dur\"",
            "playingRect.top - listRect.top + list.scrollTop",
            "list._musicQueueAutoScrollItem !== playingItem");
        Assert.DoesNotContain("playingEl.offsetTop", method, StringComparison.Ordinal);
        AssertContainsAll(
            appLibraryJs,
            "durationSec: _musicQueuePeekDurationSec(f)",
            "durationSec: _musicQueuePeekDurationSec(t)");
        Assert.Contains("tracks.Add(BuildMusicFileObj(track, name));", playlistHandler, StringComparison.Ordinal);
        Assert.Contains("durationSec", musicHandler, StringComparison.Ordinal);
        Assert.Contains("min-width:2.7rem;text-align:right", css, StringComparison.Ordinal);
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
    public void VideoSortBreadcrumbTokensReplacePreviousSuffix()
    {
        var libraryJs = ReadWebAsset("app-library.js");
        var methodStart = libraryJs.IndexOf("function _videoRenderBreadcrumbTokens(", StringComparison.Ordinal);
        var methodEnd = libraryJs.IndexOf("\nfunction videoClearBreadcrumbToken(", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = libraryJs[methodStart..methodEnd];

        AssertContainsAll(
            method,
            "bc.querySelector('#video-breadcrumb-tokens')?.remove();",
            "tokenContainer.id = 'video-breadcrumb-tokens';",
            "bc.appendChild(tokenContainer);");
        Assert.DoesNotContain("bc.innerHTML +=", method, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoListSortingUsesConsistentAscendingAndDescendingDirection()
    {
        var libraryJs = ReadWebAsset("app-library.js");
        var methodStart = libraryJs.IndexOf("function _sortedFiles(files) {", StringComparison.Ordinal);
        var methodEnd = libraryJs.IndexOf("\nfunction _filteredFiles(", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = libraryJs[methodStart..methodEnd];

        AssertContainsAll(
            method,
            "const direction = _videoSortDir === 'desc' ? -1 : 1;",
            "((Number(a.duration)||0) - (Number(b.duration)||0)) * direction",
            "const r = ya.localeCompare(yb) ||",
            "((Number(a.progress)||0) - (Number(b.progress)||0)) * direction",
            ".localeCompare(b.displayName||b.name||'') * direction");
        Assert.DoesNotContain("asc ? -r : r", method, StringComparison.Ordinal);
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
    public void LiveTvPlaybackWaitCorrelatesStatusAndUsesBoundedStartupBudget()
    {
        var liveTvJs = ReadWebAsset("app-live-tv.js");

        AssertContainsAll(
            liveTvJs,
            "const _liveTvPlaybackStartTimeoutMs = 15_000;",
            "const _liveTvPlaybackStartBudgetMs = 45_000;",
            "async function _waitForLiveTvPlaybackStart(expectedUrl, deadline, attemptId)",
            "if (_liveTvAttemptingUrl || !_liveTvCurrentUrl) return false;",
            "if (!_liveTvPlaybackUrlsMatch(expectedUrl, statusUrl))",
            "await _liveTvTryStartChannel(candidate, name, candidateDeadline, attemptId, requestController)");
    }

    [Fact]
    public void LiveTvPlaybackStatusMatchingDecodesEncodedStatusPaths()
    {
        var liveTvJs = ReadWebAsset("app-live-tv.js");

        AssertContainsAll(
            liveTvJs,
            "function _liveTvDecodeStatusUrl(value)",
            "const binary = atob(raw);",
            "new TextDecoder('utf-8').decode(bytes).trim();",
            "const left = _liveTvDecodeStatusUrl(first);",
            "const right = _liveTvDecodeStatusUrl(second);");
    }

    [Fact]
    public void LiveTvStopCancelsStartupAndDoesNotMarkTheChannelFailed()
    {
        var liveTvJs = ReadWebAsset("app-live-tv.js");
        var libraryJs = ReadWebAsset("app-library.js");

        AssertContainsAll(
            liveTvJs,
            "function liveTvCancelPendingPlaybackStart(reason = 'user')",
            "_liveTvPlaybackAttemptGeneration += 1;",
            "_liveTvPlayRequestController?.abort();",
            "if (attemptId !== _liveTvPlaybackAttemptGeneration) return;",
            "if (sawExpectedUrl && status)",
            "if (!started) {",
            "liveTvCancelPendingPlaybackStart('server');");
        AssertContainsAll(
            libraryJs,
            "async function stop() {",
            "if (currentMode === 'live-tv' && typeof liveTvCancelPendingPlaybackStart === 'function')",
            "liveTvCancelPendingPlaybackStart();");
    }

    [Fact]
    public void LiveTvFailureAndHiddenChannelFiltersAreRecoverable()
    {
        var liveTvJs = ReadWebAsset("app-live-tv.js");

        AssertContainsAll(
            liveTvJs,
            "let _liveTvHideFailed = false;",
            "_liveTvHideFailed = _liveTvHideFailedExplicit && raw.hideFailed === true;",
            "raw.hideFailedExplicit === true",
            "function liveTvToggleShowHidden()",
            "if (_isLiveTvHidden(c.url) && !_liveTvShowHidden) return false;",
            "liveTvUnhideChannel(decodeURIComponent(");
    }

    [Fact]
    public void LiveTvShowsCachedChannelsImmediatelyAndDefaultsToAllChannels()
    {
        var liveTvJs = ReadWebAsset("app-live-tv.js");

        AssertContainsAll(
            liveTvJs,
            "_liveTvShowFavoritesOnly = false;",
            "const initialChannels = hasCachedChannels ? cached.channels : _liveTvFallbackChannels.slice();",
            "_liveTvApplyChannels(initialChannels);",
            "if (_liveTvAttemptingUrl || !_liveTvCurrentUrl) return false;",
            "const reportedUrl = _liveTvDecodeStatusUrl(statusUrl);",
            "clearTimeout(timeout);",
            "async function _liveTvTryStartChannel(url, name, deadline, attemptId, requestController)",
            "signal: requestController?.signal",
            "_liveTvPlayRequestController?.abort();",
            "clearPlaybackErrorDedup('Live TV');");
    }

    [Fact]
    public void LiveTvPermanentUpstreamFailuresAreNotPassedToThePlayer()
    {
        var playbackHandler = ReadProjectFile("Services/Web/WebServer.Handlers.VideoPlayback.cs");

        AssertContainsAll(
            playbackHandler,
            "var upstreamStatusCode = (int)response.StatusCode;",
            "upstreamStatusCode is >= 400 and < 500",
            "response.StatusCode is not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests",
            "$\"Stream provider returned HTTP {upstreamStatusCode} {response.ReasonPhrase}.\"");
    }

    [Fact]
    public void LiveTvPlaybackIsSupersededWhenPlaybackIsStoppedOrReplaced()
    {
        var playbackHandler = ReadProjectFile("Services/Web/WebServer.Handlers.VideoPlayback.cs");
        var requestHandler = ReadProjectFile("Services/Web/WebServer.Handlers.Request.cs");
        var server = ReadProjectFile("Services/Web/WebServer.cs");

        AssertContainsAll(
            server,
            "private readonly object _liveTvPlaybackRequestGate = new();",
            "private long _liveTvPlaybackRequestGeneration;");
        AssertContainsAll(
            playbackHandler,
            "requestGeneration = ++_liveTvPlaybackRequestGeneration;",
            "if (requestGeneration != _liveTvPlaybackRequestGeneration",
            "|| requestCancellation.IsCancellationRequested",
            "invalidatePendingLiveTvRequest: false",
            "TrySendResponse(ctx, 409, \"text/plain\", \"Live TV playback request was superseded.\");");
        AssertContainsAll(
            requestHandler,
            "case \"/api/stop\":",
            "_liveTvPlaybackRequestGeneration++;",
            "_liveTvPlaybackRequestCancellation?.Cancel();",
            "_callbacks.Stop();");
    }

    [Fact]
    public void LiveTvProbeUsesValidatedDestinationsAndBoundedCancellation()
    {
        var playbackHandler = ReadProjectFile("Services/Web/WebServer.Handlers.VideoPlayback.cs");
        var server = ReadProjectFile("Services/Web/WebServer.cs");

        AssertContainsAll(
            playbackHandler,
            "new CancellationTokenSource(TimeSpan.FromSeconds(8))",
            "_podcastFeedHttpClient.GetAsync(",
            "catch (PodcastDestinationRejectedException)",
            "catch (PodcastHostResolutionException)",
            "requestCancellation.IsCancellationRequested");
        AssertContainsAll(
            server,
            "CreateLinkedTokenSource(lifetimeToken)",
            "_source.CancelAsync()",
            "_liveTvPlaybackRequestCancellation?.Cancel();");
    }

    [Fact]
    public void LiveTvHealthProbeTreatsOpaqueAndTransientFailuresAsUnknown()
    {
        var liveTvJs = ReadWebAsset("app-live-tv.js");
        var probeStart = liveTvJs.IndexOf("async function _liveTvProbeChannelHealth(", StringComparison.Ordinal);
        var probeEnd = liveTvJs.IndexOf("\nfunction _liveTvProbeVisibleChannels(", probeStart, StringComparison.Ordinal);
        Assert.True(probeStart >= 0 && probeEnd > probeStart, "Expected the channel-health probe implementation.");

        var probe = liveTvJs[probeStart..probeEnd];
        AssertContainsAll(
            probe,
            "response.type !== 'opaque' && response.status !== 0",
            "probeResult = null;",
            "reason: response.type === 'opaque' ? 'opaque_response' : 'status_unavailable'",
            "if (err?.name !== 'AbortError')");
        Assert.DoesNotContain("probeResult = false;", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveTvTelemetrySharesRequestIdsAndOmitsFullStreamUrls()
    {
        var liveTvJs = ReadWebAsset("app-live-tv.js");
        var playbackHandler = ReadProjectFile("Services/Web/WebServer.Handlers.VideoPlayback.cs");

        AssertContainsAll(
            liveTvJs,
            "let _liveTvCurrentRequestId = '';",
            "'X-RemotePlay-Request-Id': _liveTvCurrentRequestId",
            "requestId,",
            "phase: 'startup_confirmed'",
            "requestedHost: _liveTvHostLabel(url)",
            "playedHost: _liveTvHostLabel(playedUrl)",
            "_liveTvEmitTelemetry('play_cancelled'");
        Assert.Contains("\"X-RemotePlay-Request-Id\"", playbackHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void ReusedIsolatedPlayerRestartsHealthMonitoring()
    {
        var source = ReadProjectFile("UI", "Windows", "MainWindow.VideoPlayerIsolated.cs");
        var methodStart = source.IndexOf("private async void PlayMovieIsolated(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "Expected the isolated playback-start method.");

        var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        var method = source[methodStart..(methodEnd < 0 ? source.Length : methodEnd)];
        var reuseIndex = method.IndexOf("Reusing existing VideoPlayer process", StringComparison.Ordinal);
        var monitorIndex = method.IndexOf("StartVideoPlayerHealthMonitoring();", StringComparison.Ordinal);
        var playCommandIndex = method.IndexOf("SendPlayCommandAsync(playCmd)", StringComparison.Ordinal);

        Assert.True(
            reuseIndex >= 0 && monitorIndex > reuseIndex && playCommandIndex > monitorIndex,
            "Health monitoring must be restarted for reused players before sending a play command.");
    }

    [Fact]
    public void IsolatedPlayerRecyclesBeforeReplacingAnActiveHttpStream()
    {
        var source = ReadProjectFile("UI", "Windows", "MainWindow.VideoPlayerIsolated.cs");
        var methodStart = source.IndexOf("private async void PlayMovieIsolated(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "Expected the isolated playback-start method.");

        var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        var method = source[methodStart..(methodEnd < 0 ? source.Length : methodEnd)];
        var recycleIndex = method.IndexOf("ShouldRecycleForHttpStreamSwitch(_currentFilePath, filePath)", StringComparison.Ordinal);
        var disposeIndex = method.IndexOf("await Task.Run(previousManager.Dispose);", StringComparison.Ordinal);
        var playCommandIndex = method.IndexOf("SendPlayCommandAsync(playCmd)", StringComparison.Ordinal);

        Assert.True(
            recycleIndex >= 0 && disposeIndex > recycleIndex && playCommandIndex > disposeIndex,
            "The isolated player must be recycled before a replacement HTTP stream is sent.");
    }

    [Fact]
    public void IsolatedPlayerHealthPingRequiresResponsiveUiAndBoundedIpc()
    {
        var managerSource = ReadProjectFile("Services", "VideoPlayerProcessManager.cs");
        var playerSource = ReadProjectFile("RemotePlay.VideoPlayer", "MainWindow.xaml.cs");
        var monitorSource = ReadProjectFile("UI", "Windows", "MainWindow.VideoPlayerMonitoring.cs");

        var pingStart = playerSource.IndexOf("if (message is PingCommand ping)", StringComparison.Ordinal);
        var responseSwitch = pingStart < 0
            ? -1
            : playerSource.IndexOf("IpcMessage response = message switch", pingStart, StringComparison.Ordinal);
        Assert.True(pingStart >= 0 && responseSwitch > pingStart, "Expected the isolated-player ping handler.");
        var pingHandler = playerSource[pingStart..responseSwitch];
        var dispatcherCheck = pingHandler.IndexOf("await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Send)", StringComparison.Ordinal);
        var pongResponse = pingHandler.IndexOf("return new PongResponse", StringComparison.Ordinal);
        Assert.True(
            dispatcherCheck >= 0 && pongResponse > dispatcherCheck,
            "A ping should only be answered after the WPF dispatcher processes work.");

        AssertContainsAll(
            managerSource,
            "private static readonly TimeSpan CommandResponseTimeout = TimeSpan.FromSeconds(5);",
            "private static readonly TimeSpan PingResponseTimeout = TimeSpan.FromSeconds(5);",
            "pipeWriter = _pipeWriter;",
            "pipeReader = _pipeReader;",
            "pipeClient = _pipeClient;",
            "Task.WhenAny(responseTask, timeoutTask)",
            "DisposePipeResources(pipeWriter, pipeReader, pipeClient);",
            "return ConnectIpc(ct, ReconnectTimeoutMs);",
            "CanResumePlaybackPath(lastFilePath)");
        AssertContainsAll(
            monitorSource,
            "Volatile.Read(ref _isRestarting) != 0",
            "await _videoPlaybackStartLock.WaitAsync();",
            "if (!ReferenceEquals(_videoPlayerManager, manager))",
            "await manager.RestartAndResumeAsync();");
    }

    [Fact]
    public void IsolatedPlayerVlcErrorsAreLoggedAndExposedInPlaybackStatus()
    {
        var playerSource = ReadProjectFile("RemotePlay.VideoPlayer", "MainWindow.xaml.cs");
        var contractSource = ReadProjectFile("RemotePlay.VideoPlayer", "Ipc", "IpcContracts.cs");
        var playbackSource = ReadProjectFile("UI", "Windows", "MainWindow.Playback.cs");

        AssertContainsAll(
            playerSource,
            "vlc.Log += OnLibVlcLog;",
            "private void OnLibVlcLog(object? sender, LogEventArgs e)",
            "PlayerDiagnostics.Error(detail);",
            "LastError = System.Threading.Volatile.Read(ref _lastPlaybackError)");
        AssertContainsAll(contractSource, "public string LastError { get; init; } = string.Empty;");
        AssertContainsAll(playbackSource, "LastError = isolatedVideoActive", "isolatedStatus!.LastError");
    }

    [Fact]
    public void IsolatedPlayerStopClearsAndSuppressesLatePlaybackErrors()
    {
        var playerSource = ReadProjectFile("RemotePlay.VideoPlayer", "MainWindow.xaml.cs");
        var playbackSource = ReadProjectFile("UI", "Windows", "MainWindow.Playback.cs");
        var isolatedPlaybackSource = ReadProjectFile("UI", "Windows", "MainWindow.VideoPlayerIsolated.cs");

        AssertContainsAll(
            playerSource,
            "private bool _playbackStopRequested;",
            "System.Threading.Volatile.Write(ref _playbackStopRequested, true);",
            "System.Threading.Volatile.Write(ref _lastPlaybackError, string.Empty);",
            "if (!System.Threading.Volatile.Read(ref _playbackStopRequested))",
            "Ignoring playback error reported after an explicit stop");

        var resetStart = playbackSource.IndexOf("private void ResetPlaybackState()", StringComparison.Ordinal);
        Assert.True(resetStart >= 0, "Expected the common playback reset method.");
        var resetEnd = playbackSource.IndexOf("\n    private ", resetStart + 1, StringComparison.Ordinal);
        var resetMethod = playbackSource[resetStart..(resetEnd < 0 ? playbackSource.Length : resetEnd)];
        AssertContainsAll(resetMethod, "_lastPlaybackError = string.Empty;");

        var stopStart = isolatedPlaybackSource.IndexOf("private async Task StopVideoIsolatedAsync()", StringComparison.Ordinal);
        Assert.True(stopStart >= 0, "Expected the isolated player stop method.");
        var stopEnd = isolatedPlaybackSource.IndexOf("\n    private ", stopStart + 1, StringComparison.Ordinal);
        var stopMethod = isolatedPlaybackSource[stopStart..(stopEnd < 0 ? isolatedPlaybackSource.Length : stopEnd)];
        AssertContainsAll(stopMethod, "_currentFilePath = null;", "_lastPlaybackError = string.Empty;");

        var logStart = playerSource.IndexOf("private void OnLibVlcLog(", StringComparison.Ordinal);
        Assert.True(logStart >= 0, "Expected to find the isolated player VLC log handler.");
        var logEnd = playerSource.IndexOf("\n    private void OnMediaPlaying(", logStart, StringComparison.Ordinal);
        Assert.True(logEnd > logStart, "Expected the isolated player VLC log handler to end before OnMediaPlaying.");
        Assert.DoesNotContain(
            "System.Threading.Volatile.Write(ref _lastPlaybackError, detail);",
            playerSource[logStart..logEnd],
            StringComparison.Ordinal);
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
