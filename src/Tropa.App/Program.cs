using Avalonia;

namespace Tropa.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Один экземпляр на сеанс пользователя: два экземпляра боролись бы за прокси Windows и порты.
        var mutexName = @"Local\Tropa.App.SingleInstance";
#if DEBUG
        // Отладочный экземпляр с отдельным каталогом данных не должен конфликтовать с установленной Тропой.
        if (Environment.GetEnvironmentVariable("TROPA_DATA_DIR") is { Length: > 0 } dataDir)
            mutexName += "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDir)))[..12];
#endif
        using var single = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
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
