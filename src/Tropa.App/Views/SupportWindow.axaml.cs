using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Tropa.App.ViewModels;

namespace Tropa.App.Views;

/// <summary>
/// Окно «Для поддержки» (ADR-072): журнал с фильтрами, проверка домена и отчёт. Отчёт уходит
/// из программы только по явному действию пользователя (docs/02-security.md, правило 7).
/// </summary>
internal sealed partial class SupportWindow : Window
{
    private readonly DispatcherTimer _logTimer;

    public SupportWindow()
    {
        InitializeComponent();
        // Журнал в открытом окне обновляется сам, раз в две секунды.
        _logTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            if (DataContext is DiagnosticsViewModel vm)
                vm.RefreshLogCommand.Execute(null);
        });
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is DiagnosticsViewModel vm)
        {
            vm.RefreshLogCommand.Execute(null);
            if (!vm.ReportOpen)
                vm.OpenReportCommand.Execute(null);
        }

        _logTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _logTimer.Stop();
        base.OnClosed(e);
    }

    private async void OnCopyReport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsViewModel { ReportText: { } text } && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            ReportNote.Text = "Отчёт скопирован.";
        }
    }

    private async void OnSaveReport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not DiagnosticsViewModel { ReportText: { } text })
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
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
