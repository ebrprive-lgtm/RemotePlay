using System.IO;
using Concentus;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;

namespace RemotePlay.Services.Audio;

/// <summary>
/// NAudio WaveStream that decodes an Opus (.opus) file using Concentus.
/// Supports position seeking by replaying from the beginning (Opus OGG containers
/// don't support random-access byte seeking without a full re-decode).
/// </summary>
internal sealed class OpusFileReader : WaveStream
{
    private readonly string   _filePath;
    private readonly WaveFormat _waveFormat;

    private FileStream           _fileStream;
    private OpusOggReadStream    _oggStream;
    private readonly byte[]      _sampleBuffer;
    private int                  _bufferOffset;
    private int                  _bufferCount;

    // Accumulated position in bytes (relative to PCM output)
    private long _position;
    private long _length; // estimated; updated as we decode

    // Decoded sample rate from the Opus file (always 48 kHz per spec)
    private const int OpusSampleRate = 48_000;
    private const int Channels       = 2; // stereo; Concentus always outputs stereo
    private const int BitsPerSample  = 16;

    public OpusFileReader(string filePath)
    {
        _filePath    = filePath;
        _waveFormat  = new WaveFormat(OpusSampleRate, BitsPerSample, Channels);
        _sampleBuffer = new byte[OpusSampleRate * Channels * (BitsPerSample / 8)]; // 1 s buffer

        OpenStream();

        // Estimate length: decode the whole file once to count frames, then reopen.
        // For large files this might be slow; skip estimation and just use 0 (unknown).
        _length = 0;
    }

    private void OpenStream()
    {
        _fileStream  = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder  = new OpusDecoder(OpusSampleRate, Channels); // Concentus.Structs.OpusDecoder
        _oggStream   = new OpusOggReadStream(decoder, _fileStream);
        _bufferOffset = 0;
        _bufferCount  = 0;
    }

    // ── WaveStream overrides ──────────────────────────────────────────────────

    public override WaveFormat WaveFormat => _waveFormat;

    public override long Length => _length > 0 ? _length : long.MaxValue;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < _position)
            {
                // Seek backwards: must reopen from start
                _oggStream.Close();
                _fileStream.Dispose();
                _position     = 0;
                _bufferOffset = 0;
                _bufferCount  = 0;
                OpenStream();
            }
            // Seek forward: skip decoded bytes
            if (value > _position)
            {
                long skip = value - _position;
                var skipBuf = new byte[4096];
                while (skip > 0)
                {
                    int toRead = (int)Math.Min(skip, skipBuf.Length);
                    int got    = Read(skipBuf, 0, toRead);
                    if (got == 0) break;
                    skip -= got;
                }
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int written = 0;

        while (written < count)
        {
            // Drain the current decoded buffer first
            if (_bufferCount > 0)
            {
                int toCopy = Math.Min(count - written, _bufferCount);
                Array.Copy(_sampleBuffer, _bufferOffset, buffer, offset + written, toCopy);
                _bufferOffset += toCopy;
                _bufferCount  -= toCopy;
                written       += toCopy;
                _position     += toCopy;
                continue;
            }

            // Decode next packet
            if (!_oggStream.HasNextPacket)
            {
                // Update length estimate now that we've reached the end
                if (_length == 0 || _length < _position)
                    _length = _position;
                break;
            }

            short[]? samples = _oggStream.DecodeNextPacket();
            if (samples == null || samples.Length == 0)
                break;

            // Convert short[] to byte[]
            int byteCount = samples.Length * 2;
            if (byteCount > _sampleBuffer.Length)
            {
                // Grow buffer if needed (shouldn't normally happen)
                var tmp = new byte[byteCount];
                Buffer.BlockCopy(samples, 0, tmp, 0, byteCount);
                Array.Copy(tmp, 0, buffer, offset + written, Math.Min(byteCount, count - written));
                int copied = Math.Min(byteCount, count - written);
                _position += copied;
                written   += copied;
                // If there was overflow, stash the rest in _sampleBuffer (or discard for simplicity)
            }
            else
            {
                Buffer.BlockCopy(samples, 0, _sampleBuffer, 0, byteCount);
                _bufferOffset = 0;
                _bufferCount  = byteCount;
            }
        }

        return written;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _oggStream?.Close();
            _fileStream?.Dispose();
        }
        base.Dispose(disposing);
    }
}
