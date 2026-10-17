using System.Windows;
using System.Diagnostics.CodeAnalysis;
using InputKey = System.Windows.Input.Key;
using InputKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace RemotePlay;

[ExcludeFromCodeCoverage]
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

    private void OnKeyDown(object sender, InputKeyEventArgs e)
    {
        if (e.Key == InputKey.Enter)
            DialogResult = true;
    }
}
