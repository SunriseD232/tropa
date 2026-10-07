using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
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

internal sealed partial class RulesView : UserControl
{
    public RulesView() => InitializeComponent();
}

internal sealed partial class EditServerView : UserControl
{
    public EditServerView() => InitializeComponent();
}

internal sealed partial class PlaceholderView : UserControl
{
    public PlaceholderView() => InitializeComponent();
}

internal sealed partial class DiagnosticsView : UserControl
{
    public DiagnosticsView() => InitializeComponent();

    // Отчёт уходит из программы только по явному действию пользователя (docs/02-security.md, правило 7).
    private async void OnCopyReport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsViewModel { ReportText: { } text } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            ReportNote.Text = "Отчёт скопирован.";
        }
    }

    private async void OnSaveReport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not DiagnosticsViewModel { ReportText: { } text } || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить отчёт диагностики",
            SuggestedFileName = $"tropa-report-{DateTime.Now:yyyyMMdd-HHmm}.md",
            DefaultExtension = "md",
            FileTypeChoices = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }],
        });
        if (file is null)
            return;
        await using (var stream = await file.OpenWriteAsync())
        await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            await writer.WriteAsync(text);
        ReportNote.Text = "Отчёт сохранён: " + file.Name;
    }
}

internal sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.ExportRequested -= OnExport;
                vm.ImportRequested -= OnImport;
                vm.ExportRequested += OnExport;
                vm.ImportRequested += OnImport;
            }
        };
    }

    // Файл выбирает только пользователь в диалоге (docs/02-security.md, §3.7).
    private async void OnExport(object? sender, EventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить настройки Тропы",
            SuggestedFileName = $"tropa-settings-{DateTime.Now:yyyyMMdd}.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Настройки Тропы") { Patterns = ["*.json"] }],
        });
        if (file is null)
            return;
        await using (var stream = await file.OpenWriteAsync())
        await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            await writer.WriteAsync(vm.ExportText());
        vm.ReportExported(file.Name);
    }

    private async void OnImport(object? sender, EventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Загрузить настройки Тропы",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Настройки Тропы") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0)
            return;
        await using var stream = await files[0].OpenReadAsync();
        if (stream.CanSeek && stream.Length > 2 * 1024 * 1024)
        {
            vm.ImportText("");
            return;
        }

        using var reader = new StreamReader(stream);
        vm.ImportText(await reader.ReadToEndAsync());
    }
}
