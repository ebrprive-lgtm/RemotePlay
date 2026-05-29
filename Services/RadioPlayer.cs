using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RemotePlay.Services.Audio;

namespace RemotePlay.Services;

internal sealed class RadioPlayer : IDisposable
{
    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private MediaFoundationReader? _reader;
    private string _currentUrl   = string.Empty;
    private string _currentName  = string.Empty;
    private string _streamTitle  = string.Empty; // ICY / in-stream metadata title
    private bool   _isPlaying;
    private string _lastError    = string.Empty;
    private int    _deviceNumber = -1;
    private double _volume       = 0.8;
    private double _boost        = 1.0;
    private VolumeSampleProvider? _boostProvider;
    private ReverbSampleProvider? _reverb;
    private EqualizerSampleProvider? _eq;
    private int _reverbPreset = 0;
    private int _eqPreset = -1;
    private DateTime _playStartUtc = DateTime.MinValue;
    private DateTime _lastSampleUtc = DateTime.MinValue; // updated when audio data flows

    // Reuse the same device enumeration helpers as MusicPlayer.
    public static IReadOnlyList<(int DeviceNumber, string Name)> EnumerateDevices() =>
        MusicPlayer.EnumerateDevices();

    public static int ResolveDeviceNumber(string deviceId) =>
        MusicPlayer.ResolveDeviceNumber(deviceId);

    public void SetDevice(string deviceId)
    {
        lock (_lock) _deviceNumber = ResolveDeviceNumber(deviceId);
    }

    // Tracks a pending connect so Stop() can cancel it
    private CancellationTokenSource? _connectCts;

    // ── Playback ─────────────────────────────────────────────────────────────

    public void Play(string streamUrl, string stationName)
    {
        // Cancel any in-flight connection attempt and stop any current stream
        CancellationTokenSource cts;
        lock (_lock)
        {
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = cts = new CancellationTokenSource();
            StopLocked();
            _lastError     = string.Empty;
            _streamTitle   = string.Empty;
            _playStartUtc  = DateTime.UtcNow;
            _lastSampleUtc = DateTime.UtcNow;
            _currentUrl    = streamUrl;
            _currentName   = stationName;
            _isPlaying     = false; // will flip to true once stream opens
        }

        // Open the stream on a background thread — MediaFoundationReader can block for
        // 5–30 s on slow streams, which would starve the ThreadPool if done inline.
        _ = Task.Run(() =>
        {
            if (cts.IsCancellationRequested) return;
            try
            {
                var reader = new MediaFoundationReader(streamUrl);
                var output = new WaveOutEvent { DeviceNumber = _deviceNumber };
                output.PlaybackStopped += OnPlaybackStopped;
                // Wrap through a VolumeSampleProvider for combined volume+boost gain,
                // matching the gain model used by MusicPlayer (sample-level, same curve).
                var sampleSrc = reader.ToSampleProvider();
                // Ensure stereo for reverb
                ISampleProvider stereoSrc = sampleSrc.WaveFormat.Channels == 1
                    ? sampleSrc.ToStereo()
                    : sampleSrc;
                var boostProv = new VolumeSampleProvider(stereoSrc) { Volume = (float)(_volume * _boost) };
                var eq = new EqualizerSampleProvider(boostProv);
                eq.ApplyPreset(_eqPreset);
                var reverb = new ReverbSampleProvider(eq);
                reverb.ApplyPreset(_reverbPreset);
                output.Init(reverb);

                lock (_lock)
                {
                    if (cts.IsCancellationRequested || _currentUrl != streamUrl)
                    {
                        // A newer Play()/Stop() arrived while we were connecting — discard
                        try { output.Stop(); } catch { }
                        output.Dispose();
                        reader.Dispose();
                        return;
                    }
                    _reader         = reader;
                    _output         = output;
                    _boostProvider  = boostProv;
                    _eq             = eq;
                    _reverb         = reverb;
                    _isPlaying      = true;
                }
                output.Play();
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    if (_currentUrl == streamUrl) // still relevant
                    {
                        _lastError    = ex.Message;
                        _isPlaying    = false;
                        _playStartUtc = DateTime.MinValue;
                        DisposePlaybackLocked();
                    }
                }
            }
        });
    }

    public void Stop()
    {
        lock (_lock)
        {
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = null;
            StopLocked();
        }
    }

    private void StopLocked()
    {
        _isPlaying     = false;
        _currentUrl    = string.Empty;
        _streamTitle   = string.Empty;
        _playStartUtc  = DateTime.MinValue;
        _lastSampleUtc = DateTime.MinValue;
        DisposePlaybackLocked();
    }

    public void SetVolume(double volume)
    {
        lock (_lock)
        {
            _volume = Math.Clamp(volume, 0.0, 1.0);
            if (_boostProvider != null) _boostProvider.Volume = (float)(_volume * _boost);
        }
    }

    public void SetBoost(double boost)
    {
        lock (_lock)
        {
            _boost = Math.Clamp(boost, 1.0, 3.0);
            if (_boostProvider != null) _boostProvider.Volume = (float)(_volume * _boost);
        }
    }

    public void SetReverbPreset(int preset)
    {
        lock (_lock)
        {
            _reverbPreset = Math.Clamp(preset, 0, ReverbSampleProvider.PresetNames.Length - 1);
            _reverb?.ApplyPreset(_reverbPreset);
        }
    }

    public void SetEqPreset(int preset)
    {
        lock (_lock)
        {
            _eqPreset = Math.Clamp(preset, -1, EqualizerSampleProvider.PresetCount - 1);
            _eq?.ApplyPreset(_eqPreset);
        }
    }

    /// <summary>
    /// </summary>
    public void SetStreamTitle(string title)
    {
        lock (_lock) _streamTitle = title ?? string.Empty;
    }

    /// <summary>Call periodically when audio data is confirmed flowing.</summary>
    public void NotifyAudioAlive()
    {
        lock (_lock) { if (_isPlaying) _lastSampleUtc = DateTime.UtcNow; }
    }

    public RadioStatus GetStatus()
    {
        lock (_lock)
        {
            var elapsed = _isPlaying && _playStartUtc != DateTime.MinValue
                ? (int)(DateTime.UtcNow - _playStartUtc).TotalSeconds
                : 0;
            // Stalled = playing but no audio-alive ping for > 8 s
            var stalled = _isPlaying
                && _lastSampleUtc != DateTime.MinValue
                && (DateTime.UtcNow - _lastSampleUtc).TotalSeconds > 8;
            return new RadioStatus(
                IsPlaying:   _isPlaying,
                StationUrl:  _currentUrl,
                StationName: _currentName,
                StreamTitle: _streamTitle,
                Volume:      _volume,
                Boost:       _boost,
                ElapsedSeconds: elapsed,
                IsStalled:   stalled,
                Error:       _lastError,
                EqPreset:    _eqPreset,
                ReverbPreset: _reverbPreset);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (_lock)
        {
            if (e.Exception != null) _lastError = e.Exception.Message;
            _isPlaying     = false;
            _currentUrl    = string.Empty;
            _playStartUtc  = DateTime.MinValue;
            _lastSampleUtc = DateTime.MinValue;
            DisposePlaybackLocked();
        }
    }

    private void DisposePlaybackLocked()
    {
        try { _output?.Stop(); } catch { }
        _output?.Dispose();
        _reader?.Dispose();
        _output = null;
        _reader = null;
        _boostProvider = null;
        _eq = null;
        _reverb = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = null;
            DisposePlaybackLocked();
        }
    }
}

internal sealed record RadioStatus(
    bool   IsPlaying,
    string StationUrl,
    string StationName,
    string StreamTitle,
    double Volume,
    double Boost,
    int    ElapsedSeconds,
    bool   IsStalled,
    string Error,
    int    EqPreset,
    int    ReverbPreset);
