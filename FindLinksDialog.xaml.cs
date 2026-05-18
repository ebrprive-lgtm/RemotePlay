using System.IO;
using System.Windows;

namespace RemotePlay;

public partial class FindLinksDialog : Window
{
    private sealed record LinkResult(string Name, string Folder, string FullPath);

    private readonly string _targetPath;
    private readonly string _libraryRoot;

    public FindLinksDialog(string targetPath, string libraryRoot)
    {
        InitializeComponent();
        _targetPath  = targetPath;
        _libraryRoot = libraryRoot;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        TargetLabel.Text = _targetPath;
        StatusText.Text  = "Searching…";
        Spinner.Visibility = Visibility.Visible;

        var hits = await Task.Run(() =>
            Directory
                .EnumerateFiles(_libraryRoot, "*" + RplinkHelper.Extension, SearchOption.AllDirectories)
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
                .ToList());

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
            ResultsList.ItemsSource = hits;
            ResultsList.Visibility = Visibility.Visible;
        }
    }
}
