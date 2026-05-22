using NAudio.Wave;
using System.IO;

namespace RemotePlay.Services;

internal sealed class MusicPlayer : IDisposable
{
    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private string _currentPath = string.Empty;
    private bool _isPaused;
    private bool _isPlaying;
    private string _lastError = string.Empty;
    private int _deviceNumber = -1;
    private double _volume = 0.8;
    private double _boost  = 1.0;

    public static IReadOnlyList<(int DeviceNumber, string Name)> EnumerateDevices()
    {
        var list = new List<(int, string)> { (-1, "(Default)") };
        int count = WaveOut.DeviceCount;
        for (int i = 0; i < count; i++) { var c = WaveOut.GetCapabilities(i); list.Add((i, c.ProductName)); }
        return list;
    }

    public static int ResolveDeviceNumber(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return -1;
        int count = WaveOut.DeviceCount;
        for (int i = 0; i < count; i++) { var c = WaveOut.GetCapabilities(i); if (c.ProductName.Contains(deviceId, StringComparison.OrdinalIgnoreCase)) return i; }
        return -1;
    }

    public void SetDevice(string deviceId) { lock (_lock) _deviceNumber = ResolveDeviceNumber(deviceId); }

    public void Play(string filePath, double startPositionSeconds = 0)
    {
        lock (_lock)
        {
            DisposePlayback();
            _lastError = string.Empty; _isPaused = false; _isPlaying = false; _currentPath = filePath;
            try
            {
                _reader = new AudioFileReader(filePath) { Volume = (float)(_volume * _boost) };
                if (startPositionSeconds > 0)
                {
                    var target = TimeSpan.FromSeconds(startPositionSeconds);
                    if (target < _reader.TotalTime) _reader.CurrentTime = target;
                }
                _output = new WaveOutEvent { DeviceNumber = _deviceNumber };
                _output.Init(_reader);
                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Play();
                _isPlaying = true;
            }
            catch (Exception ex) { _lastError = ex.Message; _currentPath = string.Empty; DisposePlayback(); }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_output is null) return;
            if (_isPaused) { _output.Play(); _isPaused = false; } else { _output.Pause(); _isPaused = true; }
        }
    }

    public void Stop() { lock (_lock) { DisposePlayback(); } }

    public MusicStatus GetStatus()
    {
        lock (_lock)
        {
            double pos = _reader?.CurrentTime.TotalSeconds ?? 0;
            double dur = _reader?.TotalTime.TotalSeconds ?? 0;
            return new MusicStatus(_isPlaying && !_isPaused, _isPaused, _currentPath,
                string.IsNullOrEmpty(_currentPath) ? string.Empty : Path.GetFileNameWithoutExtension(_currentPath),
                pos, dur, _lastError);
        }
    }

    public void SetVolume(double volume)
    {
        lock (_lock) { _volume = Math.Clamp(volume, 0, 1); if (_reader is not null) _reader.Volume = (float)(_volume * _boost); }
    }

    public void SetBoost(double boost)
    {
        lock (_lock) { _boost = Math.Clamp(boost, 0, 4); if (_reader is not null) _reader.Volume = (float)(_volume * _boost); }
    }

    public void Seek(double seconds)
    {
        lock (_lock)
        {
            if (_reader is null || _output is null) return;
            // Reposition the reader directly – no Stop()/Play() needed.
            // WaveOutEvent feeds audio by calling Read() on the reader; the next
            // buffer fill will start from the new CurrentTime.
            // Calling Stop() here would fire PlaybackStopped on the Windows callback
            // thread (asynchronously), which we cannot reliably suppress.
            _reader.CurrentTime = TimeSpan.FromSeconds(
                Math.Clamp(seconds, 0, _reader.TotalTime.TotalSeconds - 0.01));
            // If for some reason the output stalled, kick it back to playing.
            if (_isPlaying && !_isPaused && _output.PlaybackState != PlaybackState.Playing)
                _output.Play();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (_lock)
        {
            if (e.Exception is not null) _lastError = e.Exception.Message;
            if (_isPlaying && !_isPaused) { _isPlaying = false; _currentPath = string.Empty; }
        }
    }

    private void DisposePlayback()
    {
        _isPlaying = false; _isPaused = false; _currentPath = string.Empty;
        if (_output is not null) { _output.PlaybackStopped -= OnPlaybackStopped; _output.Stop(); _output.Dispose(); _output = null; }
        if (_reader is not null) { _reader.Dispose(); _reader = null; }
    }

    public void Dispose() { lock (_lock) { DisposePlayback(); } }
}

internal sealed record MusicStatus(bool IsPlaying, bool IsPaused, string CurrentPath, string Title, double Position, double Duration, string LastError);