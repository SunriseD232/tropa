using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
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

    // «⋯» у строки: выделить этот сервер и открыть то же меню, что и по правому щелчку.
    private void OnRowMore(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ServerItemViewModel item } button)
            return;
        if (List.SelectedItems is { } picked && !picked.Contains(item))
        {
            picked.Clear();
            List.SelectedItem = item;
        }

        RowMenu.Open(button);
    }

    private void OnClearPicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => List.SelectedItems?.Clear();

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && List.SelectedItems is { Count: > 0 } picked)
        {
            picked.Clear();
            e.Handled = true;
        }
    }

    // Выделение нескольких серверов: модель знает, к чему применять «Удалить» и «Проверить».
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ServersViewModel vm && sender is ListBox list)
            vm.SetPicked(list.SelectedItems?.OfType<ServerItemViewModel>() ?? []);
    }

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

internal sealed partial class RoutesView : UserControl
{
    public RoutesView() => InitializeComponent();

    // Щелчок по карточке маршрута — редактор справа; щелчки по кнопкам внутри карточки не открывают его.
    private void OnRouteTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Avalonia.Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (sender is Control { DataContext: RouteCardViewModel card })
            card.EditCommand.Execute(null);
    }
}

internal sealed partial class GamesView : UserControl
{
    public GamesView() => InitializeComponent();
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
                vm.SiteFileRequested -= OnSiteFile;
                vm.CopyRequested -= OnCopy;
                vm.CopyRequested += OnCopy;
                vm.ExportRequested += OnExport;
                vm.ImportRequested += OnImport;
                vm.SiteFileRequested += OnSiteFile;
            }
        };
    }

    private async void OnCopy(object? sender, string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
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

    private async void OnSiteFile(object? sender, EventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Список сайтов",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Списки (txt, json, yaml, list)") { Patterns = ["*.txt", "*.json", "*.yaml", "*.yml", "*.list", "*.conf", "hosts"] },
                new FilePickerFileType("Все файлы") { Patterns = ["*"] },
            ],
        });
        if (files.Count == 0)
            return;
        await using var stream = await files[0].OpenReadAsync();
        if (stream.CanSeek && stream.Length > 10 * 1024 * 1024)
            return;
        using var reader = new StreamReader(stream);
        vm.SiteImportText = await reader.ReadToEndAsync();
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
