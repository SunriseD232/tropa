using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Infrastructure;

namespace Tropa.App.ViewModels;

/// <summary>Окно «Добавить серверы»: подписка по URL или ссылки vless:// / vmess:// / trojan://.</summary>
internal sealed partial class ImportViewModel(TropaEngine engine, InfoViewModel info) : ObservableObject
{
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LooksLikeSubscription))]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Result { get; set; }

    [ObservableProperty]
    public partial bool HasResult { get; set; }

    public bool LooksLikeSubscription => TropaEngine.IsSubscriptionUrl(Text.Trim());

    public bool IsHttp => Text.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    public void Open(string? text = null)
    {
        Text = text?.Trim() ?? "";
        Name = "";
        Result = null;
        HasResult = false;
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void ShowInfo(string key) => info.Show(key);

    /// <summary>QR-код с экрана (ADR-053): окно прячется, снимок только в памяти, найденное — в поле, как вставленное руками.</summary>
    [RelayCommand]
    private async Task ScanScreenAsync()
    {
        IsBusy = true;
        try
        {
            var shot = await Services.UiHost.CaptureScreenAsync();
            Found(shot is null ? [] : await Task.Run(() => Services.QrCodes.Decode(shot.Bgra, shot.Width, shot.Height)),
                "На экране не найден QR-код. Откройте его крупно (в Telegram, браузере) и нажмите ещё раз.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScanImageAsync()
    {
        IsBusy = true;
        try
        {
            using var image = await Services.UiHost.PickImageAsync();
            if (image is null)
                return;
            Found(Services.QrCodes.Decode(image), "На картинке не найден QR-код.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Result = "Картинку не удалось открыть: " + ex.Message;
            HasResult = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Найденный текст — недоверенный: только подставляем в поле, добавляет пользователь кнопкой.
    private void Found(IReadOnlyList<string> codes, string none)
    {
        if (codes.Count == 0)
        {
            Result = none;
            HasResult = true;
            return;
        }

        Text = string.Join("\n", codes);
        Result = codes.Count == 1
            ? "QR-код распознан. Проверьте и нажмите «Добавить»."
            : $"Распознано QR-кодов: {codes.Count}. Проверьте и нажмите «Добавить».";
        HasResult = true;
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (string.IsNullOrWhiteSpace(Text))
            return;
        if (IsHttp)
        {
            Result = "Адрес подписки начинается с http:// — данные пойдут без шифрования и их может подменить провайдер. Попросите у продавца ссылку https://.";
            HasResult = true;
            return;
        }

        IsBusy = true;
        try
        {
            var report = await engine.ImportTextAsync(Text, string.IsNullOrWhiteSpace(Name) ? null : Name);
            var head = report.Added + report.Updated > 0
                ? $"Готово: добавлено {report.Added}" + (report.Updated > 0 ? $", обновлено {report.Updated}" : "")
                : "Новых серверов не найдено.";
            if (report.Errors > 0)
                head += $". Не удалось разобрать: {report.Errors}";
            if (report.Skipped > 0)
                head += $". Пропущено неподдерживаемых: {report.Skipped}";
            Result = string.Join("\n", new[] { head + "." }.Concat(report.Messages.Take(8)));
            HasResult = true;
            if (report.Added + report.Updated > 0 && report.Errors == 0 && report.Messages.Count == 0)
                IsOpen = false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
