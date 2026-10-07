using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.App.Services;

namespace Tropa.App.ViewModels;

/// <summary>Модальное окно справки по кнопке «i».</summary>
internal sealed partial class InfoViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string? What { get; set; }

    [ObservableProperty]
    public partial string? Why { get; set; }

    [ObservableProperty]
    public partial string? Dpi { get; set; }

    [ObservableProperty]
    public partial string? Risk { get; set; }

    [ObservableProperty]
    public partial string? Conf { get; set; }

    [ObservableProperty]
    public partial string? Now { get; set; }

    public void Show(string key, string? unavailableReason = null)
    {
        var a = InfoTexts.Get(key);
        if (a is null)
            return;
        Title = a.T;
        What = a.What;
        Why = a.Why;
        Dpi = a.Dpi;
        Risk = a.Risk;
        Conf = a.Conf;
        Now = unavailableReason is null ? null : "Сейчас недоступно: " + unavailableReason;
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;
}
