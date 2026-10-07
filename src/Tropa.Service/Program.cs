using System.Diagnostics;
using Microsoft.Extensions.Hosting.WindowsServices;
using Tropa.Infrastructure.SystemIntegration;
using Tropa.Ipc;
using Tropa.Service;

// Командная строка службы:
//   (без аргументов)  — работа: служба Windows или, при запуске вручную, консольный режим разработки;
//   --install         — установить службу (нужны права администратора);
//   --uninstall       — откатить изменения и удалить службу;
//   --rollback-all    — вернуть системные настройки, изменённые службой (для деинсталлятора и аварий).
if (args.Contains("--rollback-all"))
{
    var o = ServiceOptions.Production();
    if (o.DnsRegistry is { } registry)
        new DnsClientPolicy(registry, new ChangeJournal(o.JournalPath)).Restore();
    Console.WriteLine("Системные настройки, изменённые службой Тропы, восстановлены.");
    return 0;
}

if (args.Contains("--install") || args.Contains("--uninstall"))
    return ServiceInstaller.Run(args.Contains("--install"));

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = IpcProtocol.ServiceName);
builder.Services.AddSingleton(ServiceOptions.Production());
builder.Services.AddHostedService<PipeServer>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
return 0;

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
            var code = Sc("create", IpcProtocol.ServiceName, "binPath=", $"\"{exe}\"", "start=", "delayed-auto", "obj=", "LocalSystem", "DisplayName=", "Тропа");
            if (code != 0)
                return code;
            Sc("description", IpcProtocol.ServiceName, "Тропа: ядро прокси, режим «Весь компьютер», системные настройки сети.");
            // Перезапуск службы при сбое: через 5 с, 10 с, затем 60 с.
            Sc("failure", IpcProtocol.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/60000");
            return Sc("start", IpcProtocol.ServiceName);
        }

        Sc("stop", IpcProtocol.ServiceName);
        using (var rollback = Process.Start(new ProcessStartInfo(exe, "--rollback-all") { UseShellExecute = false }))
            rollback?.WaitForExit(30_000);
        return Sc("delete", IpcProtocol.ServiceName);
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
