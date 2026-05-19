using System.Windows;

namespace RemotePlay;

/// <summary>Dark-themed modal dialog that replaces <see cref="MessageBox"/> for Links-tab operations.
/// Always stays on top of its owner window.</summary>
public partial class DarkMessageBox : Window
{
    private bool _confirmed;

    private DarkMessageBox()
    {
        InitializeComponent();
    }

    /// <summary>Shows an informational message. The dialog stays on top of <paramref name="owner"/>.</summary>
    public static void Show(string message, string title, Window owner)
    {
        var dlg = new DarkMessageBox
        {
            Owner = owner
        };
        dlg.TitleText.Text   = title;
        dlg.MessageText.Text = message;
        dlg.CancelButton.Visibility = Visibility.Collapsed;
        dlg.ShowDialog();
    }

    /// <summary>Shows a confirmation dialog with OK / Cancel buttons.
    /// Returns <c>true</c> when the user clicks OK.</summary>
    public static bool Confirm(string message, string title, Window owner)
    {
        var dlg = new DarkMessageBox
        {
            Owner = owner
        };
        dlg.TitleText.Text   = title;
        dlg.MessageText.Text = message;
        dlg.CancelButton.Visibility = Visibility.Visible;
        dlg.ShowDialog();
        return dlg._confirmed;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _confirmed = false;
        Close();
    }
}
