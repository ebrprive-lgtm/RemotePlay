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

    private PlaybackQueueItem[] GetPlaybackQueue()
    {
        PlaybackQueueItem[] result = [];
        Dispatcher.Invoke(() =>
        {
            result = _playbackQueue
                .Select(path => new PlaybackQueueItem(path, Path.GetFileNameWithoutExtension(path)))
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
                _isPaused = false;
                _lastPlaybackError = string.Empty;
                _currentFilePath = filePath;
                _pendingResumePosition = null;
                _duration = TimeSpan.Zero;
                _lastSubtitleTrackId = null;
                _lastAudioTrackId = null;
                _zoom = 1;
                ApplyStoredMoviePreferences(filePath);
                HideIdleOverlay();
                using var media = new Media(_libVlc, new Uri(filePath, UriKind.Absolute));
                media.AddOption(":avcodec-hw=none");
                _hasSubtitles = TryAttachSubtitle(media, filePath);
                _mediaPlayer.Play(media);
                _mediaPlayer.SetRate((float)_playbackSpeed);
                ApplyAudioLevel();
                ApplyVideoZoom();
                ApplyBrightnessOverlay();
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
            _subtitlesEnabled = preferences.SubtitlesEnabled;
            _lastAudioTrackId = preferences.AudioTrackId;
            _lastSubtitleTrackId = preferences.SubtitleTrackId;
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
                SaveCurrentMoviePreferences();
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
                _mediaPlayer.Stop();
                _currentFilePath = null;
                _pendingResumePosition = null;
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
            HideIdleOverlay();

            ApplyPreferredAudioTrack();
            ApplyVideoZoom();
            var preferredSubtitleTrack = GetPreferredSubtitleTrackId();
            _hasSubtitles = preferredSubtitleTrack is not null;
            if (_hasSubtitles)
            {
                if (_subtitlesEnabled)
                {
                    _mediaPlayer.SetSpu(preferredSubtitleTrack!.Value);
                    _lastSubtitleTrackId = preferredSubtitleTrack.Value;
                }
                else
                {
                    _mediaPlayer.SetSpu(-1);
                }
            }

            if (string.IsNullOrWhiteSpace(_currentFilePath) || _duration <= TimeSpan.Zero)
                return;

            var resumePosition = _playbackHistory.GetResumePosition(_currentFilePath, _duration);
            if (resumePosition is null)
                return;

            _pendingResumePosition = resumePosition;
            _mediaPlayer.Time = (long)resumePosition.Value.TotalMilliseconds;
            AppendLog($"Resumed from {resumePosition.Value:mm\\:ss}");
        });
    }

    private void OnMediaLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() => _duration = TimeSpan.FromMilliseconds(Math.Max(0, e.Length)));
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

        if (VideoPanel.FindName("VideoBrightnessOverlay") is UIElement brightnessOverlay)
            brightnessOverlay.Visibility = Visibility.Visible;

        ApplyBrightnessOverlay();
    }

    private void ShowIdleOverlay()
    {
        IdleOverlay.Visibility = Visibility.Visible;
        IdleOverlay.IsHitTestVisible = true;

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
            try { _mediaPlayer.Time = (long)Math.Max(0, seconds * 1000); }
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
        string? nextPath = null;
        Dispatcher.Invoke(() => nextPath = GetAdjacentVideoPath(direction));

        if (!string.IsNullOrWhiteSpace(nextPath))
            PlayMovie(nextPath);
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

        _playbackHistory.SavePosition(_currentFilePath, TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time)), _duration);
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
            AudioTrackId = _lastAudioTrackId,
            SubtitleTrackId = _lastSubtitleTrackId,
            SubtitlesEnabled = _subtitlesEnabled
        });
    }

    private WebServer CreateWebServer(AppConfig config) =>
        new(config, new WebServerCallbacks
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
            ClearQueue = ClearPlaybackQueue,
            GetDisplayDiagnostics = GetDisplayDiagnostics
        });

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
            var audioTracks = _mediaPlayer.AudioTrackDescription;
            if (audioTracks is null || audioTracks.Length == 0)
                return;

            var preferredTrack = FindPreferredTrackId(audioTracks, _config.PreferredAudioLanguage, preferForced: false);
            if (preferredTrack is not null)
            {
                _mediaPlayer.SetAudioTrack(preferredTrack.Value);
                _lastAudioTrackId = preferredTrack.Value;
            }
            else if (_lastAudioTrackId is int lastTrack && audioTracks.Any(t => t.Id == lastTrack))
            {
                _mediaPlayer.SetAudioTrack(lastTrack);
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
                return null;

            var configuredTrack = FindPreferredTrackId(
                subtitleTracks,
                _config.PreferredSubtitleLanguage,
                _config.PreferForcedSubtitles);
            if (configuredTrack is not null)
                return configuredTrack;

            if (_lastSubtitleTrackId is int lastTrack && subtitleTracks.Any(t => t.Id == lastTrack))
                return lastTrack;

            var firstTrack = subtitleTracks
                .Where(t => t.Id >= 0)
                .OrderBy(t => t.Id)
                .FirstOrDefault();

            return firstTrack.Id >= 0 ? firstTrack.Id : null;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not resolve subtitle track", ex);
            return null;
        }
    }

    private static int? FindPreferredTrackId(IEnumerable<TrackDescription> tracks, string language, bool preferForced)
    {
        var usableTracks = tracks.Where(t => t.Id >= 0).ToArray();
        if (usableTracks.Length == 0)
            return null;

        if (preferForced)
        {
            var forcedTrack = usableTracks.FirstOrDefault(t => IsForcedTrack(t.Name));
            if (forcedTrack.Id >= 0)
                return forcedTrack.Id;
        }

        var normalizedLanguage = NormalizeTrackToken(language);
        if (string.IsNullOrWhiteSpace(normalizedLanguage))
            return null;

        var preferredTrack = usableTracks.FirstOrDefault(t => TrackNameMatchesLanguage(t.Name, normalizedLanguage));
        return preferredTrack.Id >= 0 ? preferredTrack.Id : null;
    }

    private static bool TrackNameMatchesLanguage(string? trackName, string language)
    {
        if (string.IsNullOrWhiteSpace(trackName))
            return false;

        var normalizedName = NormalizeTrackToken(trackName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return false;

        return normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(token => token == language)
            || normalizedName.Contains(language, StringComparison.OrdinalIgnoreCase);
    }

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
            .Select(t => new TrackOption(t.Id, BuildTrackName(t, includeOffTrack)))
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

}
