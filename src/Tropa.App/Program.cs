using Avalonia;

namespace Tropa.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Один экземпляр на сеанс пользователя: два экземпляра боролись бы за прокси Windows и порты.
        using var single = new Mutex(initiallyOwned: true, @"Local\Tropa.App.SingleInstance", out var createdNew);
        if (!createdNew)
            return 0;

        App.StartMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Используется и дизайнером Avalonia.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
