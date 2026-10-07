using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Tropa.App.ViewModels;

namespace Tropa.App.Views;

internal sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>true — окно закрывается по-настоящему (выход из трея), иначе прячется в трей.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && !e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.V || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;
        // В поле ввода Ctrl+V работает как обычно.
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;
        if (DataContext is MainWindowViewModel vm && Clipboard is { } clipboard)
        {
            e.Handled = true;
            vm.OnPaste(await clipboard.TryGetTextAsync());
        }
    }

    private void OnInfoBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Info.IsOpen = false;
    }

    private void OnInfoCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}
