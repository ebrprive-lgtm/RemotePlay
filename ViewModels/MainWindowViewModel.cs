using System.Windows.Input;
using RemotePlay.Mvvm;

namespace RemotePlay.ViewModels;

internal sealed class MainWindowViewModel : ViewModelBase
{
    private string _statusText = "Ready";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public ICommand ClearStatusCommand { get; }

    public MainWindowViewModel()
    {
        ClearStatusCommand = new RelayCommand(() => StatusText = "Ready");
    }

    public void SetStatus(string message)
    {
        StatusText = string.IsNullOrWhiteSpace(message) ? "Ready" : message;
    }
}
