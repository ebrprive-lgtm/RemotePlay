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
    private readonly float[] _preDelayL;
    private readonly float[] _preDelayR;
    private readonly int _sampleRate;
    private int _writeIndex;
    private int _preDelayWriteIndex;
    private int _tap1;
    private int _tap2;
    private int _tap3;
    private int _tap4;
    private float _wet;
    private float _feedback;
    private float _width;
    private float _damping;
    private float _cross;
    private float _strength = 1f;
    private float _dry = 1f;
    private int _preDelaySamples;
    private float _outputTrim = 1f;
    private bool _bypass;
    private float _presetWet;
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
        int maxPreDelaySamples = Math.Max((int)(_sampleRate * 0.2f), 1);
        _preDelayL = new float[maxPreDelaySamples];
        _preDelayR = new float[maxPreDelaySamples];
        ApplyPreset(0);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Applies one of the named presets (0 = Off).</summary>
    public void ApplyPreset(int index)
    {
        RoomPreset preset = Presets[Math.Clamp(index, 0, Presets.Length - 1)];
        _presetWet = preset.Wet;
        _wet = _presetWet * _strength;
        _feedback = preset.Feedback;
        _width = preset.Width;
        _damping = preset.Damping;
        _cross = preset.Cross;

        float room = Math.Clamp(preset.RoomSize, 0f, 1f);
        _tap1 = MsToSamples(35f + 45f * room);
        _tap2 = MsToSamples(79f + 110f * room);
        _tap3 = MsToSamples(143f + 210f * room);
        _tap4 = MsToSamples(221f + 430f * room);
        UpdateOutputTrim();
    }

    /// <summary>Sets wet blend strength in range [0..2], preserving the selected preset shape.</summary>
    public void SetStrength(double strength)
    {
        _strength = (float)Math.Clamp(strength, 0d, 2d);
        _wet = _presetWet * _strength;
        UpdateOutputTrim();
    }

    /// <summary>Sets dry signal amount in range [0..1].</summary>
    public void SetDryLevel(double dry)
    {
        _dry = (float)Math.Clamp(dry, 0d, 1d);
        UpdateOutputTrim();
    }

    /// <summary>Sets input pre-delay in milliseconds [0..120].</summary>
    public void SetPreDelayMs(double ms)
    {
        var clampedMs = Math.Clamp(ms, 0d, 120d);
        _preDelaySamples = Math.Clamp((int)(_sampleRate * clampedMs / 1000d), 0, _preDelayL.Length - 1);
    }

    /// <summary>Enables/disables reverb path while preserving configured parameters.</summary>
    public void SetBypass(bool bypass)
    {
        _bypass = bypass;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (_bypass)
        {
            return read;
        }

        int end = offset + read;
        for (int n = offset; n + 1 < end; n += 2)
        {
            float inL = buffer[n];
            float inR = buffer[n + 1];

            float procInL = inL;
            float procInR = inR;
            if (_preDelaySamples > 0)
            {
                int preDelayReadIndex = _preDelayWriteIndex - _preDelaySamples;
                if (preDelayReadIndex < 0)
                {
                    preDelayReadIndex += _preDelayL.Length;
                }

                procInL = _preDelayL[preDelayReadIndex];
                procInR = _preDelayR[preDelayReadIndex];
                _preDelayL[_preDelayWriteIndex] = inL;
                _preDelayR[_preDelayWriteIndex] = inR;

                if (++_preDelayWriteIndex >= _preDelayL.Length)
                {
                    _preDelayWriteIndex = 0;
                }
            }

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

            float mixedL = (inL * _dry) + (wetL * _wet);
            float mixedR = (inR * _dry) + (wetR * _wet);
            buffer[n] = SoftClip(mixedL * _outputTrim);
            buffer[n + 1] = SoftClip(mixedR * _outputTrim);

            float monoInput = (procInL + procInR) * 0.5f;
            _delayL[_writeIndex] = SoftClip(monoInput * (1f - _cross) + procInL * _cross + _filterL * _feedback);
            _delayR[_writeIndex] = SoftClip(monoInput * (1f - _cross) + procInR * _cross + _filterR * _feedback);

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

    private void UpdateOutputTrim()
    {
        float energy = (_wet * 0.55f) + (_dry * 0.9f);
        _outputTrim = Math.Clamp(1f / MathF.Max(0.7f, energy), 0.55f, 1f);
    }

    private readonly record struct RoomPreset(float RoomSize, float Feedback, float Wet, float Damping, float Width, float Cross);
}
