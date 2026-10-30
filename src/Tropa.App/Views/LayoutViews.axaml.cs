using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Tropa.App.ViewModels;

namespace Tropa.App.Views;

/// <summary>Вид «Тропа»: боковое меню и экраны.</summary>
internal sealed partial class TropaLayoutView : UserControl
{
    public TropaLayoutView() => InitializeComponent();
}

/// <summary>Вид «как в v2rayN» (ADR-051): только разметка поверх тех же моделей.</summary>
internal sealed partial class V2rayLayoutView : UserControl
{
    private const double TableMinWidth = 960;

    public V2rayLayoutView() => InitializeComponent();

    // Колонки со звёздочкой делят ширину окна; уже минимума — таблица прокручивается вбок, а не сжимает имена до нуля.
    private void OnTableScrollSizeChanged(object? sender, SizeChangedEventArgs e) =>
        Table.Width = Math.Max(TableMinWidth, e.NewSize.Width);

    // Двойной щелчок по строке — сделать сервер активным, как в v2rayN.
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Servers.UseSelectedCommand.Execute(null);
    }

    // Буфер читается только по явному действию — пункту меню.
    private async void OnPasteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            vm.OnPaste(await clipboard.TryGetTextAsync());
    }

    private void OnRowsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is ListBox list)
            vm.Servers.SetPicked(list.SelectedItems?.OfType<ServerItemViewModel>() ?? []);
    }

    // QR из меню «Серверы»: открываем окно «Добавить серверы» и сразу ищем код.
    private async void OnScanScreenClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Import.Open();
            await vm.Import.ScanScreenCommand.ExecuteAsync(null);
        }
    }

    private async void OnScanImageClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Import.Open();
            await vm.Import.ScanImageCommand.ExecuteAsync(null);
        }
    }

    // Новые строки журнала видны сразу: прокрутка к концу.
    private void OnLogTextChanged(object? sender, TextChangedEventArgs e) =>
        LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
}
