using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RemotePlay.Services.Audio;
using System.IO;

namespace RemotePlay.Services;

internal sealed class MusicPlayer : IDisposable
{
    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private WaveStream? _reader;           // AudioFileReader or OpusFileReader
    private WaveChannel32? _volumeChannel; // wraps OpusFileReader for volume control
    private string _currentPath = string.Empty;
    private bool _isPaused;
    private bool _isPlaying;
    private string _lastError = string.Empty;
    private int _deviceNumber = -1;
    private double _volume = 0.8;
    private double _boost  = 1.0;
    private string? _nextTrackPath;
    private string _tagArtist = string.Empty;
    private string _tagTitle  = string.Empty;
    private string _tagAlbum  = string.Empty;
    private ReverbSampleProvider? _reverb;
    private EqualizerSampleProvider? _eq;
    private int _reverbPreset = 0;
    private int _eqPreset = -1;

    /// <summary>Fired (outside the lock) when the player auto-advances to the next track.</summary>
    public event Action<string>? TrackAdvanced;

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
            // Read ID3 tags once so polling GetStatus() is cheap
            _tagArtist = string.Empty;
            _tagTitle  = string.Empty;
            _tagAlbum  = string.Empty;
            try
            {
                using var tf = TagLib.File.Create(filePath);
                _tagArtist = tf.Tag.FirstPerformer ?? tf.Tag.FirstAlbumArtist ?? string.Empty;
                _tagTitle  = tf.Tag.Title ?? string.Empty;
                _tagAlbum  = tf.Tag.Album ?? string.Empty;
            }
            catch { /* ignore tag read failures */ }
            try
            {
                var ext = Path.GetExtension(filePath);
                IWaveProvider provider;
                if (ext.Equals(".opus", StringComparison.OrdinalIgnoreCase))
                {
                    var opusReader = new OpusFileReader(filePath);
                    _reader = opusReader;
                    _volumeChannel = new WaveChannel32(opusReader) { Volume = (float)(_volume * _boost) };
                    if (startPositionSeconds > 0)
                        _reader.Position = (long)(startPositionSeconds * _reader.WaveFormat.AverageBytesPerSecond);
                    provider = _volumeChannel;
                }
                else
                {
                    var afr = new AudioFileReader(filePath) { Volume = (float)(_volume * _boost) };
                    _reader = afr;
                    _volumeChannel = null;
                    if (startPositionSeconds > 0)
                    {
                        var target = TimeSpan.FromSeconds(startPositionSeconds);
                        if (target < afr.TotalTime) afr.CurrentTime = target;
                    }
                    provider = afr;
                }
                _output = new WaveOutEvent { DeviceNumber = _deviceNumber };
                // Convert to sample provider: AudioFileReader is already ISampleProvider;
                // WaveChannel32 (opus path) needs explicit float conversion.
                ISampleProvider sampleProvider = provider is ISampleProvider sp
                    ? sp
                    : new NAudio.Wave.SampleProviders.WaveToSampleProvider(provider);
                if (sampleProvider.WaveFormat.Channels == 1)
                    sampleProvider = sampleProvider.ToStereo();
                _eq = new EqualizerSampleProvider(sampleProvider);
                _eq.ApplyPreset(_eqPreset);
                _reverb = new ReverbSampleProvider(_eq);
                _reverb.ApplyPreset(_reverbPreset);
                _output.Init(_reverb);
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
            double pos = 0, dur = 0;
            if (_reader is AudioFileReader afr)
            { pos = afr.CurrentTime.TotalSeconds; dur = afr.TotalTime.TotalSeconds; }
            else if (_reader is not null && _reader.WaveFormat.AverageBytesPerSecond > 0)
            { pos = (double)_reader.Position / _reader.WaveFormat.AverageBytesPerSecond;
              dur = _reader.Length > 0 ? (double)_reader.Length / _reader.WaveFormat.AverageBytesPerSecond : 0; }
            var displayTitle = !string.IsNullOrEmpty(_tagTitle)
                ? _tagTitle
                : (string.IsNullOrEmpty(_currentPath) ? string.Empty : Path.GetFileNameWithoutExtension(_currentPath));
            // Fall back to folder-path-derived values when tags are absent
            var displayArtist = _tagArtist;
            var displayAlbum  = _tagAlbum;
            if (string.IsNullOrEmpty(displayArtist) && !string.IsNullOrEmpty(_currentPath))
            {
                var dir = Path.GetDirectoryName(_currentPath) ?? string.Empty;
                displayAlbum  = string.IsNullOrEmpty(displayAlbum)  ? Path.GetFileName(dir) : displayAlbum;
                displayArtist = Path.GetFileName(Path.GetDirectoryName(dir)) ?? string.Empty;
            }
            return new MusicStatus(_isPlaying && !_isPaused, _isPaused, _currentPath,
                displayTitle, displayArtist, displayAlbum, pos, dur, _lastError, _eqPreset, _reverbPreset);
        }
    }

    public void SetVolume(double volume)
    {
        lock (_lock)
        {
            _volume = Math.Clamp(volume, 0, 1);
            if (_reader is AudioFileReader afr) afr.Volume = (float)(_volume * _boost);
            else if (_volumeChannel is not null) _volumeChannel.Volume = (float)(_volume * _boost);
        }
    }

    public void SetBoost(double boost)
    {
        lock (_lock)
        {
            _boost = Math.Clamp(boost, 0, 4);
            if (_reader is AudioFileReader afr) afr.Volume = (float)(_volume * _boost);
            else if (_volumeChannel is not null) _volumeChannel.Volume = (float)(_volume * _boost);
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

    public void SetNextTrack(string? path)
    {
        lock (_lock) _nextTrackPath = string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public void Seek(double seconds)
    {
        lock (_lock)
        {
            if (_reader is null || _output is null) return;
            if (_reader is AudioFileReader afr)
            {
                afr.CurrentTime = TimeSpan.FromSeconds(
                    Math.Clamp(seconds, 0, afr.TotalTime.TotalSeconds - 0.01));
            }
            else
            {
                long targetByte = (long)(seconds * _reader.WaveFormat.AverageBytesPerSecond);
                _reader.Position = Math.Clamp(targetByte, 0, Math.Max(0, _reader.Length - 1));
            }
            if (_isPlaying && !_isPaused && _output.PlaybackState != PlaybackState.Playing)
                _output.Play();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        string? nextPath = null;
        lock (_lock)
        {
            if (e.Exception is not null) _lastError = e.Exception.Message;
            if (_isPlaying && !_isPaused)
            {
                // Natural end-of-track: pick up queued next track if any
                nextPath = _nextTrackPath;
                _nextTrackPath = null;
                _isPlaying = false;
                _currentPath = string.Empty;
            }
        }

        if (nextPath is not null)
        {
            Play(nextPath, 0);
            TrackAdvanced?.Invoke(nextPath);
        }
    }

    private void DisposePlayback()
    {
        _isPlaying = false; _isPaused = false; _currentPath = string.Empty;
        _tagArtist = string.Empty; _tagTitle = string.Empty; _tagAlbum = string.Empty;
        if (_output is not null) { _output.PlaybackStopped -= OnPlaybackStopped; _output.Stop(); _output.Dispose(); _output = null; }
        _reverb = null;
        _eq = null;
        if (_volumeChannel is not null) { _volumeChannel.Dispose(); _volumeChannel = null; }
        if (_reader is not null) { _reader.Dispose(); _reader = null; }
    }

    public void Dispose() { lock (_lock) { DisposePlayback(); } }
}

internal sealed record MusicStatus(bool IsPlaying, bool IsPaused, string CurrentPath, string Title, string Artist, string Album, double Position, double Duration, string LastError, int EqPreset, int ReverbPreset);