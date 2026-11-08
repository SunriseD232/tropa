using System.Diagnostics;
using Microsoft.Extensions.Hosting.WindowsServices;
using Tropa.Infrastructure.SystemIntegration;
using Tropa.Ipc;
using Tropa.Service;

// Командная строка службы:
//   (без аргументов)  — работа: служба Windows или, при запуске вручную, консольный режим разработки;
//   --install         — установить службу (нужны права администратора);
//   --uninstall       — откатить изменения и удалить службу;
//   --rollback-all    — вернуть системные настройки, изменённые службой (для деинсталлятора и аварий);
//                       с --data <каталог> — службы портативной Тропы;
//   --portable --pipe <имя> --parent <pid> --data <каталог>
//                     — служба портативной Тропы (ADR-063): потомок Tropa.exe с правами администратора,
//                       живёт, пока жив родитель.
var portableData = ArgValue(args, "--data");
if (args.Contains("--rollback-all"))
{
    var o = portableData is null ? ServiceOptions.Production() : ServiceOptions.Portable("unused", portableData, 0);
    if (o.DnsRegistry is { } registry)
        new DnsClientPolicy(registry, new ChangeJournal(o.JournalPath)).Restore();
    if (o.LoopbackStore is { } loopback)
        new LoopbackExemption(loopback, new ChangeJournal(o.JournalPath)).Restore();
    // Фильтры kill switch откатывать не нужно: они исчезли вместе с процессом службы (ADR-021).
    Console.WriteLine("Системные настройки, изменённые службой Тропы, восстановлены.");
    return 0;
}

if (args.Contains("--install") || args.Contains("--uninstall"))
    return ServiceInstaller.Run(args.Contains("--install"));

if (args.Contains("--portable"))
{
    var pipe = ArgValue(args, "--pipe");
    if (pipe is null || portableData is null || !int.TryParse(ArgValue(args, "--parent"), out var parent)
        || !pipe.StartsWith(PortableParent.PipePrefix, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Портативный режим: нужны --pipe, --parent и --data.");
        return 2;
    }

    if (!ServiceOptions.IsPrivileged())
    {
        Console.Error.WriteLine("Портативной службе нужны права администратора.");
        return 5;
    }

    // Родитель — Tropa.exe из этой же папки; иначе службу запустил кто-то другой.
    if (PortableParent.PathOf(parent) is not { } parentPath
        || !string.Equals(parentPath, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Tropa.exe")), StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Портативную службу запускает только Tropa.exe из её папки.");
        return 3;
    }

    var portable = Host.CreateApplicationBuilder([]);
    portable.Services.AddSingleton(ServiceOptions.Portable(pipe, portableData, parent));
    portable.Services.AddSingleton(new PortableParent(parent));
    portable.Services.AddHostedService<PipeServer>();
    portable.Services.AddHostedService<PortableParentWatcher>();
    await portable.Build().RunAsync().ConfigureAwait(false);
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = IpcProtocol.ServiceName);
builder.Services.AddSingleton(ServiceOptions.Production());
builder.Services.AddHostedService<PipeServer>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
return 0;

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

internal static class ServiceInstaller
{
    public static int Run(bool install)
    {
        if (!ServiceOptions.IsPrivileged())
        {
            Console.Error.WriteLine("Нужны права администратора: запустите от имени администратора.");
            return 5;
        }

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к службе.");
        if (install)
        {
            if (!exe.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))
                Console.Error.WriteLine("Внимание: служба ставится не из Program Files. Каталог должен быть доступен на запись только администраторам.");
            // Обычный автозапуск, а не отложенный: отложенная служба поднимается через пару минут после входа,
            // и Тропа из автозагрузки успевала подключиться в «Только браузеры».
            var code = Sc("create", IpcProtocol.ServiceName, "binPath=", $"\"{exe}\"", "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "Тропа");
            // 1073 — служба уже есть (обновление): переводим старую отложенную на обычный автозапуск.
            if (code == 1073)
                code = Sc("config", IpcProtocol.ServiceName, "binPath=", $"\"{exe}\"", "start=", "auto");
            if (code != 0)
                return code;
            Sc("description", IpcProtocol.ServiceName, "Тропа: ядро прокси, режим «Весь компьютер», системные настройки сети.");
            // Перезапуск службы при сбое: через 5 с, 10 с, затем 60 с.
            Sc("failure", IpcProtocol.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/60000");
            return Sc("start", IpcProtocol.ServiceName);
        }

        StopAndWait();
        using (var rollback = Process.Start(new ProcessStartInfo(exe, "--rollback-all") { UseShellExecute = false }))
            rollback?.WaitForExit(30_000);
        return Sc("delete", IpcProtocol.ServiceName);
    }

    /// <summary>Останавливает службу и ждёт остановки: иначе установщик не сможет заменить её файлы.</summary>
    private static void StopAndWait()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(IpcProtocol.ServiceName);
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            // Службы нет или она не остановилась — дальше sc delete пометит её на удаление.
        }
    }

    private static int Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
