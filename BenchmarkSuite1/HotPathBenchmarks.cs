using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;
using Microsoft.VSDiagnostics;

namespace RemotePlay.Benchmarks;
[ShortRunJob]
[CPUUsageDiagnoser]
public class HotPathBenchmarks
{
    // ── test data ─────────────────────────────────────────────────────────────
    private static readonly string[] FileNames = GenerateFileNames(500);
    private static readonly string[] Extensions = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".flv"];
    private static readonly string[] AllExtensionsSample = GenerateExtensionSample(1000);
    // thumb cache key helpers
    private const string SamplePath = @"C:\Movies\Season 1\Episode 01 - Pilot.mkv";
    private const int ThumbSize = 320;
    // pre-compute stable values so benchmarks only measure the hash/FileInfo cost
    private static readonly long _fileLength = 1_234_567_890L;
    private static readonly long _lastWriteTicks = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly string _normalizedPath = Path.GetFullPath(SamplePath);
    // ── NaturalStringComparer: Before — new uncompiled Regex per Compare ─────
    [Benchmark(Baseline = true, Description = "NaturalSort_Before (uncompiled Regex, new instance/request)")]
    public void NaturalSort_Before()
    {
        var comparer = new NaturalStringComparerBefore();
        var copy = FileNames.ToArray();
        Array.Sort(copy, comparer.Compare);
    }

    // ── NaturalStringComparer: After — compiled Regex, static instance ────────
    private static readonly NaturalStringComparerAfter _afterComparer = new();
    [Benchmark(Description = "NaturalSort_After (compiled Regex, static instance)")]
    public void NaturalSort_After()
    {
        var copy = FileNames.ToArray();
        Array.Sort(copy, _afterComparer.Compare);
    }

    // ── Extension lookup: Before — string[] + ToLowerInvariant ───────────────
    private static readonly string[] ExtensionArray = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".flv"];
    [Benchmark(Description = "ExtLookup_Before (string[] + ToLowerInvariant)")]
    public int ExtLookup_Before()
    {
        int hits = 0;
        foreach (var ext in AllExtensionsSample)
            if (ExtensionArray.Contains(ext.ToLowerInvariant()))
                hits++;
        return hits;
    }

    // ── Extension lookup: After — HashSet<string>(OrdinalIgnoreCase) ─────────
    private static readonly HashSet<string> ExtensionSet = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mkv",
        ".avi",
        ".mov",
        ".wmv",
        ".m4v",
        ".ts",
        ".flv"
    };
    [Benchmark(Description = "ExtLookup_After (HashSet OrdinalIgnoreCase)")]
    public int ExtLookup_After()
    {
        int hits = 0;
        foreach (var ext in AllExtensionsSample)
            if (ExtensionSet.Contains(ext))
                hits++;
        return hits;
    }

    // ── Thumbnail cache key: Before — new FileInfo + Encoding.UTF8.GetBytes ──
    [Benchmark(Description = "ThumbCacheKey_Before (new FileInfo + Encoding.GetBytes)")]
    public string ThumbCacheKey_Before()
    {
        // Original pattern: reads fileInfo fields every time
        var key = $"{_normalizedPath}|{_fileLength}|{_lastWriteTicks}|{ThumbSize}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return hash + ".jpg";
    }

    // ── Thumbnail cache key: After — stackalloc UTF-8 encode + SHA256 ─────────
    [Benchmark(Description = "ThumbCacheKey_After (stackalloc UTF-8 + SHA256.TryHashData)")]
    public string ThumbCacheKey_After()
    {
        // Use GetByteCount + stackalloc to avoid the intermediate byte[] heap allocation
        var key = $"{_normalizedPath}|{_fileLength}|{_lastWriteTicks}|{ThumbSize}";
        var byteCount = Encoding.UTF8.GetByteCount(key);
        Span<byte> keyBytes = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(key, keyBytes);
        Span<byte> hashBytes = stackalloc byte[32];
        SHA256.TryHashData(keyBytes, hashBytes, out _);
        return Convert.ToHexString(hashBytes) + ".jpg";
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private static string[] GenerateFileNames(int count)
    {
        var rng = new Random(42);
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            var season = rng.Next(1, 10);
            var episode = rng.Next(1, 30);
            names[i] = $"Show Name S{season:D2}E{episode:D2} - Episode Title {rng.Next(1, 999)}.mkv";
        }

        return names;
    }

    private static string[] GenerateExtensionSample(int count)
    {
        string[] pool = [".mp4", ".mkv", ".avi", ".jpg", ".png", ".txt", ".srt", ".sub"];
        var rng = new Random(42);
        var result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = pool[rng.Next(pool.Length)];
        return result;
    }

    // ── inline before/after NaturalStringComparer implementations ─────────────
    private sealed class NaturalStringComparerBefore : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x == null && y == null)
                return 0;
            if (x == null)
                return -1;
            if (y == null)
                return 1;
            // Original: uncompiled Regex created anew on every Compare call
            var xParts = Regex.Split(x, @"(\d+)");
            var yParts = Regex.Split(y, @"(\d+)");
            var maxLen = Math.Max(xParts.Length, yParts.Length);
            for (int i = 0; i < maxLen; i++)
            {
                var xPart = i < xParts.Length ? xParts[i] : string.Empty;
                var yPart = i < yParts.Length ? yParts[i] : string.Empty;
                if (string.IsNullOrEmpty(xPart) && string.IsNullOrEmpty(yPart))
                    continue;
                if (string.IsNullOrEmpty(xPart))
                    return -1;
                if (string.IsNullOrEmpty(yPart))
                    return 1;
                if (int.TryParse(xPart, out var xNum) && int.TryParse(yPart, out var yNum))
                {
                    var numCmp = xNum.CompareTo(yNum);
                    if (numCmp != 0)
                        return numCmp;
                }
                else
                {
                    var strCmp = string.Compare(xPart, yPart, StringComparison.OrdinalIgnoreCase);
                    if (strCmp != 0)
                        return strCmp;
                }
            }

            return 0;
        }
    }

    private sealed class NaturalStringComparerAfter : IComparer<string>
    {
        // Compiled once at class load
        private static readonly Regex DigitSplitRegex = new(@"(\d+)", RegexOptions.Compiled);
        public int Compare(string? x, string? y)
        {
            if (x == null && y == null)
                return 0;
            if (x == null)
                return -1;
            if (y == null)
                return 1;
            var xParts = DigitSplitRegex.Split(x);
            var yParts = DigitSplitRegex.Split(y);
            var maxLen = Math.Max(xParts.Length, yParts.Length);
            for (int i = 0; i < maxLen; i++)
            {
                var xPart = i < xParts.Length ? xParts[i] : string.Empty;
                var yPart = i < yParts.Length ? yParts[i] : string.Empty;
                if (string.IsNullOrEmpty(xPart) && string.IsNullOrEmpty(yPart))
                    continue;
                if (string.IsNullOrEmpty(xPart))
                    return -1;
                if (string.IsNullOrEmpty(yPart))
                    return 1;
                if (int.TryParse(xPart, out var xNum) && int.TryParse(yPart, out var yNum))
                {
                    var numCmp = xNum.CompareTo(yNum);
                    if (numCmp != 0)
                        return numCmp;
                }
                else
                {
                    var strCmp = string.Compare(xPart, yPart, StringComparison.OrdinalIgnoreCase);
                    if (strCmp != 0)
                        return strCmp;
                }
            }

            return 0;
        }
    }
}