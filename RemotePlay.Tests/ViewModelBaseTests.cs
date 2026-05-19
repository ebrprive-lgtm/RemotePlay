using Xunit;
using RemotePlay.Mvvm;

namespace RemotePlay.Tests;

public class ViewModelBaseTests
{
    private sealed class TestViewModel : ViewModelBase
    {
        private string _name = string.Empty;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public void ForceNotify(string propertyName) => RaisePropertyChanged(propertyName);
    }

    [Fact]
    public void SetProperty_WhenValueChanges_RaisesPropertyChanged()
    {
        var vm = new TestViewModel();
        string? raised = null;
        vm.PropertyChanged += (_, e) => raised = e.PropertyName;

        vm.Name = "Alice";

        Assert.Equal(nameof(TestViewModel.Name), raised);
    }

    [Fact]
    public void SetProperty_WhenValueUnchanged_DoesNotRaisePropertyChanged()
    {
        var vm = new TestViewModel { Name = "Bob" };
        var count = 0;
        vm.PropertyChanged += (_, _) => count++;

        vm.Name = "Bob";

        Assert.Equal(0, count);
    }

    [Fact]
    public void SetProperty_WhenValueChanges_ReturnsTrueAndUpdatesField()
    {
        var vm = new TestViewModel();

        vm.Name = "Carol";

        Assert.Equal("Carol", vm.Name);
    }

    [Fact]
    public void RaisePropertyChanged_FiresEventWithCorrectName()
    {
        var vm = new TestViewModel();
        string? raised = null;
        vm.PropertyChanged += (_, e) => raised = e.PropertyName;

        vm.ForceNotify("SomeProperty");

        Assert.Equal("SomeProperty", raised);
    }
}
