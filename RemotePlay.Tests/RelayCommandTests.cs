using Xunit;
using RemotePlay.Mvvm;

namespace RemotePlay.Tests;

public class RelayCommandTests
{
    [Fact]
    public void Execute_InvokesTheAction()
    {
        var executed = false;
        var command = new RelayCommand(() => executed = true);

        command.Execute(null);

        Assert.True(executed);
    }

    [Fact]
    public void CanExecute_WithNoGuard_ReturnsTrue()
    {
        var command = new RelayCommand(() => { });

        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public void CanExecute_WithGuardReturnsFalse_ReturnsFalse()
    {
        var command = new RelayCommand(() => { }, canExecute: () => false);

        Assert.False(command.CanExecute(null));
    }

    [Fact]
    public void CanExecute_WithGuardReturnsTrue_ReturnsTrue()
    {
        var command = new RelayCommand(() => { }, canExecute: () => true);

        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public void RaiseCanExecuteChanged_FiresCanExecuteChangedEvent()
    {
        var command = new RelayCommand(() => { });
        var raised = false;
        command.CanExecuteChanged += (_, _) => raised = true;

        command.RaiseCanExecuteChanged();

        Assert.True(raised);
    }
}
