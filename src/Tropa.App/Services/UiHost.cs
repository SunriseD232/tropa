using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Tropa.App.Services;

/// <summary>
/// Буфер обмена, диалоги файлов и окно для моделей экранов. Файлы выбирает только пользователь
/// (docs/02-security.md, §3.7), секреты в буфер — только по кнопке и ненадолго (§3.4).
/// </summary>
internal static class UiHost
{
    /// <summary>Через сколько стирать из буфера скопированный секрет, если он там ещё лежит.</summary>
    public static readonly TimeSpan SecretClipboardLifetime = TimeSpan.FromSeconds(60);

    // Окно Тропы не регистрируется как MainWindow жизненного цикла (оно живёт само, прячется в трей),
    // поэтому берём его у App: иначе буфер обмена и диалоги не находили окна и молча не работали.
    private static Window? MainWindow => App.MainWindowInstance
        ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>Кладёт секрет (ссылку сервера) в буфер и через 60 с стирает, если пользователь не скопировал ничего другого.</summary>
    public static async Task<bool> CopySecretAsync(string text)
    {
        if (MainWindow?.Clipboard is not { } clipboard)
            return false;
        await clipboard.SetTextAsync(text);
        _ = ClearLaterAsync(clipboard, text);
        return true;
    }

    private static async Task ClearLaterAsync(IClipboard clipboard, string text)
    {
        await Task.Delay(SecretClipboardLifetime);
        try
        {
            if (await clipboard.TryGetTextAsync() == text)
                await clipboard.ClearAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Буфер занят другой программой — оставить как есть лучше, чем уронить Тропу.
        }
    }

    /// <summary>Сохраняет текст в файл, который пользователь выбрал в диалоге. null — отменил.</summary>
    public static async Task<string?> SaveTextAsync(string title, string suggestedName, string text)
    {
        if (MainWindow?.StorageProvider is not { } storage)
            return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file is null)
            return null;
        await using (var stream = await file.OpenWriteAsync())
        await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            await writer.WriteAsync(text);
        return file.Name;
    }

    /// <summary>Картинка, которую пользователь выбрал в диалоге. null — отменил или файл не открылся.</summary>
    public static async Task<Bitmap?> PickImageAsync()
    {
        if (MainWindow?.StorageProvider is not { } storage)
            return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Картинка с QR-кодом",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Картинки") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"] }],
        });
        if (files.Count == 0)
            return null;
        // Огромный файл — не скриншот с QR; не читаем его в память целиком.
        if ((await files[0].GetBasicPropertiesAsync()).Size is > 30L * 1024 * 1024)
            throw new InvalidDataException("Файл больше 30 МБ — это не похоже на картинку с QR-кодом.");
        await using var stream = await files[0].OpenReadAsync();
        return new Bitmap(stream);
    }

    /// <summary>Снимок экрана для поиска QR: окно Тропы на это время прячется, чтобы не закрывать код.</summary>
    public static async Task<ScreenCapture.Shot?> CaptureScreenAsync()
    {
        var window = MainWindow;
        var state = window?.WindowState;
        // Сворачиваем окно, чтобы оно не закрывало QR-код, и разворачиваем обратно после снимка.
        if (window is not null)
            window.WindowState = WindowState.Minimized;
        try
        {
            // Даём Windows доиграть анимацию и дорисовать то, что было под окном.
            await Task.Delay(800);
            return await Task.Run(ScreenCapture.CaptureVirtualScreen);
        }
        finally
        {
            if (window is not null)
            {
                window.WindowState = state is null or WindowState.Minimized ? WindowState.Normal : state.Value;
                window.Activate();
            }
        }
    }
}
