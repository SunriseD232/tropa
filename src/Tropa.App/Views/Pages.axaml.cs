using Avalonia.Controls;
using Avalonia.Input;
using Tropa.App.ViewModels;

namespace Tropa.App.Views;

internal sealed partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();
}

internal sealed partial class ServersView : UserControl
{
    public ServersView() => InitializeComponent();

    // Двойной щелчок по серверу — сделать его текущим.
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ServersViewModel vm)
            vm.UseSelectedCommand.Execute(null);
    }
}

internal sealed partial class PlaceholderView : UserControl
{
    public PlaceholderView() => InitializeComponent();
}
