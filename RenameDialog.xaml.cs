using System.Windows;
using System.Windows.Input;

namespace RemotePlay;

public partial class RenameDialog : Window
{
    public string NewName => NameBox.Text;

    public RenameDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            DialogResult = true;
    }
}
