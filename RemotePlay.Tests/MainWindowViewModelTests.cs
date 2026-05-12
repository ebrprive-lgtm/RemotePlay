using RemotePlay.ViewModels;
using Xunit;

namespace RemotePlay.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void SetStatusUsesProvidedMessage()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.SetStatus("Server started");

        Assert.Equal("Server started", viewModel.StatusText);
    }

    [Fact]
    public void SetStatusFallsBackToReadyWhenMessageIsEmpty()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.SetStatus("   ");

        Assert.Equal("Ready", viewModel.StatusText);
    }

    [Fact]
    public void ClearStatusCommandResetsStatusTextToReady()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SetStatus("Something happened");

        viewModel.ClearStatusCommand.Execute(null);

        Assert.Equal("Ready", viewModel.StatusText);
    }
}
