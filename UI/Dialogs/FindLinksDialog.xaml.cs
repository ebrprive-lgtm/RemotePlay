using System.IO;
using System.Windows;
using System.Diagnostics.CodeAnalysis;

namespace RemotePlay;

[ExcludeFromCodeCoverage]
public partial class FindLinksDialog : Window
{
    private sealed record LinkResult(string Name, string Folder, string FullPath);

    private readonly string _targetPath;
    private readonly string _libraryRoot;
    private readonly Func<string, string[]?>? _indexLookup;

    /// <param name="indexLookup">Optional fast-path: returns .rplink paths from the in-memory index,
    /// or <c>null</c> when the index is not ready (falls back to disk scan).</param>
    public FindLinksDialog(string targetPath, string libraryRoot, Func<string, string[]?>? indexLookup = null)
    {
        InitializeComponent();
        _targetPath   = targetPath;
        _libraryRoot  = libraryRoot;
        _indexLookup  = indexLookup;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        TargetLabel.Text = _targetPath;
        StatusText.Text  = "Searching…";
        Spinner.Visibility = Visibility.Visible;

        var hits = await Task.Run(() =>
        {
            // Fast path: use the in-memory index when available.
            var indexedSources = _indexLookup?.Invoke(_targetPath);
            IEnumerable<string> candidates;

            if (indexedSources is { Length: > 0 })
            {
                candidates = indexedSources;
            }
            else
            {
                // Fallback: full disk walk (index empty or not yet built).
                candidates = Directory.EnumerateFiles(
                    _libraryRoot, "*" + RplinkHelper.Extension, SearchOption.AllDirectories);
            }

            return candidates
                .Select(f => (file: f, resolved: RplinkHelper.TryReadTarget(f)))
                .Where(t => t.resolved is not null &&
                            string.Equals(Path.GetFullPath(t.resolved),
                                          Path.GetFullPath(_targetPath),
                                          StringComparison.OrdinalIgnoreCase))
                .Select(t => new LinkResult(
                    Path.GetFileNameWithoutExtension(t.file),
                    Path.GetDirectoryName(t.file) ?? string.Empty,
                    t.file))
                .OrderBy(r => r.Folder)
                .ThenBy(r => r.Name)
                .ToList();
        });

        Spinner.Visibility = Visibility.Collapsed;

        if (hits.Count == 0)
        {
            StatusText.Text = "No links found pointing to this file.";
            StatusText.Foreground = System.Windows.Media.Brushes.DarkGray;
        }
        else
        {
            StatusText.Text = $"{hits.Count} link{(hits.Count == 1 ? "" : "s")} found:";
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            if (FindName("ResultsItems") is System.Windows.Controls.ItemsControl ic)
                ic.ItemsSource = hits;
            if (FindName("ResultsPanel") is System.Windows.Controls.Border panel)
                panel.Visibility = Visibility.Visible;
        }
    }
}
