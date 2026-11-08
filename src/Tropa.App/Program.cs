using Avalonia;

namespace Tropa.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // «Аварийно вернуть настройки сети» из меню «Пуск» и из деинсталлятора: без окна,
        // работает даже при испорченных настройках и при запущенном основном экземпляре.
        // Установщик добавляет --reason=update или --reason=uninstall: тогда в журнале это не авария.
        if (args.Contains("--emergency-rollback", StringComparer.OrdinalIgnoreCase))
            return EmergencyRollback(args.FirstOrDefault(a => a.StartsWith("--reason=", StringComparison.OrdinalIgnoreCase))?["--reason=".Length..].ToLowerInvariant());
        // Обновление портативной Тропы (ADR-063): эта копия — новая версия из распакованного архива.
        if (args.Length >= 3 && args[0] == Infrastructure.Updates.PortableUpdater.ApplyArgument)
            return ApplyPortableUpdate(args[1], args[2], args.Contains("--reconnect", StringComparer.Ordinal));
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
        var single = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        // Перезапуск от имени администратора (ADR-052): старый экземпляр ещё закрывается — ждём его до 20 с.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!createdNew && args.Contains(Services.Elevation.WaitInstanceArgument, StringComparer.Ordinal) && DateTime.UtcNow < deadline)
        {
            single.Dispose();
            Thread.Sleep(300);
            single = new Mutex(initiallyOwned: true, mutexName, out createdNew);
        }

        using var _ = single;
        if (!createdNew)
            return 0;

        App.StartMinimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        // Перезапуск установщиком после обновления из Тропы (installer/tropa.iss, RelaunchArgs).
        App.AfterUpdate = args.Contains("--after-update", StringComparer.OrdinalIgnoreCase);
        App.ReconnectAfterUpdate = (App.AfterUpdate && args.Contains("--reconnect", StringComparer.OrdinalIgnoreCase))
            || args.Contains(Services.Elevation.ReconnectArgument, StringComparer.Ordinal);
        App.FromAutostart = args.Contains(Infrastructure.SystemIntegration.Autostart.Argument, StringComparer.OrdinalIgnoreCase);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static int EmergencyRollback(string? reason)
    {
        try
        {
            var engine = App.OpenEngine();
            engine.EmergencyRollbackAsync(reason).GetAwaiter().GetResult();
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            return 1;
        }
    }

    /// <summary>
    /// Ждёт, пока старая портативная Тропа закроется, копирует себя (свою папку) поверх неё и запускает её.
    /// Работает только из распакованной папки обновления внутри data той же Тропы.
    /// </summary>
    private static int ApplyPortableUpdate(string target, string oldPid, bool reconnect)
    {
        var source = Path.GetFullPath(AppContext.BaseDirectory);
        target = Path.GetFullPath(target);
        var expectedStaging = Path.Combine(target, Infrastructure.Service.PortableMode.DataFolder) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(expectedStaging, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (int.TryParse(oldPid, out var pid))
        {
            try
            {
                using var old = System.Diagnostics.Process.GetProcessById(pid);
                old.WaitForExit(TimeSpan.FromSeconds(60));
            }
            catch (ArgumentException)
            {
                // Уже закрылась.
            }
        }

        var ok = true;
        try
        {
            Infrastructure.Updates.PortableUpdater.Apply(source, target, TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Infrastructure.Cores.IntegrityException)
        {
            // Не вышло — запускаем ту Тропу, что есть: без программы человек не должен остаться.
            ok = false;
        }

        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(target, "Tropa.exe")) { UseShellExecute = false, WorkingDirectory = target };
        info.ArgumentList.Add("--after-update");
        if (reconnect)
            info.ArgumentList.Add("--reconnect");
        using (System.Diagnostics.Process.Start(info))
        {
        }

        return ok ? 0 : 1;
    }

    private static int SetAutostart(bool enabled)
    {
        try
        {
            var engine = Infrastructure.TropaEngine.Open(App.DataPaths());
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
