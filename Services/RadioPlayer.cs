using NAudio.Wave;

namespace RemotePlay.Services;

internal sealed class RadioPlayer : IDisposable
{
    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private MediaFoundationReader? _reader;
    private string _currentUrl  = string.Empty;
    private string _currentName = string.Empty;
    private bool   _isPlaying;
    private string _lastError = string.Empty;
    private int    _deviceNumber = -1;
    private double _volume = 0.8;

    // Reuse the same device enumeration helpers as MusicPlayer.
    public static IReadOnlyList<(int DeviceNumber, string Name)> EnumerateDevices() =>
        MusicPlayer.EnumerateDevices();

    public static int ResolveDeviceNumber(string deviceId) =>
        MusicPlayer.ResolveDeviceNumber(deviceId);

    public void SetDevice(string deviceId)
    {
        lock (_lock) _deviceNumber = ResolveDeviceNumber(deviceId);
    }

    // ── Playback ─────────────────────────────────────────────────────────────

    public void Play(string streamUrl, string stationName)
    {
        lock (_lock)
        {
            StopLocked();
            _lastError = string.Empty;
            try
            {
                _reader = new MediaFoundationReader(streamUrl);
                _output = new WaveOutEvent { DeviceNumber = _deviceNumber, Volume = (float)_volume };
                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Init(_reader);
                _output.Play();
                _currentUrl  = streamUrl;
                _currentName = stationName;
                _isPlaying   = true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isPlaying = false;
                DisposePlaybackLocked();
            }
        }
    }

    public void Stop()
    {
        lock (_lock) StopLocked();
    }

    private void StopLocked()
    {
        _isPlaying  = false;
        _currentUrl = string.Empty;
        DisposePlaybackLocked();
    }

    public void SetVolume(double volume)
    {
        lock (_lock)
        {
            _volume = Math.Clamp(volume, 0.0, 1.0);
            if (_output != null) _output.Volume = (float)_volume;
        }
    }

    public RadioStatus GetStatus()
    {
        lock (_lock)
        {
            return new RadioStatus(
                IsPlaying:   _isPlaying,
                StationUrl:  _currentUrl,
                StationName: _currentName,
                Volume:      _volume,
                Error:       _lastError);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (_lock)
        {
            if (e.Exception != null) _lastError = e.Exception.Message;
            _isPlaying  = false;
            _currentUrl = string.Empty;
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
    }

    public void Dispose()
    {
        lock (_lock) DisposePlaybackLocked();
    }
}

internal sealed record RadioStatus(
    bool   IsPlaying,
    string StationUrl,
    string StationName,
    double Volume,
    string Error);
