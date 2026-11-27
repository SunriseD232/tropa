using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Tropa.App.ViewModels;

namespace Tropa.App.Views;

internal sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainWindowViewModel? _vm;

    // Вид окна (ADR-051): живёт только один из двух, чтобы скрытый не держал свои экраны и подписки.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelChanged;
        if (_vm is not null)
            _vm.Diagnostics.SupportRequested -= OnSupportRequested;
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.Diagnostics.SupportRequested += OnSupportRequested;
        }
        ApplyLayout();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsV2rayLayout))
            ApplyLayout();
    }

    private void ApplyLayout()
    {
        var v2ray = _vm?.IsV2rayLayout == true;
        if (LayoutHost.Content is Control current && current is V2rayLayoutView == v2ray)
            return;
        LayoutHost.Content = v2ray ? new V2rayLayoutView() : new TropaLayoutView();
    }

    private SupportWindow? _support;

    // «Для поддержки…» (ADR-072): одно окно журнала и отчёта рядом с главным.
    private void OnSupportRequested(object? sender, EventArgs e)
    {
        if (_vm is null)
            return;
        if (_support is { } open)
        {
            open.Activate();
            return;
        }

        _support = new SupportWindow { DataContext = _vm.Diagnostics };
        _support.Closed += (_, _) => _support = null;
        _support.Show(this);
    }

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
