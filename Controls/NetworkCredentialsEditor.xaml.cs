using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using PasswordBox = System.Windows.Controls.PasswordBox;
using Grid = System.Windows.Controls.Grid;
using ContentPresenter = System.Windows.Controls.ContentPresenter;
using System.IO;
using System.Runtime.InteropServices;
using MessageBox = System.Windows.MessageBox;
using Border = System.Windows.Controls.Border;

namespace RemotePlay.Controls;

/// <summary>Mutable view-model row for one network share credential entry.</summary>
public sealed class NetworkShareCredentialEntry
{
    public string Path     { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed partial class NetworkCredentialsEditor : System.Windows.Controls.UserControl
{
    public event EventHandler? CredentialsChanged;

    public static readonly DependencyProperty EntriesProperty =
        DependencyProperty.Register(
            nameof(Entries),
            typeof(ObservableCollection<NetworkShareCredentialEntry>),
            typeof(NetworkCredentialsEditor),
            new PropertyMetadata(null));

    public ObservableCollection<NetworkShareCredentialEntry> Entries
    {
        get => (ObservableCollection<NetworkShareCredentialEntry>)GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }

    public NetworkCredentialsEditor()
    {
        InitializeComponent();
        Entries = [];
    }

    private void OnAddEntry(object sender, RoutedEventArgs e)
    {
        Entries.Add(new NetworkShareCredentialEntry());
        OnCredentialsChanged();
    }

    private void OnRemoveEntry(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is NetworkShareCredentialEntry entry)
        {
            Entries.Remove(entry);
            OnCredentialsChanged();
        }
    }

    private void OnPathTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is NetworkShareCredentialEntry entry && entry.Path != tb.Text)
        {
            entry.Path = tb.Text;
            OnCredentialsChanged();
        }
    }

    private void OnUsernameTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is NetworkShareCredentialEntry entry && entry.Username != tb.Text)
        {
            entry.Username = tb.Text;
            OnCredentialsChanged();
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb && pb.Tag is NetworkShareCredentialEntry entry)
        {
            entry.Password = pb.Password;
            OnCredentialsChanged();
        }
    }

    private void OnBrowsePath(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NetworkShareCredentialEntry entry)
            return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the network share root (e.g. \\\\server\\share)",
            InitialDirectory = string.IsNullOrWhiteSpace(entry.Path) ? string.Empty : entry.Path,
        };

        if (dialog.ShowDialog() != true)
            return;

        entry.Path = dialog.FolderName;

        // Reflect the new value back into the TextBox in the same row.
        if (btn.Parent is System.Windows.Controls.Grid grid)
        {
            foreach (var child in grid.Children)
            {
                if (child is TextBox tb && tb.Tag == entry)
                {
                    tb.Text = dialog.FolderName;
                    break;
                }
            }
        }

        OnCredentialsChanged();
    }

    /// <summary>
    /// Populates the editor from an existing credentials array, also restoring password values
    /// which cannot be set via data binding on <see cref="PasswordBox"/>.
    /// </summary>
    internal void LoadEntries(IEnumerable<NetworkShareCredential> credentials)
    {
        Entries.Clear();
        foreach (var c in credentials)
            Entries.Add(new NetworkShareCredentialEntry { Path = c.Path, Username = c.Username, Password = c.Password });

        // PasswordBox values must be set manually after the ItemsControl renders.
        Dispatcher.InvokeAsync(SyncPasswordBoxes, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SyncPasswordBoxes()
    {
        foreach (var item in CredentialsItemsControl.Items)
        {
            if (item is not NetworkShareCredentialEntry entry)
                continue;

            var container = CredentialsItemsControl.ItemContainerGenerator.ContainerFromItem(item) as ContentPresenter;
            if (container is null)
                continue;

            var pb = FindPasswordBox(container);
            if (pb is not null)
                pb.Password = entry.Password;
        }
    }

    private static PasswordBox? FindPasswordBox(System.Windows.DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is PasswordBox pb)
                return pb;

            var found = FindPasswordBox(child);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static T? FindVisualParent<T>(System.Windows.DependencyObject child) where T : System.Windows.DependencyObject
    {
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
        if (parent == null)
            return null;

        if (parent is T typedParent)
            return typedParent;

        return FindVisualParent<T>(parent);
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                return typedChild;

            var found = FindVisualChild<T>(child);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                yield return typedChild;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private void OnCredentialsChanged() => CredentialsChanged?.Invoke(this, EventArgs.Empty);

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NetworkShareCredentialEntry entry)
            return;

        // Find the parent container to get actual values from UI controls
        var container = FindVisualParent<Border>(btn);
        if (container == null)
            return;

        // Get current values from UI controls
        string currentPath = entry.Path;
        string currentUsername = entry.Username;
        string currentPassword = entry.Password;

        // Find the PasswordBox in the same container to get the actual current password
        var passwordBox = FindVisualChild<PasswordBox>(container);
        if (passwordBox != null)
        {
            currentPassword = passwordBox.Password;
        }

        // Find TextBoxes to get current path and username
        var textBoxes = FindVisualChildren<TextBox>(container).ToList();
        foreach (var tb in textBoxes)
        {
            if (tb.Tag == entry)
            {
                // Determine if it's path or username based on binding or order
                // Path textbox comes first, username second
                if (textBoxes.IndexOf(tb) == 0)
                    currentPath = tb.Text;
                else if (textBoxes.IndexOf(tb) == 1)
                    currentUsername = tb.Text;
            }
        }

        // Disable button during test
        btn.IsEnabled = false;
        var originalContent = btn.Content;
        btn.Content = "Testing...";

        try
        {
            var result = await TestNetworkShareAsync(currentPath, currentUsername, currentPassword);

            if (result.Success)
            {
                btn.Content = "✓ Success";
                btn.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1a4d2e"));
                btn.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4ecca3"));
                MessageBox.Show($"Successfully connected to {currentPath}", "Connection Test", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                btn.Content = "✗ Failed";
                btn.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4d1a1a"));
                btn.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#ff6b6b"));
                MessageBox.Show($"Failed to connect to {currentPath}\n\nError: {result.ErrorMessage}", "Connection Test Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // Reset button after 3 seconds
            await Task.Delay(3000);
            btn.Content = originalContent;
            btn.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1a4d2e"));
            btn.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4ecca3"));
        }
        catch (Exception ex)
        {
            btn.Content = "✗ Error";
            btn.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4d1a1a"));
            btn.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#ff6b6b"));
            MessageBox.Show($"Error testing connection: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);

            await Task.Delay(3000);
            btn.Content = originalContent;
            btn.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1a4d2e"));
            btn.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4ecca3"));
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private static async Task<(bool Success, string ErrorMessage)> TestNetworkShareAsync(string path, string username, string password)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return (false, "Network share path is empty");

                // Validate UNC path format
                if (!path.StartsWith(@"\\"))
                    return (false, "Path must be a UNC path (e.g., \\\\server\\share)");

                // Try to connect with credentials if provided
                if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
                {
                    var connectionResult = ConnectToNetworkShare(path, username, password);
                    if (!connectionResult.Success)
                        return connectionResult;
                }

                // Test if we can access the share
                if (!Directory.Exists(path))
                    return (false, "Cannot access the network share. It may not exist or you don't have permission.");

                // Try to enumerate to verify read access
                try
                {
                    Directory.GetFileSystemEntries(path, "*", new EnumerationOptions { MaxRecursionDepth = 0, RecurseSubdirectories = false });
                }
                catch (UnauthorizedAccessException)
                {
                    return (false, "Access denied. Please check your credentials.");
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string password, string username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string LocalName;
        public string RemoteName;
        public string Comment;
        public string Provider;
    }

    private static (bool Success, string ErrorMessage) ConnectToNetworkShare(string path, string username, string password)
    {
        try
        {
            var netResource = new NetResource
            {
                Scope = 2,
                Type = 1,
                DisplayType = 3,
                Usage = 1,
                LocalName = null,
                RemoteName = path,
                Comment = null,
                Provider = null
            };

            int result = WNetAddConnection2(ref netResource, password, username, 0);

            if (result == 0)
                return (true, string.Empty);

            return result switch
            {
                5 => (false, "Access denied. Invalid username or password."),
                53 => (false, "Network path not found. Check the server name."),
                86 => (false, "Invalid password."),
                1219 => (true, string.Empty), // Already connected
                1326 => (false, "Login failure. Invalid username or password."),
                _ => (false, $"Failed to connect. Error code: {result}")
            };
        }
        catch (Exception ex)
        {
            return (false, $"Connection error: {ex.Message}");
        }
    }
}
