using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Animation;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using RemotePlay.Models;

namespace RemotePlay;

public partial class MainWindow
{
    // -- Playback -------------------------------------------------------------

    private void PlayNextQueuedMovie()
    {
        string? nextPath = null;
        Dispatcher.Invoke(() =>
        {
            if (_playbackQueue.Count == 0)
                return;

            nextPath = _playbackQueue[0];
            _playbackQueue.RemoveAt(0);
        });

        if (!string.IsNullOrWhiteSpace(nextPath))
            PlayMovie(nextPath);
    }

    private void EnqueueMovie(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        Dispatcher.Invoke(() =>
        {
            if (_playbackQueue.Any(path => string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase)))
                return;

            _playbackQueue.Add(filePath);
            AppendLog($"Queued: {Path.GetFileName(filePath)}");
        });
    }

    private void ClearPlaybackQueue()
    {
        Dispatcher.Invoke(() =>
        {
            _playbackQueue.Clear();
            AppendLog("Playback queue cleared");
        });
    }

    private void RemoveFromPlaybackQueue(string filePath)
    {
        Dispatcher.Invoke(() =>
        {
            var index = _playbackQueue.FindIndex(path => string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            _playbackQueue.RemoveAt(index);
            AppendLog($"Removed from queue: {Path.GetFileName(filePath)}");
        });
    }

    private void MovePlaybackQueueItem(string filePath, int direction)
    {
        Dispatcher.Invoke(() =>
        {
            var index = _playbackQueue.FindIndex(path => string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            var targetIndex = index + Math.Sign(direction);
            if (targetIndex < 0 || targetIndex >= _playbackQueue.Count)
                return;

            (_playbackQueue[index], _playbackQueue[targetIndex]) = (_playbackQueue[targetIndex], _playbackQueue[index]);
            AppendLog($"Moved queue item: {Path.GetFileName(filePath)}");
        });
    }

    private PlaybackQueueItem[] GetPlaybackQueue()
    {
        PlaybackQueueItem[] result = [];
        Dispatcher.Invoke(() =>
        {
            result = _playbackQueue
                .Select(path => new PlaybackQueueItem(WebPathHelpers.EncodePath(path), Path.GetFileNameWithoutExtension(path)))
                .ToArray();
        });

        return result;
    }

    private void PlayMovie(string filePath)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                RestoreWindowForPlayback();
                _isPaused = false;
                _lastPlaybackError = string.Empty;
                _currentFilePath = filePath;
                _pendingResumePosition = null;
                _smartResumeApplied = false;
                _duration = TimeSpan.Zero;
                _lastSubtitleTrackId = null;
                _lastAudioTrackId = null;
                _preferredSubtitleApplied = false;
                _zoom = 1;
                _forceSwAudio = false;
                ApplyStoredMoviePreferences(filePath);
                BeginVideoTransition();
                VideoPlayer.Visibility = Visibility.Visible;
                _mediaPlayer.Stop();
                using var media = new Media(_libVlc, new Uri(filePath, UriKind.Absolute));
                media.AddOption(":avcodec-hw=none");
                if (_forceSwAudio)
                {
                    media.AddOption(":audio-resampler=soxr");
                    media.AddOption(":no-spdif");
                    AppendLog("[Audio] Software audio decode forced for this file.");
                }
                _hasSubtitles = TryAttachSubtitle(media, filePath);
                _mediaPlayer.Play(media);
                _mediaPlayer.SetRate((float)_playbackSpeed);
                ApplyAudioLevel();
                ApplyVideoZoom();
                _historyTimer.Start();
                NowPlayingText.Text = "▶  " + Path.GetFileNameWithoutExtension(filePath);
                ShowBanner();
                AppendLog($"Playing: {Path.GetFileName(filePath)}");
                Logger.Info($"Playing: {filePath}");

                // Auto-switch to video mode when a movie starts.
                // If we are already in video mode, self-heal bounds in case the
                // window drifted away from full monitor size on this machine.
                if (!_isVideoMode)
                    OnToggleView(this, new RoutedEventArgs());
                else
                    EnsureFullscreenWindowBounds();
            }
            catch (Exception ex)
            {
                Logger.Error("Error starting playback", ex);
                AppendLog($"ERROR playing: {ex.Message}");
            }
        });
    }

    private void RestoreWindowForPlayback()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            ShowFromTray();
            return;
        }

        ShowInTaskbar = true;
        Activate();
    }

    private void ApplyStoredMoviePreferences(string filePath)
    {
        var preferences = _playbackHistory.GetPreferences(filePath);
        if (preferences is null)
            return;

        _isApplyingMoviePreferences = true;
        try
        {
            _brightness = Math.Clamp(preferences.Brightness, 0, 1);
            _saturation = Math.Clamp(preferences.Saturation, 0, 2);
            _zoom = Math.Clamp(preferences.Zoom, 1, 2);
            _forceSwAudio = preferences.ForceSwAudio;
        }
        finally
        {
            _isApplyingMoviePreferences = false;
        }
    }

    private void SetAudioTrack(int trackId)
    {
        Dispatcher.Invoke(() =>
        {
            if (_mediaPlayer.Media is null)
                return;

            try
            {
                _mediaPlayer.SetAudioTrack(trackId);
                _lastAudioTrackId = trackId;
            }
            catch (Exception ex)
            {
                Logger.Error("Could not set audio track", ex);
            }
        });
    }

    private void SetSubtitleTrack(int trackId)
    {
        Dispatcher.Invoke(() =>
        {
            if (_mediaPlayer.Media is null)
                return;

            try
            {
                _mediaPlayer.SetSpu(trackId);
                _subtitlesEnabled = trackId != -1;
                if (trackId >= 0)
                    _lastSubtitleTrackId = trackId;
                _preferredSubtitleApplied = true;
                SavePlaybackPreferences();
            }
            catch (Exception ex)
            {
                Logger.Error("Could not set subtitle track", ex);
            }
        });
    }

    private void StopMovie()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                SaveCurrentPlaybackPosition();
                _historyTimer.Stop();
                _miniPreviewTimer.Stop();
                MiniPreviewBorder.Visibility = Visibility.Collapsed;
                _mediaPlayer.Stop();
                _currentFilePath = null;
                _pendingResumePosition = null;
                _smartResumeApplied = false;
                _isPaused = false;
                _duration = TimeSpan.Zero;
                _hasSubtitles = false;
                ShowIdleOverlay();
                HideBanner();
                AppendLog("Playback stopped");
                Logger.Info("Playback stopped");
            }
            catch (Exception ex)
            {
                Logger.Error("Error stopping playback", ex);
                AppendLog($"ERROR stopping: {ex.Message}");
            }
        });
    }

    private void TogglePause()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (_isPaused)
                {
                    _mediaPlayer.Play();
                    _isPaused = false;
                    NowPlayingText.Text = NowPlayingText.Text.Replace("⏸", "▶");
                }
                else
                {
                    _mediaPlayer.Pause();
                    _isPaused = true;
                    NowPlayingText.Text = NowPlayingText.Text.Replace("▶", "⏸");
                    ShowBanner();
                    _bannerTimer.Stop();
                }
                AppendLog(_isPaused ? "Paused" : "Resumed");
            }
            catch (Exception ex)
            {
                Logger.Error("Error toggling pause", ex);
                AppendLog($"ERROR pause/resume: {ex.Message}");
            }
        });
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var hasQueuedMovie = _playbackQueue.Count > 0;
            var endedFilePath = _currentFilePath;
            var shouldPlayNext = _config.PlaybackEndBehavior == PlaybackEndMode.PlayNext;
            if (!string.IsNullOrWhiteSpace(_currentFilePath))
                _playbackHistory.Clear(_currentFilePath);

            _historyTimer.Stop();
            _currentFilePath = null;
            _pendingResumePosition = null;
            _isPaused = false;
            _duration = TimeSpan.Zero;
            _hasSubtitles = false;
            ShowIdleOverlay();
            HideBanner();
            AppendLog("Playback finished");
            Logger.Info("Playback finished");

            if (hasQueuedMovie)
            {
                Dispatcher.InvokeAsync(PlayNextQueuedMovie);
            }
            else if (shouldPlayNext)
            {
                var nextPath = GetAdjacentVideoPath(endedFilePath, 1);
                if (!string.IsNullOrWhiteSpace(nextPath))
                    Dispatcher.InvokeAsync(() => PlayMovie(nextPath));
            }
        });
    }

    private void OnMediaPlaying(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            VideoPlayer.Visibility = Visibility.Visible;
            _ = CompleteVideoTransitionAsync();

            if (!_isVideoMode)
                _miniPreviewTimer.Start();
            ApplyVideoZoom();

            // Capture codec/track info while media is active and tracks are populated.
            _codecInfo = CaptureCodecInfo();

            Logger.Info("[Subtitle] OnMediaPlaying: attempting subtitle selection");
            var preferredSubtitleTrack = GetPreferredSubtitleTrackId();
            if (preferredSubtitleTrack is int subtitleTrackId)
            {
                _hasSubtitles = true;
                if (_subtitlesEnabled || subtitleTrackId >= 0)
                {
                    _mediaPlayer.SetSpu(subtitleTrackId);
                    _lastSubtitleTrackId = subtitleTrackId;
                    _subtitlesEnabled = subtitleTrackId >= 0;
                }
                else
                {
                    _mediaPlayer.SetSpu(-1);
                }
                _preferredSubtitleApplied = true;
                Logger.Info($"[Subtitle] OnMediaPlaying: applied track id={subtitleTrackId}");
            }
            else
            {
                Logger.Info("[Subtitle] OnMediaPlaying: no track selected — will retry via ESAdded");
            }

            if (string.IsNullOrWhiteSpace(_currentFilePath) || _duration <= TimeSpan.Zero)
                return;

            var resumePosition = _playbackHistory.GetResumePosition(_currentFilePath, _duration);
            if (resumePosition is null)
                return;

            _pendingResumePosition = resumePosition;
            AppendLog($"Resume available from {resumePosition.Value:mm\\:ss}");
        });
    }

    private void OnMediaLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() => _duration = TimeSpan.FromMilliseconds(Math.Max(0, e.Length)));
    }

    private void OnESAdded(object? sender, MediaPlayerESAddedEventArgs e)
    {
        Logger.Info($"[Subtitle] ESAdded: type={e.Type} id={e.Id}");
        if (e.Type != TrackType.Text)
            return;

        Dispatcher.InvokeAsync(() =>
        {
            Logger.Info($"[Subtitle] ESAdded (Text track id={e.Id}): preferredSubtitleApplied={_preferredSubtitleApplied}");
            if (_preferredSubtitleApplied)
                return;

            var preferredTrack = GetPreferredSubtitleTrackId();
            if (preferredTrack is not int trackId)
            {
                Logger.Info("[Subtitle] ESAdded: GetPreferredSubtitleTrackId returned null");
                return;
            }

            _hasSubtitles = true;
            if (_subtitlesEnabled || trackId >= 0)
            {
                _mediaPlayer.SetSpu(trackId);
                _lastSubtitleTrackId = trackId;
                _subtitlesEnabled = trackId >= 0;
            }
            else
            {
                _mediaPlayer.SetSpu(-1);
            }
            _preferredSubtitleApplied = true;
            Logger.Info($"[Subtitle] ESAdded: applied track id={trackId}");
        });
    }

    private void OnMediaFailed(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _lastPlaybackError = "VLC playback failed";
            _historyTimer.Stop();
            Logger.Error("Media playback failed");
            ShowIdleOverlay();
            AppendLog("MEDIA ERROR: VLC playback failed");
            ShowDiag("Playback error — VLC could not play this file");
        });
    }

    private void HideIdleOverlay()
    {
        IdleOverlay.Visibility = Visibility.Collapsed;
        IdleOverlay.IsHitTestVisible = false;

        // VideoPanel was pre-created Hidden (HWND exists at correct size, just SW_HIDE).
        // Switching to Visible is SW_SHOW only — no resize, no WM_PAINT white flash.
        VideoPanel.Visibility = Visibility.Visible;

        if (VideoPanel.FindName("VideoBrightnessOverlay") is UIElement brightnessOverlay)
            brightnessOverlay.Visibility = Visibility.Visible;

        ApplyBrightnessOverlay();
    }

    private void BeginVideoTransition()
    {
        _videoTransitionCts?.Cancel();
        _videoTransitionCts?.Dispose();
        _videoTransitionCts = new CancellationTokenSource();

        VideoTransitionOverlay.Visibility = Visibility.Visible;
        VideoTransitionOverlay.Opacity = 1;
        VideoTransitionOverlay.IsHitTestVisible = false;

        if (VideoPanel.FindName("VideoBrightnessOverlay") is UIElement brightnessOverlay)
        {
            brightnessOverlay.Opacity = 0;
            brightnessOverlay.Visibility = Visibility.Visible;
        }

        Logger.Info($"[VideoTransition] Begin: VideoPlayer={VideoPlayer.Visibility}, IdleOverlay={IdleOverlay.Visibility}, Brightness={VideoBrightnessOverlay.Opacity:0.###}");
    }

    private async Task CompleteVideoTransitionAsync()
    {
        var transitionCts = _videoTransitionCts;
        if (transitionCts is null)
        {
            // The transition was cancelled mid-flight (e.g. ShowIdleOverlay was called
            // during EnterFullscreenCleanAsync while BeginVideoTransition was already
            // pending). Media is now playing so we still need to reveal the video panel.
            await Dispatcher.InvokeAsync(() =>
            {
                HideIdleOverlay();
                VideoTransitionOverlay.Visibility = Visibility.Collapsed;
                VideoTransitionOverlay.Opacity = 0;
                ApplyBrightnessOverlay();
                Logger.Info("[VideoTransition] Complete (no-CTS fallback): video panel revealed");
            });
            return;
        }

        try
        {
            await Task.Delay(180, transitionCts.Token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_videoTransitionCts, transitionCts) || transitionCts.IsCancellationRequested)
                    return;

                HideIdleOverlay();
                VideoTransitionOverlay.Visibility = Visibility.Collapsed;
                VideoTransitionOverlay.Opacity = 0;
                ApplyBrightnessOverlay();
                Logger.Info($"[VideoTransition] Complete: VideoPlayer={VideoPlayer.Visibility}, IdleOverlay={IdleOverlay.Visibility}, Brightness={VideoBrightnessOverlay.Opacity:0.###}");
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelVideoTransition()
    {
        _videoTransitionCts?.Cancel();
        _videoTransitionCts?.Dispose();
        _videoTransitionCts = null;
        VideoTransitionOverlay.Visibility = Visibility.Collapsed;
        VideoTransitionOverlay.Opacity = 0;
    }

    private void ShowIdleOverlay()
    {
        CancelVideoTransition();

        // Refresh the QR image in case it wasn't loaded yet when UpdateServerUrlDisplay ran.
        if (_serverUrl is not null
            && FindName("FullscreenQrImage") is System.Windows.Controls.Image qrImage
            && qrImage.Source is null)
        {
            try { qrImage.Source = CreateQrBitmapSource(_serverUrl); }
            catch (Exception ex) { Logger.Error("Failed to refresh idle overlay QR", ex); }
        }

        // IdleOverlay is now in the root Grid (outside VideoPanel) so showing it
        // never requires the LibVLC HWND to be visible.
        IdleOverlay.Visibility = Visibility.Visible;
        IdleOverlay.IsHitTestVisible = true;

        // Collapse VideoPanel only when not in video mode. When in video mode keep
        // it Hidden so the HWND stays at the correct size and SW_SHOW later won't
        // cause a resize/repaint white flash.
        if (!_isVideoMode)
            VideoPanel.Visibility = Visibility.Collapsed;
        else if (VideoPanel.Visibility == Visibility.Visible)
            VideoPanel.Visibility = Visibility.Hidden;

        // Keep idle screen dark even when brightness overlay had been raised during playback.
        if (VideoPanel.FindName("VideoBrightnessOverlay") is UIElement brightnessOverlay)
        {
            brightnessOverlay.Opacity = 0;
            brightnessOverlay.Visibility = Visibility.Visible;
        }
    }

    private void ShowBanner()
    {
        _bannerTimer.Stop();
        NowPlayingBanner.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
        if (!_isPaused)
            _bannerTimer.Start();
    }

    private void HideBanner()
    {
        _bannerTimer.Stop();
        NowPlayingBanner.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(600)));
    }

    // Returns current playback state for the web interface
    private PlaybackStatus GetPlaybackStatus()
    {
        var result = new PlaybackStatus();
        Dispatcher.Invoke(() =>
        {
            var hasSubtitles = _hasSubtitles;
            var subtitlesEnabled = _subtitlesEnabled;
            var audioTracks = Array.Empty<TrackOption>();
            var subtitleTracks = Array.Empty<TrackOption>();
            var currentAudioTrackId = -1;
            var currentSubtitleTrackId = -1;
            try
            {
                // Guard: AudioTrackDescription and SpuDescription call into native
                // LibVLC memory. Accessing them after Stop/Dispose causes an
                // AccessViolationException. Only call when media is loaded and active.
                var isActive = _mediaPlayer.Media is not null
                    && _mediaPlayer.State is not VLCState.Stopped
                                          and not VLCState.NothingSpecial
                                          and not VLCState.Ended
                                          and not VLCState.Error;
                if (isActive)
                {
                    audioTracks = BuildTrackOptions(_mediaPlayer.AudioTrackDescription, includeOffTrack: false);
                    currentAudioTrackId = _mediaPlayer.AudioTrack;
                    if (currentAudioTrackId >= 0)
                        _lastAudioTrackId = currentAudioTrackId;

                    subtitleTracks = BuildTrackOptions(_mediaPlayer.SpuDescription, includeOffTrack: true);
                    if (subtitleTracks.Any(t => t.Id >= 0))
                        hasSubtitles = true;

                    var currentSpu = _mediaPlayer.Spu;
                    currentSubtitleTrackId = currentSpu;
                    subtitlesEnabled = currentSpu != -1;
                    if (currentSpu >= 0)
                        _lastSubtitleTrackId = currentSpu;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Could not inspect subtitle tracks", ex);
            }

            result = new PlaybackStatus
            {
                IsPlaying = _mediaPlayer.Media is not null && _mediaPlayer.State is not VLCState.Stopped and not VLCState.NothingSpecial and not VLCState.Ended,
                IsPaused = _isPaused,
                PositionSeconds = Math.Max(0, _mediaPlayer.Time / 1000d),
                DurationSeconds = Math.Max(0, _duration.TotalSeconds),
                Title = NowPlayingText.Text,
                Volume = _volume,
                IsMuted = _mediaPlayer.Mute,
                LastError = _lastPlaybackError,
                CanResume = _pendingResumePosition is not null,
                ResumePositionSeconds = Math.Max(0, _pendingResumePosition?.TotalSeconds ?? 0),
                SmartResumeApplied = _smartResumeApplied,
                Brightness = _brightness,
                Saturation = _saturation,
                AudioBoost = _audioBoost,
                PlaybackSpeed = _playbackSpeed,
                Zoom = _zoom,
                SubtitlesEnabled = subtitlesEnabled,
                HasSubtitles = hasSubtitles,
                AudioTracks = audioTracks,
                SubtitleTracks = subtitleTracks,
                CurrentAudioTrackId = currentAudioTrackId,
                CurrentSubtitleTrackId = currentSubtitleTrackId,
                PreviousTitle = GetAdjacentVideoTitle(-1),
                NextTitle = GetAdjacentVideoTitle(1),
                FilePath = _currentFilePath,
                Queue = GetPlaybackQueue(),
                QueueCount = _playbackQueue.Count
            };
        });
        return result;
    }

    private void SeekTo(double seconds)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                _mediaPlayer.Time = (long)Math.Max(0, seconds * 1000);
                _pendingResumePosition = null;
            }
            catch (Exception ex) { Logger.Error("Seek failed", ex); }
        });
    }

    private void SkipBy(double seconds)
    {
        Dispatcher.Invoke(() =>
        {
            if (_mediaPlayer.Media is null)
                return;

            var target = TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time)) + TimeSpan.FromSeconds(seconds);
            if (target < TimeSpan.Zero)
                target = TimeSpan.Zero;

            if (_duration > TimeSpan.Zero && target > _duration)
                target = _duration;

            _mediaPlayer.Time = (long)target.TotalMilliseconds;
        });
    }

    private void PlayAdjacent(int direction)
    {
        if (direction > 0 && TryPlayNextQueuedMovie())
            return;

        string? nextPath = null;
        Dispatcher.Invoke(() => nextPath = GetAdjacentVideoPath(direction));

        if (!string.IsNullOrWhiteSpace(nextPath))
            PlayMovie(nextPath);
    }

    private bool TryPlayNextQueuedMovie()
    {
        var hasQueuedMovie = false;
        Dispatcher.Invoke(() => hasQueuedMovie = _playbackQueue.Count > 0);
        if (!hasQueuedMovie)
            return false;

        PlayNextQueuedMovie();
        return true;
    }

    private string? GetAdjacentVideoPath(int direction)
    {
        return GetAdjacentVideoPath(_currentFilePath, direction);
    }

    private string? GetAdjacentVideoTitle(int direction)
    {
        var path = GetAdjacentVideoPath(direction);
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileNameWithoutExtension(path);
    }

    private string? GetAdjacentVideoPath(string? currentFilePath, int direction)
    {
        if (string.IsNullOrWhiteSpace(currentFilePath))
            return null;

        var directory = Path.GetDirectoryName(currentFilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        var files = Directory.EnumerateFiles(directory)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
            return null;

        var currentIndex = Array.FindIndex(files, f => string.Equals(f, currentFilePath, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0)
            return null;

        var nextIndex = currentIndex + Math.Sign(direction);
        return nextIndex >= 0 && nextIndex < files.Length ? files[nextIndex] : null;
    }

    private void SetVolume(double volume)
    {
        Dispatcher.Invoke(() =>
        {
            _volume = Math.Clamp(volume, 0, 1);
            ApplyAudioLevel();
            SavePlaybackPreferences();
        });
    }

    private void ToggleMute()
    {
        Dispatcher.Invoke(() => _mediaPlayer.Mute = !_mediaPlayer.Mute);
    }

    private void SetBrightness(double brightness)
    {
        Dispatcher.Invoke(() =>
        {
            _brightness = Math.Clamp(brightness, 0, 1);
            ApplyBrightnessOverlay();
            SaveCurrentMoviePreferences();
            SavePlaybackPreferences();
        });
    }

    private void SetAudioBoost(double audioBoost)
    {
        Dispatcher.Invoke(() =>
        {
            _audioBoost = Math.Clamp(audioBoost, 1, 2);
            ApplyAudioLevel();
            SavePlaybackPreferences();
        });
    }

    private void SetSaturation(double saturation)
    {
        Dispatcher.Invoke(() =>
        {
            _saturation = Math.Clamp(saturation, 0, 2);
            ApplyBrightnessOverlay();
            SaveCurrentMoviePreferences();
            SavePlaybackPreferences();
        });
    }

    private void SetZoom(double zoom)
    {
        Dispatcher.Invoke(() =>
        {
            _zoom = Math.Clamp(zoom, 1, 2);
            ApplyVideoZoom();
            SaveCurrentMoviePreferences();
            SavePlaybackPreferences();
        });
    }

    private void SetPlaybackSpeed(double playbackSpeed)
    {
        Dispatcher.Invoke(() =>
        {
            _playbackSpeed = Math.Clamp(playbackSpeed, 0.5, 2);
            _mediaPlayer.SetRate((float)_playbackSpeed);
            SavePlaybackPreferences();
            SaveCurrentMoviePreferences();
        });
    }

    private void ToggleSubtitles()
    {
        Dispatcher.Invoke(() =>
        {
            if (_mediaPlayer.Media is null)
            {
                _subtitlesEnabled = !_subtitlesEnabled;
                SavePlaybackPreferences();
                return;
            }

            var currentSpu = _mediaPlayer.Spu;
            var currentlyEnabled = currentSpu != -1;
            if (currentlyEnabled && currentSpu >= 0)
                _lastSubtitleTrackId = currentSpu;

            if (currentlyEnabled)
            {
                _mediaPlayer.SetSpu(-1);
                _subtitlesEnabled = false;
            }
            else
            {
                var restoreTrack = GetPreferredSubtitleTrackId();
                if (restoreTrack is null)
                {
                    _hasSubtitles = false;
                    _subtitlesEnabled = false;
                }
                else
                {
                    _mediaPlayer.SetSpu(restoreTrack.Value);
                    _lastSubtitleTrackId = restoreTrack.Value;
                    _subtitlesEnabled = true;
                }
            }

            SavePlaybackPreferences();
        });
    }

    private void SaveCurrentPlaybackPosition()
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath) || _mediaPlayer.Media is null)
            return;

        _playbackHistory.SavePosition(_currentFilePath, TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time)), _duration, _config.PlaybackHistoryLimit);
    }

    private void SaveCurrentMoviePreferences()
    {
        if (_isApplyingMoviePreferences || string.IsNullOrWhiteSpace(_currentFilePath))
            return;

        _playbackHistory.SavePreferences(_currentFilePath, new MoviePlaybackPreferences
        {
            Brightness = _brightness,
            Saturation = _saturation,
            Zoom = _zoom,
            ForceSwAudio = _forceSwAudio
        });
    }

    private void FixAudio()
    {
        var filePath = _currentFilePath;
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        _forceSwAudio = true;
        SaveCurrentMoviePreferences();
        AppendLog("[Audio] Software decode enabled — restarting playback...");
        PlayMovie(filePath);
    }

    private WebServer CreateWebServer(AppConfig config)
    {
        _appUpdater?.Stop();
        _appUpdater = new RemotePlay.Services.AppUpdater();
        _appUpdater.Start(config);

        return new WebServer(config, new WebServerCallbacks
        {
            Play = PlayMovie,
            Stop = StopMovie,
            Pause = TogglePause,
            GetStatus = GetPlaybackStatus,
            Seek = SeekTo,
            Skip = SkipBy,
            SetVolume = SetVolume,
            ToggleMute = ToggleMute,
            SetBrightness = SetBrightness,
            SetSaturation = SetSaturation,
            SetZoom = SetZoom,
            SetAudioBoost = SetAudioBoost,
            SetPlaybackSpeed = SetPlaybackSpeed,
            ToggleSubtitles = ToggleSubtitles,
            SetAudioTrack = SetAudioTrack,
            SetSubtitleTrack = SetSubtitleTrack,
            PlayAdjacent = PlayAdjacent,
            Enqueue = EnqueueMovie,
            RemoveFromQueue = RemoveFromPlaybackQueue,
            MoveQueueItem = MovePlaybackQueueItem,
            ClearQueue = ClearPlaybackQueue,
            ClearPlaybackHistory = _playbackHistory.Clear,
            GetDisplayDiagnostics = GetDisplayDiagnostics,
            FixAudio = FixAudio
        }, _broadcaster, _playbackHistory, _appUpdater);
    }

    private void ApplyAudioLevel()
    {
        try
        {
            _mediaPlayer.Volume = (int)Math.Round(_volume * _audioBoost * 100);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not apply audio level", ex);
        }
    }

    private void ApplyVideoZoom()
    {
        try
        {
            var zoom = Math.Clamp(_zoom, 1, 2);

            // Keep VLC in auto-fit mode and apply smooth UI zoom as a visual transform.
            _mediaPlayer.Scale = 0f;

            if (zoom <= 1)
            {
                VideoPlayer.RenderTransform = System.Windows.Media.Transform.Identity;
                return;
            }

            var width = Math.Max(0, VideoPlayer.ActualWidth);
            var height = Math.Max(0, VideoPlayer.ActualHeight);
            var offsetX = -((zoom - 1) * width) / 2;
            var offsetY = -((zoom - 1) * height) / 2;

            var transform = new System.Windows.Media.TransformGroup();
            transform.Children.Add(new System.Windows.Media.ScaleTransform(zoom, zoom));
            transform.Children.Add(new System.Windows.Media.TranslateTransform(offsetX, offsetY));
            VideoPlayer.RenderTransformOrigin = new System.Windows.Point(0, 0);
            VideoPlayer.RenderTransform = transform;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not apply video zoom", ex);
        }
    }

    private void ApplyBrightnessOverlay()
    {
        var nativeApplied = false;

        // Native VLC brightness uses 1.0 as normal.
        // Map slider 0..1 to VLC 0..2 so 50% = normal.
        // Saturation uses 1.0 as neutral.
        try
        {
            _mediaPlayer.SetAdjustInt(VideoAdjustOption.Enable, 1);
            _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Brightness, (float)Math.Clamp(_brightness * 2, 0, 2));
            _mediaPlayer.SetAdjustFloat(VideoAdjustOption.Saturation, (float)Math.Clamp(_saturation, 0, 2));
            nativeApplied = true;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not apply native VLC brightness", ex);
        }

        if (VideoPanel.FindName("VideoBrightnessOverlay") is UIElement brightnessOverlay)
        {
            // Only add extra highlight above 50% when native path is active.
            // Otherwise keep legacy overlay behavior as fallback.
            brightnessOverlay.Opacity = nativeApplied
                ? Math.Max(0, _brightness - 0.5) * 0.35
                : _brightness * 0.45;
        }
    }

    private void SavePlaybackPreferences()
    {
        var updatedConfig = _appConfigFactory.CreateForPlaybackPreferences(
            _config,
            _volume,
            _zoom,
            _audioBoost,
            _playbackSpeed,
            _subtitlesEnabled);

        _appConfigService.Save(updatedConfig);
    }

    private bool TryAttachSubtitle(Media media, string filePath)
    {
        var subtitlePath = FindSubtitlePath(filePath);
        if (subtitlePath is null)
            return false;

        media.AddOption($":sub-file={subtitlePath}");
        media.AddOption(_subtitlesEnabled ? ":sub-track=0" : ":sub-track=-1");
        AppendLog($"Subtitle detected: {Path.GetFileName(subtitlePath)}");
        return true;
    }

    private void ApplyPreferredAudioTrack()
    {
        try
        {
            if (_mediaPlayer.Media is null || _mediaPlayer.State is VLCState.Stopped
                    or VLCState.NothingSpecial or VLCState.Ended or VLCState.Error)
                return;

            var audioTracks = _mediaPlayer.AudioTrackDescription;
            if (audioTracks is null || audioTracks.Length == 0)
                return;

            var preferredTrack = FindPreferredTrackId(audioTracks, _config.PreferredAudioLanguage);
            if (preferredTrack is not null)
            {
                _mediaPlayer.SetAudioTrack(preferredTrack.Value);
                _lastAudioTrackId = preferredTrack.Value;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Could not apply preferred audio track", ex);
        }
    }

    private int? GetPreferredSubtitleTrackId()
    {
        try
        {
            var subtitleTracks = _mediaPlayer.SpuDescription;
            if (subtitleTracks is null || subtitleTracks.Length == 0)
            {
                Logger.Info("[Subtitle] SpuDescription is empty — no tracks available yet");
                return null;
            }

            var trackList = string.Join(", ", subtitleTracks.Select(t => $"[{t.Id}] '{t.Name}'"));
            Logger.Info($"[Subtitle] Available tracks ({subtitleTracks.Length}): {trackList}");
            Logger.Info($"[Subtitle] Config: primary='{_config.PreferredSubtitleLanguage}' secondary='{_config.SecondarySubtitleLanguage}' forced={_config.PreferForcedSubtitles}");

            var forcedTrack = _config.PreferForcedSubtitles
                ? FindForcedTrackId(subtitleTracks)
                : null;
            if (forcedTrack is not null)
            {
                Logger.Info($"[Subtitle] Selected forced track id={forcedTrack}");
                return forcedTrack;
            }

            var primaryTrack = FindPreferredTrackId(subtitleTracks, _config.PreferredSubtitleLanguage);
            if (primaryTrack is not null)
            {
                Logger.Info($"[Subtitle] Selected primary language track id={primaryTrack}");
                return primaryTrack;
            }
            Logger.Info($"[Subtitle] No match for primary language '{_config.PreferredSubtitleLanguage}'");

            var secondaryTrack = FindPreferredTrackId(subtitleTracks, _config.SecondarySubtitleLanguage);
            if (secondaryTrack is not null)
            {
                Logger.Info($"[Subtitle] Selected secondary language track id={secondaryTrack}");
                return secondaryTrack;
            }

            if (_lastSubtitleTrackId is int lastTrack && subtitleTracks.Any(t => t.Id == lastTrack))
            {
                Logger.Info($"[Subtitle] Restored last used track id={lastTrack}");
                return lastTrack;
            }

            var firstTrack = subtitleTracks
                .Where(t => t.Id >= 0)
                .OrderBy(t => t.Id)
                .Select(t => (int?)t.Id)
                .FirstOrDefault();

            if (firstTrack is int firstId)
            {
                Logger.Info($"[Subtitle] Falling back to first available track id={firstId}");
                return firstId;
            }

            Logger.Info("[Subtitle] No usable track found");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not resolve subtitle track", ex);
            return null;
        }
    }

    private static int? FindForcedTrackId(IEnumerable<TrackDescription> tracks) =>
        tracks
            .Where(t => t.Id >= 0 && IsForcedTrack(t.Name))
            .Select(t => (int?)t.Id)
            .FirstOrDefault();

    private static int? FindPreferredTrackId(IEnumerable<TrackDescription> tracks, string language)
    {
        var usableTracks = tracks.Where(t => t.Id >= 0).ToArray();
        if (usableTracks.Length == 0)
            return null;

        var normalizedLanguage = NormalizeTrackToken(language);
        if (string.IsNullOrWhiteSpace(normalizedLanguage))
            return null;

        var preferredTrack = usableTracks
            .Where(t => TrackNameMatchesLanguage(t.Name, normalizedLanguage))
            .Select(t => (int?)t.Id)
            .FirstOrDefault();
        return preferredTrack;
    }

    internal static bool TrackNameMatchesLanguage(string? trackName, string language)
    {
        if (string.IsNullOrWhiteSpace(trackName))
            return false;

        var normalizedName = NormalizeTrackToken(trackName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return false;

        var normalizedLanguage = NormalizeTrackToken(language);
        if (string.IsNullOrWhiteSpace(normalizedLanguage))
            return false;

        var aliases = GetLanguageAliases(normalizedLanguage);
        var tokens = normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return aliases.Any(alias => tokens.Any(token => token == alias || token.StartsWith(alias, StringComparison.OrdinalIgnoreCase)));
    }

    private static string[] GetLanguageAliases(string language) =>
        language switch
        {
            "eng" or "en" or "english" => ["eng", "en", "english"],
            "fre" or "fra" or "fr" or "french" => ["fre", "fra", "fr", "french"],
            "ger" or "deu" or "de" or "german" => ["ger", "deu", "de", "german", "deutsch"],
            "jpn" or "ja" or "japanese" => ["jpn", "ja", "japanese"],
            "spa" or "es" or "spanish" => ["spa", "es", "spanish"],
            "ita" or "it" or "italian" => ["ita", "it", "italian"],
            "por" or "pt" or "portuguese" => ["por", "pt", "portuguese"],
            "kor" or "ko" or "korean" => ["kor", "ko", "korean"],
            "chi" or "zho" or "zh" or "chinese" => ["chi", "zho", "zh", "chinese", "mandarin", "cantonese"],
            "rus" or "ru" or "russian" => ["rus", "ru", "russian"],
            "ara" or "ar" or "arabic" => ["ara", "ar", "arabic"],
            "hin" or "hi" or "hindi" => ["hin", "hi", "hindi"],
            "tur" or "tr" or "turkish" => ["tur", "tr", "turkish"],
            "pol" or "pl" or "polish" => ["pol", "pl", "polish"],
            "swe" or "sv" or "swedish" => ["swe", "sv", "swedish"],
            "nor" or "no" or "norwegian" => ["nor", "no", "norwegian", "nob", "nn"],
            "dan" or "da" or "danish" => ["dan", "da", "danish"],
            "fin" or "fi" or "finnish" => ["fin", "fi", "finnish"],
            "gre" or "ell" or "el" or "greek" => ["gre", "ell", "el", "greek"],
            "heb" or "he" or "hebrew" => ["heb", "he", "hebrew"],
            "cze" or "ces" or "cs" or "czech" => ["cze", "ces", "cs", "czech"],
            "hun" or "hu" or "hungarian" => ["hun", "hu", "hungarian"],
            "rum" or "ron" or "ro" or "romanian" => ["rum", "ron", "ro", "romanian"],
            "ukr" or "uk" or "ukrainian" => ["ukr", "uk", "ukrainian"],
            "tha" or "th" or "thai" => ["tha", "th", "thai"],
            "vie" or "vi" or "vietnamese" => ["vie", "vi", "vietnamese"],
            "ind" or "id" or "indonesian" => ["ind", "id", "indonesian"],
            "may" or "msa" or "ms" or "malay" => ["may", "msa", "ms", "malay"],
            "dut" or "nld" or "nl" or "dutch" => ["dut", "nld", "nl", "dutch", "nederlands"],
            _ => [language]
        };

    private static bool IsForcedTrack(string? trackName) =>
        !string.IsNullOrWhiteSpace(trackName)
        && trackName.Contains("forced", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTrackToken(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();

    private static TrackOption[] BuildTrackOptions(IEnumerable<TrackDescription>? tracks, bool includeOffTrack)
    {
        if (tracks is null)
            return includeOffTrack ? [new TrackOption(-1, "Off")] : [];

        var options = tracks
            .Where(t => includeOffTrack || t.Id >= 0)
            .Select(t => new TrackOption(
                t.Id,
                BuildTrackName(t, includeOffTrack),
                GuessTrackLanguage(t.Name),
                TrackNameContains(t.Name, "forced"),
                TrackNameContains(t.Name, "default")))
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .OrderBy(t => t.Id)
            .ToList();

        if (includeOffTrack && options.All(t => t.Id != -1))
            options.Insert(0, new TrackOption(-1, "Off"));

        return options.ToArray();
    }

    private static string BuildTrackName(TrackDescription track, bool includeOffTrack)
    {
        if (includeOffTrack && track.Id == -1)
            return "Off";

        return string.IsNullOrWhiteSpace(track.Name)
            ? $"Track {track.Id}"
            : track.Name;
    }

    private static string GuessTrackLanguage(string? name)
    {
        var normalized = NormalizeTrackToken(name);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        foreach (var option in LanguageOptions.Skip(1))
        {
            if (TrackNameMatchesLanguage(normalized, option.Code))
                return option.Name;
        }

        return string.Empty;
    }

    private static bool TrackNameContains(string? name, string token) =>
        NormalizeTrackToken(name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, token, StringComparison.OrdinalIgnoreCase));

    private static string? FindSubtitlePath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            return null;

        foreach (var extension in SubtitleExtensions)
        {
            var exactPath = Path.Combine(directory, fileName + extension);
            if (File.Exists(exactPath))
                return exactPath;
        }

        foreach (var extension in SubtitleExtensions)
        {
            var match = Directory.EnumerateFiles(directory, fileName + ".*" + extension)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (match is not null)
                return match;
        }

        return null;
    }

    private MediaCodecInfo? CaptureCodecInfo()
    {
        try
        {
            var media = _mediaPlayer.Media;
            if (media is null)
                return null;

            var tracks = media.Tracks;
            if (tracks is null || tracks.Length == 0)
                return null;

            // Codec fourcc → human-readable ASCII string (e.g. "h264", "mp4a").
            static string FourCc(uint fourcc) =>
                fourcc == 0 ? "N/A" : new string([
                    (char)(fourcc & 0xFF),
                    (char)((fourcc >> 8) & 0xFF),
                    (char)((fourcc >> 16) & 0xFF),
                    (char)((fourcc >> 24) & 0xFF)
                ]).Trim('\0').Trim();

            // Well-known fourcc → friendly display name.
            static string FourCcDesc(uint fourcc) => FourCc(fourcc) switch
            {
                "h264" or "H264" or "avc1" => "H.264 / AVC",
                "hev1" or "hvc1" or "hevc" or "H265" => "H.265 / HEVC",
                "av01" or "AV1 " => "AV1",
                "VP80" or "vp08" => "VP8",
                "VP90" or "vp09" => "VP9",
                "XVID" or "xvid" or "divx" or "DIVX" or "DX50" => "MPEG-4 / DivX",
                "mp4v" or "MP4V" => "MPEG-4 Visual",
                "mpg1" or "mpg2" or "mp2v" or "MP2V" => "MPEG-2 Video",
                "WMV3" or "wmv3" => "WMV3 / VC-1",
                "mp4a" => "AAC / MP4A",
                "a52 " or "A52 " or "ac-3" or "ac3 " => "AC-3 / Dolby Digital",
                "eac3" or "EAC3" => "E-AC-3 / Dolby Digital+",
                "dts " or "DTS " or "dtsc" or "dtsh" or "dtse" => "DTS",
                "truehd" => "Dolby TrueHD",
                "flac" or "FLAC" => "FLAC",
                "mp3 " or "MP3 " or "mpga" => "MP3",
                "aac " or "AAC " => "AAC",
                "opus" or "OPUS" => "Opus",
                "vorb" or "VORB" => "Vorbis",
                "pcm " or "PCM " or "araw" => "PCM",
                "subt" or "SUBT" => "Text / SRT",
                "ass " or "ASS " => "ASS / SSA",
                "dvbs" or "dvbt" or "DVBS" => "DVD Subtitle",
                var s => s
            };

            static string ChannelStr(uint ch) => ch switch
            {
                1 => "1.0 (Mono)",
                2 => "2.0 (Stereo)",
                6 => "5.1",
                8 => "7.1",
                _ => $"{ch} ch"
            };

            static string FpsStr(uint num, uint den) =>
                num > 0 && den > 0 ? $"{Math.Round((double)num / den, 3)} fps" : "N/A";

            static string SarStr(uint num, uint den) =>
                num > 0 && den > 0 ? $"{num}:{den}" : "N/A";

            var videoTracks = tracks
                .Where(t => t.TrackType == TrackType.Video)
                .Select(t => new VideoTrackInfo
                {
                    Id              = t.Id,
                    Codec           = FourCc(t.Codec),
                    CodecDescription = FourCcDesc(t.Codec),
                    Width           = (int)t.Data.Video.Width,
                    Height          = (int)t.Data.Video.Height,
                    FrameRate       = FpsStr(t.Data.Video.FrameRateNum, t.Data.Video.FrameRateDen),
                    AspectRatio     = SarStr(t.Data.Video.SarNum, t.Data.Video.SarDen),
                    Orientation     = t.Data.Video.Orientation.ToString(),
                    Language        = t.Language ?? string.Empty,
                    Description     = t.Description ?? string.Empty
                })
                .ToArray();

            var audioTracks = tracks
                .Where(t => t.TrackType == TrackType.Audio)
                .Select(t => new AudioTrackInfo
                {
                    Id              = t.Id,
                    Codec           = FourCc(t.Codec),
                    CodecDescription = FourCcDesc(t.Codec),
                    Channels        = (int)t.Data.Audio.Channels,
                    ChannelLayout   = ChannelStr(t.Data.Audio.Channels),
                    SampleRate      = (int)t.Data.Audio.Rate,
                    Language        = t.Language ?? string.Empty,
                    Description     = t.Description ?? string.Empty
                })
                .ToArray();

            var subtitleTracks = tracks
                .Where(t => t.TrackType == TrackType.Text)
                .Select(t => new SubtitleTrackInfo
                {
                    Id              = t.Id,
                    Codec           = FourCc(t.Codec),
                    CodecDescription = FourCcDesc(t.Codec),
                    Language        = t.Language ?? string.Empty,
                    Description     = t.Description ?? string.Empty,
                    Encoding        = t.Data.Subtitle.Encoding ?? string.Empty
                })
                .ToArray();

            return new MediaCodecInfo
            {
                FilePath        = _currentFilePath ?? string.Empty,
                FileName        = Path.GetFileName(_currentFilePath ?? string.Empty),
                ContainerFormat = Path.GetExtension(_currentFilePath ?? string.Empty).TrimStart('.').ToUpperInvariant(),
                TotalTracks     = tracks.Length,
                CapturedAtUtc   = DateTime.UtcNow.ToString("u"),
                VideoTracks     = videoTracks,
                AudioTracks     = audioTracks,
                SubtitleTracks  = subtitleTracks
            };
        }
        catch (Exception ex)
        {
            Logger.Error("Could not capture codec info", ex);
            return null;
        }
    }

}
