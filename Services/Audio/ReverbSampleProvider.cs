using NAudio.Wave;

namespace RemotePlay.Services.Audio;

/// <summary>
/// Stereo room effect inserted into a NAudio sample chain.
/// </summary>
internal sealed class ReverbSampleProvider : ISampleProvider
{
    public static readonly string[] PresetNames =
        ["Off", "Booth", "Small Room", "Medium Room", "Large Room", "Hall", "Cathedral",
         "Arena", "Cavern", "Cave", "Underwater", "Pipe"];

    private static readonly RoomPreset[] Presets =
    [
        new(0f,    0f,    0f,    0f,    0f,    0f),    // Off
        new(0.10f, 0.18f, 0.22f, 0.18f, 0.10f, 0.45f), // Booth
        new(0.16f, 0.26f, 0.34f, 0.25f, 0.18f, 0.50f), // Small Room
        new(0.24f, 0.34f, 0.48f, 0.32f, 0.24f, 0.55f), // Medium Room
        new(0.34f, 0.42f, 0.62f, 0.40f, 0.32f, 0.58f), // Large Room
        new(0.48f, 0.50f, 0.78f, 0.48f, 0.42f, 0.60f), // Hall
        new(0.65f, 0.54f, 0.90f, 0.55f, 0.52f, 0.60f), // Cathedral
        new(0.78f, 0.56f, 0.92f, 0.58f, 0.56f, 0.62f), // Arena
        new(0.85f, 0.56f, 0.88f, 0.62f, 0.60f, 0.60f), // Cavern
        new(0.90f, 0.56f, 0.84f, 0.65f, 0.62f, 0.60f), // Cave
        new(0.95f, 0.15f, 0.88f, 0.55f, 0.72f, 0.60f), // Underwater
        new(0.55f, 0.08f, 0.80f, 0.50f, 0.20f, 0.50f), // Pipe
    ];

    private readonly ISampleProvider _source;
    private readonly float[] _delayL;
    private readonly float[] _delayR;
    private readonly int _sampleRate;
    private int _writeIndex;
    private int _tap1;
    private int _tap2;
    private int _tap3;
    private int _tap4;
    private float _wet;
    private float _feedback;
    private float _width;
    private float _damping;
    private float _cross;
    private float _filterL;
    private float _filterR;

    public ReverbSampleProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.WaveFormat.Channels != 2)
        {
            throw new ArgumentException("ReverbSampleProvider requires stereo input.", nameof(source));
        }

        _source = source;
        _sampleRate = source.WaveFormat.SampleRate;
        int maxDelaySamples = Math.Max(_sampleRate * 2, 1);
        _delayL = new float[maxDelaySamples];
        _delayR = new float[maxDelaySamples];
        ApplyPreset(0);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Applies one of the named presets (0 = Off).</summary>
    public void ApplyPreset(int index)
    {
        RoomPreset preset = Presets[Math.Clamp(index, 0, Presets.Length - 1)];
        _wet = preset.Wet;
        _feedback = preset.Feedback;
        _width = preset.Width;
        _damping = preset.Damping;
        _cross = preset.Cross;

        float room = Math.Clamp(preset.RoomSize, 0f, 1f);
        _tap1 = MsToSamples(35f + 45f * room);
        _tap2 = MsToSamples(79f + 110f * room);
        _tap3 = MsToSamples(143f + 210f * room);
        _tap4 = MsToSamples(221f + 430f * room);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (_wet <= 0f)
        {
            return read;
        }

        int end = offset + read;
        for (int n = offset; n + 1 < end; n += 2)
        {
            float inL = buffer[n];
            float inR = buffer[n + 1];

            float earlyL = ReadDelay(_delayL, _tap1) * 0.60f + ReadDelay(_delayR, _tap2) * 0.40f;
            float earlyR = ReadDelay(_delayR, _tap1) * 0.60f + ReadDelay(_delayL, _tap2) * 0.40f;
            float lateL = ReadDelay(_delayL, _tap3) * 0.50f + ReadDelay(_delayR, _tap4) * 0.50f;
            float lateR = ReadDelay(_delayR, _tap3) * 0.50f + ReadDelay(_delayL, _tap4) * 0.50f;

            float reverbL = earlyL + lateL;
            float reverbR = earlyR + lateR;

            _filterL += (reverbL - _filterL) * (1f - _damping);
            _filterR += (reverbR - _filterR) * (1f - _damping);

            float wetL = _filterL * (0.55f + _width * 0.45f) + _filterR * ((1f - _width) * 0.35f);
            float wetR = _filterR * (0.55f + _width * 0.45f) + _filterL * ((1f - _width) * 0.35f);

            buffer[n] = SoftClip(inL + wetL * _wet);
            buffer[n + 1] = SoftClip(inR + wetR * _wet);

            float monoInput = (inL + inR) * 0.5f;
            _delayL[_writeIndex] = SoftClip(monoInput * (1f - _cross) + inL * _cross + _filterL * _feedback);
            _delayR[_writeIndex] = SoftClip(monoInput * (1f - _cross) + inR * _cross + _filterR * _feedback);

            if (++_writeIndex >= _delayL.Length)
            {
                _writeIndex = 0;
            }
        }

        return read;
    }

    private int MsToSamples(float ms)
        => Math.Clamp((int)(_sampleRate * ms / 1000f), 1, _delayL.Length - 1);

    private float ReadDelay(float[] delay, int samples)
    {
        int index = _writeIndex - samples;
        if (index < 0)
        {
            index += delay.Length;
        }

        return delay[index];
    }

    private static float SoftClip(float value)
        => MathF.Tanh(value);

    private readonly record struct RoomPreset(float RoomSize, float Feedback, float Wet, float Damping, float Width, float Cross);
}
