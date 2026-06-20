using NAudio.Wave;

namespace RemotePlay.Services.Audio;

internal enum VocalFilterMode
{
    Off = 0,
    Mild = 1,
    Strong = 2,
    Karaoke = 3
}

internal sealed class VocalRemovalSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly object _lock = new();
    private VocalFilterMode _mode;

    public VocalRemovalSampleProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.WaveFormat.Channels != 2)
            throw new ArgumentException("VocalRemovalSampleProvider requires stereo input.", nameof(source));

        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public VocalFilterMode Mode
    {
        get { lock (_lock) return _mode; }
        set { lock (_lock) _mode = value; }
    }

    public bool Enabled
    {
        get { lock (_lock) return _mode != VocalFilterMode.Off; }
        set { lock (_lock) _mode = value ? VocalFilterMode.Strong : VocalFilterMode.Off; }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);

        VocalFilterMode mode;
        lock (_lock) mode = _mode;
        if (mode == VocalFilterMode.Off)
            return read;

        var end = offset + read;
        for (var i = offset; i + 1 < end; i += 2)
        {
            var l = buffer[i];
            var r = buffer[i + 1];
            var side = l - r;
            var sideInv = r - l;

            var mix = mode switch
            {
                VocalFilterMode.Mild => 0.55f,
                VocalFilterMode.Strong => 1.0f,
                VocalFilterMode.Karaoke => 0.85f,
                _ => 0f
            };

            var outL = (l * (1f - mix)) + (side * mix);
            var outR = (r * (1f - mix)) + (sideInv * mix);

            if (mode == VocalFilterMode.Karaoke)
            {
                outL *= 1.12f;
                outR *= 1.12f;
            }

            buffer[i] = Math.Clamp(outL, -1f, 1f);
            buffer[i + 1] = Math.Clamp(outR, -1f, 1f);
        }

        return read;
    }
}
