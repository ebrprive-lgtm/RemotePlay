using System.Collections.ObjectModel;
using System.Windows;

namespace RemotePlay.Controls;

public sealed partial class PathListEditor : System.Windows.Controls.UserControl
{
    public event EventHandler? PathsChanged;

    public static readonly DependencyProperty PathsProperty =
        DependencyProperty.Register(
            nameof(Paths),
            typeof(ObservableCollection<string>),
            typeof(PathListEditor),
            new PropertyMetadata(null));

    public static readonly DependencyProperty BrowseTitleProperty =
        DependencyProperty.Register(
            nameof(BrowseTitle),
            typeof(string),
            typeof(PathListEditor),
            new PropertyMetadata("Select a folder"));

    public ObservableCollection<string> Paths
    {
        get => (ObservableCollection<string>)GetValue(PathsProperty);
        set => SetValue(PathsProperty, value);
    }

    public string BrowseTitle
    {
        get => (string)GetValue(BrowseTitleProperty);
        set => SetValue(BrowseTitleProperty, value);
    }

    public PathListEditor()
    {
        InitializeComponent();
        Paths = [];
    }

    private void OnAddPath(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = BrowseTitle };
        if (dialog.ShowDialog() == true)
        {
            Paths.Add(dialog.FolderName);
            OnPathsChanged();
        }
    }

    private void OnBrowsePath(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string current)
            return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = BrowseTitle,
            InitialDirectory = current
        };

        if (dialog.ShowDialog() != true)
            return;

        var index = Paths.IndexOf(current);
        if (index >= 0)
        {
            Paths[index] = dialog.FolderName;
            OnPathsChanged();
        }
    }

    private void OnRemovePath(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string path)
        {
            Paths.Remove(path);
            OnPathsChanged();
        }
    }

    private void OnPathTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb)
            return;

        var oldValue = tb.Tag as string;
        if (oldValue is null)
            return;

        var newValue = tb.Text;
        var index = Paths.IndexOf(oldValue);
        if (index >= 0 && oldValue != newValue)
        {
            Paths[index] = newValue;
            tb.Tag = newValue;
            OnPathsChanged();
        }
    }

    private void OnPathsChanged() => PathsChanged?.Invoke(this, EventArgs.Empty);
}
