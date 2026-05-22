using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace RemotePlay.UI;

/// <summary>
/// Parses structured log lines and produces colored WPF <see cref="Paragraph"/> instances
/// for display in the rich log viewer.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class LogViewerHelper
{
    // ── Log level ordering ────────────────────────────────────────────────────
    public enum LogLevel { Error = 0, Warn = 1, Info = 2, Detail = 3, All = 99 }

    // ── Level colors ──────────────────────────────────────────────────────────
    private static readonly SolidColorBrush BrError  = Brush("#FF6B6B");
    private static readonly SolidColorBrush BrWarn   = Brush("#FFAA55");
    private static readonly SolidColorBrush BrInfo   = Brush("#63CFFF");
    private static readonly SolidColorBrush BrDetail = Brush("#AAAACC");
    private static readonly SolidColorBrush BrDebug  = Brush("#666688");
    private static readonly SolidColorBrush BrMeta   = Brush("#555577");  // timestamp / source
    private static readonly SolidColorBrush BrText   = Brush("#CCCCCC");  // message body

    // ── Domain accent colors ─────────────────────────────────────────────────
    private static readonly SolidColorBrush BrVideo  = Brush("#E94560");
    private static readonly SolidColorBrush BrMusic  = Brush("#00D4AA");
    private static readonly SolidColorBrush BrRadio  = Brush("#AA88FF");

    // Matches lines written by Logger.Write:
    //   2025-06-15 14:03:22 [INFO] [Category] Message text
    private static readonly Regex LineRegex = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) \[(?<lvl>[A-Z]+)\] \[(?<cat>[^\]]+)\] (?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Domain keywords to colorize inside the message
    private static readonly (string word, SolidColorBrush color)[] DomainKeywords =
    [
        ("Video",  BrVideo),
        ("Music",  BrMusic),
        ("Radio",  BrRadio),
    ];

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Returns true if the line's level is visible at the given filter.</summary>
    public static bool IsVisible(string rawLine, LogLevel filter)
        => GetLineLevel(rawLine) <= filter;

    /// <summary>Builds a WPF <see cref="Paragraph"/> for one log line.</summary>
    public static Paragraph BuildParagraph(string rawLine)
    {
        var para = new Paragraph { Margin = new Thickness(0), LineHeight = 17 };

        var m = LineRegex.Match(rawLine);
        if (!m.Success)
        {
            // Unstructured line (stack traces, etc.) — render muted
            para.Inlines.Add(new Run(rawLine) { Foreground = BrMeta });
            return para;
        }

        var ts  = m.Groups["ts"].Value;
        var lvl = m.Groups["lvl"].Value;
        var cat = m.Groups["cat"].Value;
        var msg = m.Groups["msg"].Value;

        var levelBrush = LevelBrush(lvl);

        // [LEVEL] badge — bold, colored
        para.Inlines.Add(new Run($"[{lvl,-6}]") { Foreground = levelBrush, FontWeight = FontWeights.SemiBold });
        para.Inlines.Add(new Run(" "));

        // timestamp — muted
        para.Inlines.Add(new Run(ts) { Foreground = BrMeta });

        // [Source] — only when not "General"
        if (!string.Equals(cat, "General", StringComparison.OrdinalIgnoreCase))
        {
            para.Inlines.Add(new Run(" "));
            para.Inlines.Add(new Run($"[{cat}]") { Foreground = BrMeta });
        }

        para.Inlines.Add(new Run("  "));

        // Message body — with domain-word coloring
        AppendMessageInlines(para, msg, levelBrush);

        return para;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static void AppendMessageInlines(Paragraph para, string msg, SolidColorBrush baseBrush)
    {
        // Split message around domain keywords and color each segment accordingly
        var pos = 0;
        while (pos < msg.Length)
        {
            // Find the earliest domain keyword occurrence
            int earliest = msg.Length;
            (string word, SolidColorBrush color) found = default;

            foreach (var kw in DomainKeywords)
            {
                var idx = msg.IndexOf(kw.word, pos, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && idx < earliest)
                {
                    earliest = idx;
                    found    = kw;
                }
            }

            if (earliest == msg.Length)
            {
                // No more domain keywords — emit rest of message
                para.Inlines.Add(new Run(msg[pos..]) { Foreground = baseBrush });
                break;
            }

            // Text before keyword
            if (earliest > pos)
                para.Inlines.Add(new Run(msg[pos..earliest]) { Foreground = baseBrush });

            // The keyword itself — colored + bold
            para.Inlines.Add(new Run(msg.Substring(earliest, found.word.Length))
            {
                Foreground = found.color,
                FontWeight = FontWeights.SemiBold,
            });

            pos = earliest + found.word.Length;
        }
    }

    /// <summary>Parses the level tag from a raw log line.</summary>
    public static LogLevel GetLineLevel(string rawLine)
    {
        var m = LineRegex.Match(rawLine);
        if (!m.Success) return LogLevel.Detail; // treat unstructured as detail

        return m.Groups["lvl"].Value switch
        {
            "ERROR" => LogLevel.Error,
            "WARN"  => LogLevel.Warn,
            "INFO"  => LogLevel.Info,
            "DETAIL"=> LogLevel.Detail,
            _       => LogLevel.All,
        };
    }

    /// <summary>Infers the log level for an unstructured <c>AppendLog</c> message.</summary>
    public static LogLevel InferLevel(string message)
    {
        if (message.StartsWith("[CRASH]", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("ERROR",  StringComparison.OrdinalIgnoreCase)  ||
            message.StartsWith("MEDIA ERROR", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Error;

        if (message.StartsWith("WARN", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Warn;

        // Major server milestones that belong at Info level
        if (message.StartsWith("RemotePlay started", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Web server listening", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("ERROR starting server", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Self-test: server endpoints OK", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Settings applied", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Info;

        return LogLevel.Detail;
    }

    private static SolidColorBrush LevelBrush(string lvl) => lvl switch
    {
        "ERROR"  => BrError,
        "WARN"   => BrWarn,
        "INFO"   => BrInfo,
        "DETAIL" => BrDetail,
        _        => BrDebug,
    };

    private static SolidColorBrush Brush(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
