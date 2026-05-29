using NAudio.Wave;
using System.Runtime.CompilerServices;

namespace RemotePlay.Services.Audio;

/// <summary>
/// 10-band peaking equalizer sample provider for NAudio.
/// Uses the same centre frequencies as the VLC built-in equalizer
/// (60 Hz, 170 Hz, 310 Hz, 600 Hz, 1 kHz, 3 kHz, 6 kHz, 12 kHz, 14 kHz, 16 kHz)
/// so VLC EQ preset gain tables can be reused directly.
/// </summary>
internal sealed class EqualizerSampleProvider : ISampleProvider
{
    // ── VLC preset gain tables (dB, 10 bands) ─────────────────────────────────
    // Order matches VLC preset indices 0-17 (same as _eqPresetGains in app-playback.js)
    private static readonly float[][] PresetGains =
    [
        [ 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],          // 0  Flat
        [ 4, 4, 4, 0, 0, 0, 0, 0,-4,-4],           // 1  Classical
        [ 0, 0, 8, 5, 5, 5, 3, 0, 0, 0],           // 2  Club
        [ 8, 6, 5, 0,-2,-3, 6, 9,10, 8],           // 3  Dance
        [10, 9, 8, 1,-2,-4,-4,-2, 0, 0],           // 4  Full Bass
        [ 7, 5, 0,-6,-4, 2, 8,11,12,12],           // 5  Full Bass & Treble
        [-8,-6,-4,-3, 0, 7, 9,10,11,12],           // 6  Full Treble
        [ 5, 4, 1, 0,-2, 3, 5, 9,10, 9],           // 7  Headphones
        [12, 8, 5, 0, 0,-4,-7,-8,-7,-7],           // 8  Large Hall
        [-4,-3,-1, 3, 4, 3, 0,-2,-2,-2],           // 9  Live
        [ 5, 5, 0, 0, 0, 0, 5, 5, 5, 5],           // 10 Party
        [-2,-1, 0, 2, 4, 4, 1, 0, 0, 0],           // 11 Pop
        [ 0, 0, 0,-2,-5, 0, 6, 6, 6, 2],           // 12 Reggae
        [ 8, 5,-5,-8,-3, 4, 8, 8, 5, 2],           // 13 Rock
        [-3,-1, 3, 5, 3,-1,-3,-3,-3,-3],           // 14 Ska
        [-2,-2, 0, 2, 3, 3, 2, 0, 0, 0],           // 15 Soft
        [ 4, 4, 2, 0,-4,-3, 0, 2, 8, 9],           // 16 Soft Rock
        [ 8, 5, 0,-6,-4, 4, 9, 9, 8, 7]            // 17 Techno
    ];

    private static readonly float[] CenterFreqs = [60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000];
    private const float BandwidthOctaves = 1.0f; // Q-equivalent width per band

    /// <summary>Number of named presets (0 to PresetCount-1 are valid; -1 = bypass).</summary>
    public static int PresetCount => PresetGains.Length;

    private readonly ISampleProvider _source;
    private readonly object _lock = new();
    private BiQuadFilter[]? _filters; // null = bypass (flat / off)
    private int _preset = -1;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public EqualizerSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    /// <summary>
    /// Apply preset index (0-17).  Pass -1 to bypass EQ entirely.
    /// Thread-safe; can be called while audio is playing.
    /// </summary>
    public void ApplyPreset(int presetIndex)
    {
        lock (_lock)
        {
            _preset = presetIndex;
            if (presetIndex < 0 || presetIndex >= PresetGains.Length)
            {
                _filters = null;
                return;
            }

            var gains = PresetGains[presetIndex];
            int sampleRate = _source.WaveFormat.SampleRate;
            int channels   = _source.WaveFormat.Channels;
            int total      = CenterFreqs.Length * channels;
            var filters    = new BiQuadFilter[total];

            for (int band = 0; band < CenterFreqs.Length; band++)
            {
                float fc = CenterFreqs[band];
                float dbGain = gains[band];
                for (int ch = 0; ch < channels; ch++)
                    filters[band * channels + ch] = BiQuadFilter.PeakingEQ(sampleRate, fc, BandwidthOctaves, dbGain);
            }
            _filters = filters;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);

        BiQuadFilter[]? filters;
        lock (_lock) filters = _filters;
        if (filters is null) return read;  // bypass

        int channels = _source.WaveFormat.Channels;
        int end      = offset + read;
        for (int i = offset; i < end; i++)
        {
            int ch = (i - offset) % channels;
            // Apply all bands in series for this channel.
            float s = buffer[i];
            for (int band = 0; band < CenterFreqs.Length; band++)
                s = filters[band * channels + ch].Transform(s);
            buffer[i] = s;
        }
        return read;
    }

    /// <summary>Minimal second-order peaking EQ BiQuad filter (direct form I).</summary>
    internal sealed class BiQuadFilter
    {
        private readonly float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        private BiQuadFilter(float b0, float b1, float b2, float a0, float a1, float a2)
        {
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
            _a1 = a1 / a0; _a2 = a2 / a0;
        }

        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="centreFreq">Centre frequency in Hz.</param>
        /// <param name="bandwidthOctaves">Bandwidth in octaves (controls Q).</param>
        /// <param name="dbGain">Boost/cut in dB.</param>
        public static BiQuadFilter PeakingEQ(int sampleRate, float centreFreq, float bandwidthOctaves, float dbGain)
        {
            float A  = MathF.Pow(10f, dbGain / 40f);
            float w0 = 2f * MathF.PI * centreFreq / sampleRate;
            float sinW0 = MathF.Sin(w0);
            float cosW0 = MathF.Cos(w0);
            // α = sin(w0)*sinh( ln(2)/2 * BW * w0/sin(w0) )
            float alpha = sinW0 * MathF.Sinh(MathF.Log(2f) / 2f * bandwidthOctaves * w0 / sinW0);

            float b0 =  1f + alpha * A;
            float b1 = -2f * cosW0;
            float b2 =  1f - alpha * A;
            float a0 =  1f + alpha / A;
            float a1 = -2f * cosW0;
            float a2 =  1f - alpha / A;
            return new BiQuadFilter(b0, b1, b2, a0, a1, a2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Transform(float input)
        {
            float output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = input;
            _y2 = _y1; _y1 = output;
            return output;
        }
    }
}
