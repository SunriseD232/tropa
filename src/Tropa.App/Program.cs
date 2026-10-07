using Avalonia;

namespace Tropa.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // «Аварийно вернуть настройки сети» из меню «Пуск» и из деинсталлятора: без окна,
        // работает даже при испорченных настройках и при запущенном основном экземпляре.
        if (args.Contains("--emergency-rollback", StringComparer.OrdinalIgnoreCase))
            return EmergencyRollback();
        // Выбор «Запускать вместе с Windows» в установщике (он запускает Тропу от имени пользователя).
        if (args.FirstOrDefault(a => a.StartsWith("--set-autostart=", StringComparison.OrdinalIgnoreCase)) is { } autostart)
            return SetAutostart(autostart.EndsWith("=on", StringComparison.OrdinalIgnoreCase));

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
        App.FromAutostart = args.Contains(Infrastructure.SystemIntegration.Autostart.Argument, StringComparer.OrdinalIgnoreCase);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static int EmergencyRollback()
    {
        try
        {
            var engine = Infrastructure.TropaEngine.Open(Infrastructure.EnginePaths.Default(), connectService: Infrastructure.TropaEngine.DefaultServiceConnector());
            engine.EmergencyRollbackAsync().GetAwaiter().GetResult();
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            return 1;
        }
    }

    private static int SetAutostart(bool enabled)
    {
        try
        {
            var engine = Infrastructure.TropaEngine.Open(Infrastructure.EnginePaths.Default());
            engine.UpdateSettings(s => s with { General = s.General with { Autostart = enabled } });
            if (Environment.ProcessPath is { } exe && Infrastructure.SystemIntegration.Autostart.IsInstalledCopy(exe))
                new Infrastructure.SystemIntegration.Autostart(new Infrastructure.SystemIntegration.RegistryRunKey()).Sync(enabled, exe);
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            return 1;
        }
    }

    // Используется и дизайнером Avalonia.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
