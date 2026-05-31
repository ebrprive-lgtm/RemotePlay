using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace RemotePlay;

public partial class MainWindow
{
    // ── Karaoke / fullscreen synced-lyrics overlay ──────────────────────────

    /// <summary>
    /// Loads synced lyrics for the currently playing music track from the lyrics
    /// disk cache, then starts the lyric tick timer. Called from ShowIdleOverlay
    /// when music is active and we are entering fullscreen.
    /// </summary>
    private void StartKaraokeSession()
    {
        _lrcLines     = null;
        _lrcLineIndex = -1;
        HideKaraokeLines();

        var status = _musicPlayer.GetStatus();
        if (!status.IsPlaying && !status.IsPaused)
            return;

        var artist = status.Artist ?? string.Empty;
        var title  = status.Title  ?? string.Empty;
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title))
            return;

        var lines = TryLoadCachedSyncedLyrics(artist, title);
        if (lines is not null && lines.Length > 0)
        {
            _lrcLines     = lines;
            _lrcLineIndex = -1;
            KaraokeLyricsPanel.Visibility = Visibility.Visible;
        }

        _lyricsOffset = TryLoadLyricsOffset(artist, title);
        _lyricsTimer.Start();
    }

    /// <summary>Stops the karaoke timer and hides the lyrics panel.</summary>
    private void StopKaraokeSession()
    {
        _lyricsTimer.Stop();
        _lrcLines     = null;
        _lrcLineIndex = -1;
        HideKaraokeLines();
    }

    private void HideKaraokeLines()
    {
        if (FindName("KaraokeLyricsPanel") is not UIElement panel)
            return;

        KaraokeLyricsPanel.Visibility = Visibility.Collapsed;
        KaraokePrevLine.Text          = string.Empty;
        KaraokeActiveLine.Text        = string.Empty;
        KaraokeNextLine.Text          = string.Empty;
    }

    /// <summary>
    /// Called every 250 ms by _lyricsTimer. Updates the three lyric TextBlocks
    /// based on the current playback position.
    /// </summary>
    private void TickLyricsOverlay()
    {
        if (_lrcLines is null || _lrcLines.Length == 0)
            return;

        var pos = _musicPlayer.GetStatus().Position + _lyricsOffset;

        // Find the last line whose timestamp is <= current position
        var idx = -1;
        for (var i = 0; i < _lrcLines.Length; i++)
        {
            if (_lrcLines[i].Time <= pos)
                idx = i;
            else
                break;
        }

        if (idx == _lrcLineIndex)
            return; // no line change — skip DOM update

        _lrcLineIndex = idx;

        KaraokePrevLine.Text   = idx > 0              ? _lrcLines[idx - 1].Text : string.Empty;
        KaraokeActiveLine.Text = idx >= 0             ? _lrcLines[idx].Text     : string.Empty;
        KaraokeNextLine.Text   = idx + 1 < _lrcLines.Length ? _lrcLines[idx + 1].Text : string.Empty;
    }

    // ── Lyrics cache reader ─────────────────────────────────────────────────

    private static LrcLine[]? TryLoadCachedSyncedLyrics(string artist, string title)
    {
        try
        {
            var cacheKey  = ComputeLyricsCacheKey(artist, title);
            var cacheFile = Path.Combine(AppPaths.LyricsCacheDirectory, cacheKey + ".json");
            if (!File.Exists(cacheFile))
                return null;

            var json = File.ReadAllText(cacheFile, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("found", out var foundEl) || !foundEl.GetBoolean())
                return null;

            if (!root.TryGetProperty("syncedLyrics", out var syncedEl)
                || syncedEl.ValueKind != JsonValueKind.String)
                return null;

            var lrc = syncedEl.GetString();
            if (string.IsNullOrWhiteSpace(lrc))
                return null;

            return ParseLrc(lrc);
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeLyricsCacheKey(string artist, string title)
    {
        var raw  = $"{artist.ToLowerInvariant()}|{title.ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    // ── LRC parser ──────────────────────────────────────────────────────────

    private static readonly Regex LrcLineRegex =
        new(@"^\[(\d{2}):(\d{2}(?:\.\d+)?)\](.*)", RegexOptions.Compiled);

    private static LrcLine[] ParseLrc(string lrc)
    {
        var lines = new List<LrcLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var m = LrcLineRegex.Match(raw.Trim());
            if (!m.Success) continue;

            var time = int.Parse(m.Groups[1].Value) * 60.0
                     + double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var text = StripWordTags(m.Groups[3].Value.Trim());
            lines.Add(new LrcLine(time, text));
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return [.. lines];
    }

    // Strip per-word timestamp tags like <00:01.23> that appear in extended LRC
    private static readonly Regex WordTagRegex = new(@"<\d{2}:\d{2}\.\d+>", RegexOptions.Compiled);

    private static string StripWordTags(string text) => WordTagRegex.Replace(text, string.Empty);

    /// <summary>
    /// Reads the per-track lyrics timing offset (in seconds) from <c>lyric-offsets.json</c>.
    /// The file stores a JSON object keyed by <c>artist|title</c> (lowercase), with values in milliseconds.
    /// Returns 0 if no entry is found or the file cannot be read.
    /// </summary>
    private static double TryLoadLyricsOffset(string artist, string title)
    {
        try
        {
            var path = AppPaths.LyricOffsetsFile;
            if (!File.Exists(path))
                return 0;

            var json = File.ReadAllText(path, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var key = $"{artist.ToLowerInvariant()}|{title.ToLowerInvariant()}";
            if (doc.RootElement.TryGetProperty(key, out var val) && val.TryGetDouble(out var ms))
                return ms / 1000.0;
        }
        catch { /* ignore */ }

        return 0;
    }
}
